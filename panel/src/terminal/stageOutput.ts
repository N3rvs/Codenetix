import type {
  ReviewerKind,
  RunEvent,
  RunEventOutputAppended,
  RunEventOutputTruncated,
  RunState,
} from '../api/generated'
import type { PipelineFlowNode } from '../graph/buildPipelineGraph'

export const DEFAULT_OUTPUT_MAX_BYTES = 5 * 1024 * 1024

export type TerminalScopeMode = 'node' | 'all'

export interface TruncationInfo {
  isTruncated: boolean
  maxBytes: number
  eventSeq: number | null
}

export type TerminalTimelineItem =
  | { kind: 'output'; event: RunEventOutputAppended }
  | { kind: 'truncated'; event: RunEventOutputTruncated }

export interface StageTerminalView {
  outputEvents: RunEventOutputAppended[]
  truncated: TruncationInfo
  timelineItems: TerminalTimelineItem[]
}

export type TerminalSyncAction =
  | { kind: 'noop'; nextWrittenSeqs: number[] }
  | {
      kind: 'append'
      itemsToWrite: TerminalTimelineItem[]
      flushImmediately: boolean
      nextWrittenSeqs: number[]
    }
  | {
      kind: 'reset'
      itemsToWrite: TerminalTimelineItem[]
      nextWrittenSeqs: number[]
    }

const KNOWN_REVIEWERS: readonly ReviewerKind[] = ['security', 'quality', 'tests']

// Secuencias de control de 8 bits (C1) OSC / DCS / SOS / PM / APC y controles C1 restantes.
const C1_STRING_CONTROL_REGEX =
  /\u009d[^\x07\u009c]*(?:\x07|\u009c|$)|[\u0090\u0098\u009e\u009f][\s\S]*?(?:\u009c|$)/g
const C1_CONTROL_REGEX = /[\u0080-\u009f]/g

// Secuencias de 7 bits OSC (ESC ]) y DCS/SOS/PM/APC (ESC P/X/^/_), incluso sin terminador.
const ESC_STRING_CONTROL_REGEX =
  /\x1b\][^\x07\x1b]*(?:\x07|\x1b\\|$)|\x1b[PX^_][\s\S]*?(?:\x1b\\|$)/g

