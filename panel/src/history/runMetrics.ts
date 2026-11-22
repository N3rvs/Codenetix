import type {
  ReviewerKind,
  RiskClass,
  RunEvent,
  RunEventRiskClassified,
  RunState,
  RunSummary,
  RunVerdict,
  VerdictDecision,
} from '../api/generated'

export type HistoryStateFilter = 'all' | 'PullRequestCreated' | 'Failed' | 'Cancelled' | 'active'

export interface TokenBreakdownEntry {
  role: string
  provider: string
  model: string
  inputTokens: number
  outputTokens: number
  totalTokens: number
}

export interface TokenUsageSummary {
  inputTokens: number
  outputTokens: number
  totalTokens: number
  eventCount: number
  breakdown: TokenBreakdownEntry[]
}

export interface VerdictRoundGroup {
  round: number
  verdicts: RunVerdict[]
}

export const HISTORY_FILTER_OPTIONS: readonly { value: HistoryStateFilter; label: string }[] = [
  { value: 'all', label: 'Todos' },
  { value: 'PullRequestCreated', label: 'PR abierto' },
  { value: 'Failed', label: 'Fallida' },
  { value: 'Cancelled', label: 'Cancelada' },
  { value: 'active', label: 'En curso' },
]

export const REVIEWER_DISPLAY_LABELS: Record<ReviewerKind, string> = {
  security: 'Seguridad',
  quality: 'Calidad',
  tests: 'Pruebas',
}

export const VERDICT_DECISION_LABELS: Record<VerdictDecision, string> = {
  approved: 'Aprobado',
  changes_requested: 'Cambios pedidos',
  rejected: 'Rechazado',
}

export const RISK_CLASS_LABELS: Record<RiskClass, string> = {
  Minor: 'Riesgo menor',
  NeedsAttention: 'Requiere atención',
}

const REVIEWER_ORDER: Record<string, number> = {
  security: 0,
  quality: 1,
  tests: 2,
}

export function isTerminalRunState(state: RunState): boolean {
  return state === 'PullRequestCreated' || state === 'Failed' || state === 'Cancelled'
}

/**
 * Calcula la duración entre createdAt y updatedAt acotando valores negativos a 0s.
 * Devuelve '—' si alguna fecha es inválida (nunca NaN ni negativo).
 */
export function formatDuration(createdAt: string, updatedAt: string): string {
  const startMs = Date.parse(createdAt)
  const endMs = Date.parse(updatedAt)

  if (!Number.isFinite(startMs) || !Number.isFinite(endMs)) {
    return '—'
  }

  const totalSeconds = Math.max(0, Math.floor((endMs - startMs) / 1000))
  if (totalSeconds < 60) {
    return `${totalSeconds}s`
  }

  const totalMinutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  if (totalMinutes < 60) {
    return `${totalMinutes}m ${String(seconds).padStart(2, '0')}s`
  }

  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  return `${hours}h ${String(minutes).padStart(2, '0')}m`
}

/**
 * Formatea una marca temporal ISO en español (es-ES), o '—' si es inválida.
 */
export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) {
    return '—'
  }
  return date.toLocaleString('es-ES')
}

/**
 * Agrega tokens consumidos desde los eventos TokensConsumed de una Ejecución,
 * incluyendo desglose por rol/proveedor/modelo.
 */
export function aggregateTokenUsage(events: readonly RunEvent[]): TokenUsageSummary {
  let inputTokens = 0
  let outputTokens = 0
  let eventCount = 0

  const byKey = new Map<string, TokenBreakdownEntry>()

  for (const ev of events) {
    if (ev.type !== 'TokensConsumed') {
      continue
    }

    const inTokens = Number.isFinite(ev.inputTokens) ? Math.max(0, ev.inputTokens) : 0
    const outTokens = Number.isFinite(ev.outputTokens) ? Math.max(0, ev.outputTokens) : 0

    inputTokens += inTokens
    outputTokens += outTokens
    eventCount += 1

    const key = `${ev.role}\0${ev.provider}\0${ev.model}`
    const existing = byKey.get(key)
    if (existing) {
      existing.inputTokens += inTokens
      existing.outputTokens += outTokens
      existing.totalTokens += inTokens + outTokens
    } else {
      byKey.set(key, {
        role: ev.role,
        provider: ev.provider,
        model: ev.model,
        inputTokens: inTokens,
        outputTokens: outTokens,
        totalTokens: inTokens + outTokens,
      })
    }
  }

  return {
    inputTokens,
    outputTokens,
    totalTokens: inputTokens + outputTokens,
    eventCount,
    breakdown: Array.from(byKey.values()),
  }
}

/**
 * Formatea un coste en USD cuando existe; devuelve 'No disponible' si es null, undefined, NaN o negativo.
 */
export function formatCostDisplay(costUsd?: number | null): string {
  if (typeof costUsd !== 'number' || !Number.isFinite(costUsd) || costUsd < 0) {
    return 'No disponible'
  }

  if (costUsd === 0) {
    return '$0.00'
  }

  if (costUsd < 0.01) {
    return `$${costUsd.toFixed(4)}`
  }

  return `$${costUsd.toFixed(2)}`
}

/**
 * Filtra Ejecuciones del historial por estado y búsqueda insensible a mayúsculas sobre shortId u objectKey,
 * preservando el orden original.
 */
export function filterHistoryRuns(
  runs: readonly RunSummary[],
  stateFilter: HistoryStateFilter,
  query: string,
): RunSummary[] {
  const normalizedQuery = query.trim().toLowerCase()

  return runs.filter((run) => {
    if (stateFilter === 'active') {
      if (isTerminalRunState(run.state)) {
        return false
      }
    } else if (stateFilter !== 'all' && run.state !== stateFilter) {
      return false
    }

    if (normalizedQuery.length > 0) {
      const shortIdQuery = normalizedQuery.startsWith('#') ? normalizedQuery.slice(1) : normalizedQuery
      const matchesShortId = shortIdQuery.length > 0 && run.shortId.toLowerCase().includes(shortIdQuery)
      const matchesObjectKey = run.objectKey.toLowerCase().includes(normalizedQuery)
      if (!matchesShortId && !matchesObjectKey) {
        return false
      }
    }

    return true
  })
}

/**
 * Agrupa y ordena veredictos por ronda ascendente y por orden canónico de revisor (security, quality, tests, otros).
 */
export function groupVerdictsByRound(verdicts: readonly RunVerdict[]): VerdictRoundGroup[] {
  const byRound = new Map<number, RunVerdict[]>()

  for (const verdict of verdicts) {
    const bucket = byRound.get(verdict.round)
    if (bucket) {
      bucket.push(verdict)
    } else {
      byRound.set(verdict.round, [verdict])
    }
  }

  return Array.from(byRound.entries())
    .sort(([roundA], [roundB]) => roundA - roundB)
    .map(([round, roundVerdicts]) => ({
      round,
      verdicts: [...roundVerdicts].sort((a, b) => {
        const orderA = REVIEWER_ORDER[a.reviewer] ?? 99
        const orderB = REVIEWER_ORDER[b.reviewer] ?? 99
        if (orderA !== orderB) {
          return orderA - orderB
        }
        return a.reviewer.localeCompare(b.reviewer)
      }),
    }))
}

/**
 * Extrae el último evento RiskClassified de una Ejecución, si existe.
 */
export function extractLatestRiskClassification(
  events: readonly RunEvent[],
): RunEventRiskClassified | null {
  let latest: RunEventRiskClassified | null = null
  for (const ev of events) {
    if (ev.type === 'RiskClassified') {
      if (!latest || ev.seq >= latest.seq) {
        latest = ev
      }
    }
  }
  return latest
}