// Conserva exclusivamente códigos SGR (`ESC [ <params> m`) y elimina cualquier otro CSI o ESC.
const ESC_FILTER_REGEX = /\x1b\[([0-9;]*)m|\x1b\[[0-?]*[ -/]*[@-~]?|\x1b[ -/]*[@-~]?/g

function stateToFallbackNodeId(state: RunState): string {
  switch (state) {
    case 'Received':
      return 'received'
    case 'Analyzing':
      return 'analyzing'
    case 'Coding':
      return 'coding'
    case 'Reviewing':
      return 'review-security'
    case 'Deciding':
      return 'deciding'
    case 'PullRequestCreated':
    case 'Failed':
    case 'Cancelled':
      return 'terminal'
  }
}

function roleToNodeId(role: string): string | null {
  const normalized = role.trim().toLowerCase()
  if (normalized === 'analyst' || normalized === 'analyzing') {
    return 'analyzing'
  }
  if (normalized === 'coder' || normalized === 'coding') {
    return 'coding'
  }
  if (normalized === 'tester' || normalized === 'testing') {
    return 'testing'
  }
  for (const reviewer of KNOWN_REVIEWERS) {
    if (
      normalized === reviewer ||
      normalized === `review-${reviewer}` ||
      normalized === `reviewer:${reviewer}` ||
      normalized === `reviewer-${reviewer}`
    ) {
      return `review-${reviewer}`
    }
  }
  return null
}

function getSortedEvents(events: readonly RunEvent[]): readonly RunEvent[] {
  for (let i = 1; i < events.length; i++) {
    if (events[i].seq < events[i - 1].seq) {
      return [...events].sort((a, b) => a.seq - b.seq)
    }
  }
  return events
}

/**
 * Construye un índice determinista `stageId -> nodeId` a partir de los eventos
 * de Etapa/Revisor que declaran explícitamente su identidad de Etapa.
 */
export function buildStageIdToNodeMap(events: readonly RunEvent[]): Map<string, string> {
  const map = new Map<string, string>()

  for (const ev of getSortedEvents(events)) {
    const stageId = ev.stageId
    if (!stageId) {
      continue
    }

    switch (ev.type) {
      case 'VerdictRecorded':
      case 'ReviewerOutputRejected':
        map.set(stageId, `review-${ev.reviewer}`)
        break
      case 'SpecGenerated':
      case 'AnalystOutputRejected':
      case 'AnalysisRejected':
        map.set(stageId, 'analyzing')
        break
      case 'RunCreated':
      case 'RepositoryPrepared':
      case 'RepoConfigResolved':
        map.set(stageId, 'received')
        break
      case 'SandboxCreated':
      case 'SandboxReleased':
      case 'CodingAttemptFailed':
        map.set(stageId, 'coding')
        break
      case 'RiskClassified':
      case 'PullRequestOpened':
        map.set(stageId, 'deciding')
        break
      case 'IssueCommented':
      case 'NotificationFailed':
        map.set(stageId, 'terminal')
        break
      case 'TokensConsumed': {
        const mapped = roleToNodeId(ev.role)
        if (mapped) {
          map.set(stageId, mapped)
        }
        break
      }
      default:
        break
    }
  }

  return map
}

function resolveOutputEventNodeId(
  ev: RunEventOutputAppended | RunEventOutputTruncated,
  stageIdToNode: ReadonlyMap<string, string>,
  activeState: RunState,
): string {
  if (ev.stageId) {
    const correlated = stageIdToNode.get(ev.stageId)
    if (correlated) {
      return correlated
    }
  }
  return stateToFallbackNodeId(activeState)
}

/**
 * Extrae la lista ordenada de `OutputAppended`, los elementos intercalados (`timelineItems`)
 * y el estado de `OutputTruncated` para el nodo seleccionado o para toda la Ejecución.
 * Cuando se indica `runId`, filtra defensivamente los eventos pertenecientes a esa Ejecución.
 */
export function extractStageTerminalView(
  events: readonly RunEvent[],
  selectedNodeId: string | null,
  scopeMode: TerminalScopeMode,
  runId?: string,
): StageTerminalView {
  const scoped = runId ? events.filter((e) => e.runId === runId) : events
  const sorted = getSortedEvents(scoped)
  const stageIdToNode = buildStageIdToNodeMap(sorted)

  const outputEvents: RunEventOutputAppended[] = []
  const timelineItems: TerminalTimelineItem[] = []
  let truncated: TruncationInfo = {
    isTruncated: false,
    maxBytes: DEFAULT_OUTPUT_MAX_BYTES,
    eventSeq: null,
  }

  let activeState: RunState = 'Received'

  for (const ev of sorted) {
    if (ev.type === 'StateChanged') {
      activeState = ev.to
      continue
    }

    if (ev.type === 'OutputAppended') {
      if (
        scopeMode === 'all' ||
        (selectedNodeId !== null && resolveOutputEventNodeId(ev, stageIdToNode, activeState) === selectedNodeId)
      ) {
        outputEvents.push(ev)
        timelineItems.push({ kind: 'output', event: ev })
      }
      continue
    }

    if (ev.type === 'OutputTruncated') {
      const matchesScope =
        scopeMode === 'all' ||
        (selectedNodeId !== null && resolveOutputEventNodeId(ev, stageIdToNode, activeState) === selectedNodeId)

      if (matchesScope) {
        truncated = {
          isTruncated: true,
          maxBytes: ev.maxBytes ?? DEFAULT_OUTPUT_MAX_BYTES,
          eventSeq: ev.seq,
        }
        timelineItems.push({ kind: 'truncated', event: ev })
      }
    }
  }

  return { outputEvents, truncated, timelineItems }
}

/**
 * Indica si el último `StateChanged` de la Ejecución se encuentra en un estado terminal.
 */
export function isRunInTerminalState(events: readonly RunEvent[], runId?: string): boolean {
  const scoped = runId ? events.filter((e) => e.runId === runId) : events
  const sorted = getSortedEvents(scoped)
  let latestState: RunState | null = null
  for (const ev of sorted) {
    if (ev.type === 'StateChanged') {
      latestState = ev.to
    }
  }
  return latestState === 'PullRequestCreated' || latestState === 'Failed' || latestState === 'Cancelled'
}

/**
 * Calcula la acción determinista de sincronización de xterm.js:
 * - Si el contexto no cambió y `prevWrittenSeqs` es prefijo exacto de `nextTimelineItems`,
 *   solo añade el sufijo nuevo (`append`) sin duplicar ni resetear; vuelca de inmediato si
 *   la Ejecución alcanzó estado terminal o si el sufijo incluye un `OutputTruncated`.
 * - En cualquier otro caso (cambio de contexto, hueco rellenado con `afterSeq`, re-correlación
 *   tardía de `stageId` o lista vacía), ordena `reset` y reescritura completa en orden.
 */
export function computeTerminalSyncAction(
  prevContextKey: string,
  nextContextKey: string,
  prevWrittenSeqs: readonly number[],
  nextTimelineItems: readonly TerminalTimelineItem[],
  isTerminalState: boolean,
): TerminalSyncAction {
  const nextSeqs = nextTimelineItems.map((item) => item.event.seq)

  const sameContext = prevContextKey === nextContextKey
  let isExactPrefix = sameContext && prevWrittenSeqs.length <= nextSeqs.length
  if (isExactPrefix) {
    for (let i = 0; i < prevWrittenSeqs.length; i++) {
      if (prevWrittenSeqs[i] !== nextSeqs[i]) {
        isExactPrefix = false
        break
      }
    }
  }

  if (isExactPrefix) {
    const suffix = nextTimelineItems.slice(prevWrittenSeqs.length)
    if (suffix.length === 0) {
      return { kind: 'noop', nextWrittenSeqs: nextSeqs }
    }

    const hasTruncated = suffix.some((item) => item.kind === 'truncated')
    return {
      kind: 'append',
      itemsToWrite: suffix,
      flushImmediately: isTerminalState || hasTruncated,
      nextWrittenSeqs: nextSeqs,
    }
  }

  return {
    kind: 'reset',
    itemsToWrite: [...nextTimelineItems],
    nextWrittenSeqs: nextSeqs,
  }
}

/**
 * Cuenta cuántos fragmentos `OutputAppended` corresponden a cada nodo del grafo.
 */
export function countOutputEventsByNode(events: readonly RunEvent[]): Map<string, number> {
  const sorted = getSortedEvents(events)
  const stageIdToNode = buildStageIdToNodeMap(sorted)
  const counts = new Map<string, number>()
  let activeState: RunState = 'Received'

  for (const ev of sorted) {
    if (ev.type === 'StateChanged') {
      activeState = ev.to
      continue
    }
    if (ev.type === 'OutputAppended') {
      const nodeId = resolveOutputEventNodeId(ev, stageIdToNode, activeState)
      counts.set(nodeId, (counts.get(nodeId) ?? 0) + 1)
    }
  }

  return counts
}

/**
 * Selecciona el nodo por defecto cuando el operador aún no ha hecho clic manualmente:
 * 1) El nodo activo (priorizando uno que ya tenga salida),
 * 2) El último nodo que emitió salida `OutputAppended`,
 * 3) `'coding'` si existe, o el primer nodo disponible.
 */
export function selectDefaultNodeId(
  nodes: readonly PipelineFlowNode[],
  events: readonly RunEvent[],
): string | null {
  if (nodes.length === 0) {
    return null
  }

  const nodeIds = new Set(nodes.map((n) => n.id))
  const outputCounts = countOutputEventsByNode(events)

  const activeNodes = nodes.filter((n) => n.data.status === 'active')
  if (activeNodes.length > 0) {
    const activeWithOutput = activeNodes.find((n) => (outputCounts.get(n.id) ?? 0) > 0)
    return (activeWithOutput ?? activeNodes[0]).id
  }

  // Buscar el último nodo con salida disponible por seq descendente.
  const sorted = getSortedEvents(events)
  const stageIdToNode = buildStageIdToNodeMap(sorted)
  let activeState: RunState = 'Received'
  let lastOutputNodeId: string | null = null

  for (const ev of sorted) {
    if (ev.type === 'StateChanged') {
      activeState = ev.to
      continue
    }
    if (ev.type === 'OutputAppended') {
      const candidate = resolveOutputEventNodeId(ev, stageIdToNode, activeState)
      if (nodeIds.has(candidate)) {
        lastOutputNodeId = candidate
      }
    }
  }

  if (lastOutputNodeId) {
    return lastOutputNodeId
  }

  if (nodeIds.has('coding')) {
    return 'coding'
  }

  return nodes[0].id
}

/**
 * Devuelve únicamente los fragmentos cuyo `seq` supera `lastWrittenSeq`,
 * evitando duplicar líneas tras reconexiones `afterSeq` o re-renders de React.
 */
export function selectNewOutputEvents(
  outputEvents: readonly RunEventOutputAppended[],
  lastWrittenSeq: number,
): RunEventOutputAppended[] {
  const result: RunEventOutputAppended[] = []
  for (const ev of outputEvents) {
    if (ev.seq > lastWrittenSeq) {
      result.push(ev)
    }
  }
  return result
}

/**
 * Formatea un fragmento de salida de forma segura para xterm.js:
 * - Elimina controles C1 de 8 bits y secuencias OSC/DCS/SOS/PM/APC (incluso sin cerrar).
 * - Conserva exclusivamente códigos SGR (`ESC [ ... m`), eliminando RIS y CSI no-SGR.
 * - Preserva `\r` sueltos (barras de progreso) y normaliza `\n` sin `\r` previo a `\r\n`.
 * - Distingue `stderr` con color ANSI rojo suave.
 */
export function formatOutputChunkForTerminal(
  chunk: Pick<RunEventOutputAppended, 'text' | 'stream'>,
): string {
  if (!chunk.text) {
    return ''
  }

  const sanitized = chunk.text
    .replace(C1_STRING_CONTROL_REGEX, '')
    .replace(C1_CONTROL_REGEX, '')
    .replace(ESC_STRING_CONTROL_REGEX, '')
    .replace(ESC_FILTER_REGEX, (match, sgrParams: string | undefined) =>
      sgrParams !== undefined ? match : '',
    )

  const normalized = sanitized.replace(/(?<!\r)\n/g, '\r\n')

  if (chunk.stream?.toLowerCase() === 'stderr') {
    return `\x1b[38;5;203m${normalized}\x1b[0m`
  }

  return normalized
}

/**
 * Formatea una entrada de la línea temporal de terminal (`OutputAppended` o `OutputTruncated`).
 */
export function formatTimelineItemForTerminal(item: TerminalTimelineItem): string {
  if (item.kind === 'output') {
    return formatOutputChunkForTerminal(item.event)
  }
  const maxBytes = item.event.maxBytes ?? DEFAULT_OUTPUT_MAX_BYTES
  return `\r\n\x1b[1;33m[AVISO] Salida truncada al alcanzar el límite de ${formatByteLimit(maxBytes)} por Etapa. Los fragmentos posteriores han sido descartados.\x1b[0m\r\n`
}

/**
 * Formatea un límite en bytes de forma legible (p. ej. `5 MB` para `5 * 1024 * 1024`).
 */
export function formatByteLimit(maxBytes: number): string {
  if (maxBytes <= 0) {
    return '0 B'
  }
  const mebibyte = 1024 * 1024
  if (maxBytes >= mebibyte) {
    const mb = maxBytes / mebibyte
    return Number.isInteger(mb) ? `${mb} MB` : `${mb.toFixed(1)} MB`
  }
  if (maxBytes >= 1024) {
    const kb = maxBytes / 1024
    return Number.isInteger(kb) ? `${kb} KB` : `${kb.toFixed(1)} KB`
  }
  return `${maxBytes} B`
}
