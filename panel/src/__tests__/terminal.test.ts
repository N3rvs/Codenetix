import { afterEach, describe, expect, it, vi } from 'vitest'
import { mergeRunEvents } from '../api/client'
import type { RunEvent, RunEventOutputAppended, RunSummary } from '../api/generated'
import { buildPipelineGraph } from '../graph/buildPipelineGraph'
import { createOutputBatcher, DEFAULT_BATCH_WINDOW_MS } from '../terminal/outputBatcher'
import {
  computeTerminalSyncAction,
  DEFAULT_OUTPUT_MAX_BYTES,
  extractStageTerminalView,
  formatByteLimit,
  formatOutputChunkForTerminal,
  isRunInTerminalState,
  selectDefaultNodeId,
  selectNewOutputEvents,
} from '../terminal/stageOutput'

const RUN_A = '11111111-1111-1111-1111-111111111111'
const RUN_B = '99999999-9999-9999-9999-999999999999'
const BASE_AT = '2026-11-22T00:00:00Z'

function makeSummary(overrides: Partial<RunSummary> = {}): RunSummary {
  return {
    runId: RUN_A,
    shortId: '11111111',
    state: 'Coding',
    failureReason: null,
    failureDetail: null,
    eventId: '22222222-2222-2222-2222-222222222222',
    objectKey: 'github:owner/repo#42',
    createdAt: BASE_AT,
    updatedAt: BASE_AT,
    ...overrides,
  }
}

function makeStateChanged(
  seq: number,
  from: RunSummary['state'] | null,
  to: RunSummary['state'],
  runId: string = RUN_A,
): RunEvent {
  return {
    type: 'StateChanged',
    seq,
    runId,
    at: BASE_AT,
    from,
    to,
  }
}

function makeOutput(
  seq: number,
  text: string,
  options: { stageId?: string | null; stream?: string; runId?: string } = {},
): RunEventOutputAppended {
  return {
    type: 'OutputAppended',
    seq,
    runId: options.runId ?? RUN_A,
    at: BASE_AT,
    text,
    stream: options.stream ?? 'stdout',
    stageId: options.stageId ?? null,
  }
}

describe('outputBatcher', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('agrupa una ráfaga de 50 fragmentos en una sola escritura exactamente a los 250 ms', () => {
    vi.useFakeTimers()
    const writes: string[] = []
    const batcher = createOutputBatcher((chunk) => writes.push(chunk), DEFAULT_BATCH_WINDOW_MS)

    // 50 chunks emitidos a lo largo de 50 ms (5 avances de 10 ms).
    for (let i = 0; i < 50; i++) {
      batcher.push(`linea-${i}\r\n`)
      if (i % 10 === 0) {
        vi.advanceTimersByTime(10)
      }
    }

    // A los 249 ms (50 + 199 ms) todavía no se ha volcado.
    vi.advanceTimersByTime(199)
    expect(writes).toHaveLength(0)

    // Al llegar exactamente a 250 ms (+1 ms) se vuelca todo en una única escritura.
    vi.advanceTimersByTime(1)
    expect(writes).toHaveLength(1)
    const expected = Array.from({ length: 50 }, (_, i) => `linea-${i}\r\n`).join('')
    expect(writes[0]).toBe(expected)

    // Un segundo ciclo tras el flush vuelve a agrupar correctamente.
    batcher.push('ciclo-2-a ')
    batcher.push('ciclo-2-b')
    vi.advanceTimersByTime(249)
    expect(writes).toHaveLength(1)
    vi.advanceTimersByTime(1)
    expect(writes).toEqual([expected, 'ciclo-2-a ciclo-2-b'])

    batcher.dispose()
  })

  it('acota windowMs > 250 a 250 ms y maneja NaN de forma segura', () => {
    vi.useFakeTimers()
    const writesOver: string[] = []
    const overBatcher = createOutputBatcher((chunk) => writesOver.push(chunk), 5000)
    overBatcher.push('acotado')
    vi.advanceTimersByTime(249)
    expect(writesOver).toHaveLength(0)
    vi.advanceTimersByTime(1)
    expect(writesOver).toEqual(['acotado'])
    overBatcher.dispose()

    const writesNan: string[] = []
    const nanBatcher = createOutputBatcher((chunk) => writesNan.push(chunk), Number.NaN)
    nanBatcher.push('seguro')
    vi.advanceTimersByTime(249)
    expect(writesNan).toHaveLength(0)
    vi.advanceTimersByTime(1)
    expect(writesNan).toEqual(['seguro'])
    nanBatcher.dispose()
  })

  it('flushNow vuelca inmediatamente y cancela el temporizador pendiente', () => {
    vi.useFakeTimers()
    const writes: string[] = []
    const batcher = createOutputBatcher((chunk) => writes.push(chunk), 250)

    batcher.push('parte-1 ')
    batcher.push('parte-2')
    batcher.flushNow()

    expect(writes).toEqual(['parte-1 parte-2'])

    vi.advanceTimersByTime(500)
    expect(writes).toHaveLength(1)

    batcher.dispose()
  })

  it('dispose cancela temporizadores pendientes y nunca escribe tras el dispose', () => {
    vi.useFakeTimers()
    const writes: string[] = []
    const batcher = createOutputBatcher((chunk) => writes.push(chunk), 250)

    batcher.push('descartado')
    batcher.dispose()

    vi.advanceTimersByTime(1000)
    batcher.push('tampoco-escribe')
    batcher.flushNow()

    expect(writes).toHaveLength(0)
  })
})

describe('buildPipelineGraph selección de nodo', () => {
  it('marca selected y data.selected únicamente en selectedNodeId', () => {
    const run = makeSummary({ state: 'Coding' })
    const events: RunEvent[] = [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      makeStateChanged(3, 'Analyzing', 'Coding'),
    ]

    const { nodes } = buildPipelineGraph(run, events, 'analyzing')
    const analyzing = nodes.find((n) => n.id === 'analyzing')
    const coding = nodes.find((n) => n.id === 'coding')

    expect(analyzing?.selected).toBe(true)
    expect(analyzing?.data.selected).toBe(true)
    expect(coding?.selected).toBe(false)
    expect(coding?.data.selected).toBe(false)
  })
})

describe('stageOutput', () => {
  it('asocia OutputAppended y OutputTruncated por stageId (VerdictRecorded, SpecGenerated, TokensConsumed) y por StateChanged', () => {
    const qualityStageId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
    const analystStageId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    const testsStageId = 'cccccccc-cccc-cccc-cccc-cccccccccccc'

    const events: RunEvent[] = [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      makeOutput(3, 'analizando issue...\n', { stageId: analystStageId }),
      {
        type: 'SpecGenerated',
        seq: 4,
        runId: RUN_A,
        stageId: analystStageId,
        at: BASE_AT,
        attempts: 1,
        model: 'claude',
      },
      makeStateChanged(5, 'Analyzing', 'Coding'),
      makeOutput(6, 'compilando solución\n'),
      makeOutput(7, 'warning: unused var\n', { stream: 'stderr' }),
      makeStateChanged(8, 'Coding', 'Reviewing'),
      makeOutput(9, 'revisando calidad del diff\n', { stageId: qualityStageId }),
      {
        type: 'VerdictRecorded',
        seq: 10,
        runId: RUN_A,
        stageId: qualityStageId,
        at: BASE_AT,
        round: 1,
        reviewer: 'quality',
        decision: 'approved',
        findingCount: 0,
        model: 'claude',
      },
      {
        type: 'OutputTruncated',
        seq: 11,
        runId: RUN_A,
        stageId: qualityStageId,
        at: BASE_AT,
        maxBytes: 1024 * 1024,
      },
      makeOutput(12, 'revisando cobertura de pruebas\n', { stageId: testsStageId }),
      {
        type: 'TokensConsumed',
        seq: 13,
        runId: RUN_A,
        stageId: testsStageId,
        at: BASE_AT,
        role: 'reviewer:tests',
        provider: 'anthropic',
        model: 'claude',
        inputTokens: 100,
        outputTokens: 50,
      },
      {
        type: 'OutputTruncated',
        seq: 14,
        runId: RUN_A,
        stageId: testsStageId,
        at: BASE_AT,
        // maxBytes omitido -> debe usar DEFAULT_OUTPUT_MAX_BYTES (5 MB)
      },
    ]

    const analyzingView = extractStageTerminalView(events, 'analyzing', 'node', RUN_A)
    expect(analyzingView.outputEvents.map((e) => e.seq)).toEqual([3])
    expect(analyzingView.truncated.isTruncated).toBe(false)

    const codingView = extractStageTerminalView(events, 'coding', 'node', RUN_A)
    expect(codingView.outputEvents.map((e) => e.seq)).toEqual([6, 7])

    const qualityView = extractStageTerminalView(events, 'review-quality', 'node', RUN_A)
    expect(qualityView.outputEvents.map((e) => e.seq)).toEqual([9])
    expect(qualityView.truncated.isTruncated).toBe(true)
    expect(qualityView.truncated.maxBytes).toBe(1024 * 1024)

    const testsView = extractStageTerminalView(events, 'review-tests', 'node', RUN_A)
    expect(testsView.outputEvents.map((e) => e.seq)).toEqual([12])
    expect(testsView.truncated.isTruncated).toBe(true)
    expect(testsView.truncated.maxBytes).toBe(DEFAULT_OUTPUT_MAX_BYTES)

    // En modo 'all', ambos OutputTruncated quedan intercalados en orden cronológico en timelineItems.
    const allView = extractStageTerminalView(events, 'coding', 'all', RUN_A)
    expect(allView.timelineItems.map((i) => `${i.kind}:${i.event.seq}`)).toEqual([
      'output:3',
      'output:6',
      'output:7',
      'output:9',
      'truncated:11',
      'output:12',
      'truncated:14',
    ])
  })

  it('filtra defensivamente eventos pertenecientes a otro runId', () => {
    const mixed: RunEvent[] = [
      makeStateChanged(1, null, 'Coding', RUN_A),
      makeOutput(2, 'salida-A\n', { runId: RUN_A }),
      makeStateChanged(1, null, 'Coding', RUN_B),
      makeOutput(2, 'salida-B\n', { runId: RUN_B }),
    ]

    const viewB = extractStageTerminalView(mixed, 'coding', 'node', RUN_B)
    expect(viewB.outputEvents).toHaveLength(1)
    expect(viewB.outputEvents[0].text).toBe('salida-B\n')
    expect(viewB.outputEvents[0].runId).toBe(RUN_B)
  })

  it('sanitiza estrictamente (solo SGR) y preserva \\r sueltos de barras de progreso', () => {
    // Preserva SGR de color y \r sueltos, normaliza \n sin \r previo a \r\n.
    const progressWithSgr = '\x1b[32mProgreso 50%\x1b[0m\r\x1b[32mProgreso 100%\x1b[0m\nHecho\r\n'
    expect(formatOutputChunkForTerminal({ text: progressWithSgr, stream: 'stdout' })).toBe(
      '\x1b[32mProgreso 50%\x1b[0m\r\x1b[32mProgreso 100%\x1b[0m\r\nHecho\r\n',
    )

    // Elimina C1 (\u009d), RIS (\x1bc), CSI no-SGR (\x1b[2J, \x1b[H, \x1b[?1049h) y OSC sin cerrar.
    const hostile =
      'inicio\x1bc\x1b[2J\x1b[H\x1b[?1049h\u009d0;titulo-c1\x07medio\x1b]0;osc-sin-cerrar'
    expect(formatOutputChunkForTerminal({ text: hostile, stream: 'stdout' })).toBe('iniciomedio')

    const stderrFormatted = formatOutputChunkForTerminal({ text: 'fallo grave\n', stream: 'stderr' })
    expect(stderrFormatted).toBe('\x1b[38;5;203mfallo grave\r\n\x1b[0m')
  })

  it('formatea límites de bytes de forma legible', () => {
    expect(formatByteLimit(5 * 1024 * 1024)).toBe('5 MB')
    expect(formatByteLimit(1.5 * 1024 * 1024)).toBe('1.5 MB')
    expect(formatByteLimit(512 * 1024)).toBe('512 KB')
    expect(formatByteLimit(256)).toBe('256 B')
    expect(formatByteLimit(0)).toBe('0 B')
  })

  it('selecciona por defecto el nodo activo o el último nodo con salida', () => {
    const activeRun = makeSummary({ state: 'Coding' })
    const activeEvents: RunEvent[] = [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      makeStateChanged(3, 'Analyzing', 'Coding'),
    ]
    const activeGraph = buildPipelineGraph(activeRun, activeEvents)
    expect(selectDefaultNodeId(activeGraph.nodes, activeEvents)).toBe('coding')

    const finishedRun = makeSummary({ state: 'PullRequestCreated' })
    const finishedEvents: RunEvent[] = [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      makeOutput(3, 'spec lista\n'),
      makeStateChanged(4, 'Analyzing', 'Coding'),
      makeOutput(5, 'codigo listo\n'),
      makeStateChanged(6, 'Coding', 'PullRequestCreated'),
    ]
    const finishedGraph = buildPipelineGraph(finishedRun, finishedEvents)
    expect(selectDefaultNodeId(finishedGraph.nodes, finishedEvents)).toBe('coding')
    expect(isRunInTerminalState(finishedEvents, RUN_A)).toBe(true)
    expect(isRunInTerminalState(activeEvents, RUN_A)).toBe(false)
  })

  it('deduplica incrementalmente por seq tras reconexión afterSeq sin reescribir líneas previas', () => {
    const initial: RunEvent[] = [
      makeStateChanged(1, null, 'Coding'),
      makeOutput(2, 'linea-1\n'),
      makeOutput(3, 'linea-2\n'),
    ]

    const view1 = extractStageTerminalView(initial, 'coding', 'node', RUN_A)
    const firstBatch = selectNewOutputEvents(view1.outputEvents, 0)
    expect(firstBatch.map((e) => e.seq)).toEqual([2, 3])
    const lastWrittenSeq = firstBatch[firstBatch.length - 1].seq

    const catchUp: RunEvent[] = [
      makeOutput(3, 'linea-2\n'),
      makeOutput(4, 'linea-3\n'),
      makeOutput(5, 'linea-4\n'),
    ]
    const merged = mergeRunEvents(initial, catchUp)
    const view2 = extractStageTerminalView(merged, 'coding', 'node', RUN_A)
    const secondBatch = selectNewOutputEvents(view2.outputEvents, lastWrittenSeq)

    expect(secondBatch.map((e) => e.seq)).toEqual([4, 5])
  })
})

describe('computeTerminalSyncAction', () => {
  it('(a) cambiar de runA a runB con eventos obsoletos de runA no contamina ni descarta runB', () => {
    const runAEvents: RunEvent[] = [
      makeStateChanged(1, null, 'Coding', RUN_A),
      makeOutput(2, 'A-1\n', { runId: RUN_A }),
      makeOutput(3, 'A-2\n', { runId: RUN_A }),
    ]
    const viewA = extractStageTerminalView(runAEvents, 'coding', 'node', RUN_A)
    const actionA = computeTerminalSyncAction('', `${RUN_A}::node::coding`, [], viewA.timelineItems, false)
    expect(actionA.kind).toBe('reset')
    expect(actionA.nextWrittenSeqs).toEqual([2, 3])

    // Render intermedio tras pulsar runB antes de vaciar o recibir eventos de runB:
    // extractStageTerminalView filtra por RUN_B, devolviendo vacío y disparando reset limpio.
    const staleForB = extractStageTerminalView(runAEvents, 'coding', 'node', RUN_B)
    const actionSwitch = computeTerminalSyncAction(
      `${RUN_A}::node::coding`,
      `${RUN_B}::node::coding`,
      actionA.nextWrittenSeqs,
      staleForB.timelineItems,
      false,
    )
    expect(actionSwitch.kind).toBe('reset')
    if (actionSwitch.kind === 'reset') {
      expect(actionSwitch.itemsToWrite).toEqual([])
    }
    expect(actionSwitch.nextWrittenSeqs).toEqual([])

    // Al llegar los eventos reales de runB (incluso con seq=2 igual que runA), se escriben todos.
    const runBEvents: RunEvent[] = [
      makeStateChanged(1, null, 'Coding', RUN_B),
      makeOutput(2, 'B-1\n', { runId: RUN_B }),
    ]
    const viewB = extractStageTerminalView(runBEvents, 'coding', 'node', RUN_B)
    const actionB = computeTerminalSyncAction(
      `${RUN_B}::node::coding`,
      `${RUN_B}::node::coding`,
      actionSwitch.nextWrittenSeqs,
      viewB.timelineItems,
      false,
    )
    expect(actionB.kind).toBe('append')
    if (actionB.kind === 'append') {
      expect(actionB.itemsToWrite.map((i) => i.event.seq)).toEqual([2])
    }
  })

  it('(b) inserción de un hueco rellenado por afterSeq en medio dispara reset y reescritura ordenada', () => {
    const ctx = `${RUN_A}::node::coding`
    // Llegaron seq 2 y 5 con hueco (faltaban 3 y 4).
    const withGap = extractStageTerminalView(
      [makeStateChanged(1, null, 'Coding'), makeOutput(2, 'l2\n'), makeOutput(5, 'l5\n')],
      'coding',
      'node',
      RUN_A,
    )
    const first = computeTerminalSyncAction('', ctx, [], withGap.timelineItems, false)
    expect(first.nextWrittenSeqs).toEqual([2, 5])

    // Catch-up rellena seq 3 y 4 en medio -> [2, 5] ya no es prefijo exacto de [2, 3, 4, 5].
    const filled = extractStageTerminalView(
      [
        makeStateChanged(1, null, 'Coding'),
        makeOutput(2, 'l2\n'),
        makeOutput(3, 'l3\n'),
        makeOutput(4, 'l4\n'),
        makeOutput(5, 'l5\n'),
      ],
      'coding',
      'node',
      RUN_A,
    )
    const second = computeTerminalSyncAction(ctx, ctx, first.nextWrittenSeqs, filled.timelineItems, false)
    expect(second.kind).toBe('reset')
    if (second.kind === 'reset') {
      expect(second.itemsToWrite.map((i) => i.event.seq)).toEqual([2, 3, 4, 5])
    }
  })

  it('(c) re-correlación tardía de stageId limpia el nodo anterior mediante reset', () => {
    const qualityStageId = 'dddddddd-dddd-dddd-dddd-dddddddddddd'
    const ctxSecurity = `${RUN_A}::node::review-security`

    // Antes de llegar VerdictRecorded, el chunk en Reviewing cae por respaldo en review-security.
    const beforeVerdict = extractStageTerminalView(
      [
        makeStateChanged(1, null, 'Reviewing'),
        makeOutput(2, 'revisando calidad...\n', { stageId: qualityStageId }),
      ],
      'review-security',
      'node',
      RUN_A,
    )
    const first = computeTerminalSyncAction('', ctxSecurity, [], beforeVerdict.timelineItems, false)
    expect(first.nextWrittenSeqs).toEqual([2])

    // Llega VerdictRecorded(quality) con el mismo stageId -> seq 2 deja de pertenecer a review-security.
    const afterVerdict = extractStageTerminalView(
      [
        makeStateChanged(1, null, 'Reviewing'),
        makeOutput(2, 'revisando calidad...\n', { stageId: qualityStageId }),
        {
          type: 'VerdictRecorded',
          seq: 3,
          runId: RUN_A,
          stageId: qualityStageId,
          at: BASE_AT,
          round: 1,
          reviewer: 'quality',
          decision: 'approved',
          findingCount: 0,
          model: 'claude',
        },
      ],
      'review-security',
      'node',
      RUN_A,
    )
    const second = computeTerminalSyncAction(
      ctxSecurity,
      ctxSecurity,
      first.nextWrittenSeqs,
      afterVerdict.timelineItems,
      false,
    )
    expect(second.kind).toBe('reset')
    if (second.kind === 'reset') {
      expect(second.itemsToWrite).toEqual([])
    }
    expect(second.nextWrittenSeqs).toEqual([])
  })

  it('(d) reconexión con eventos solapados solo añade seq nuevos sin reset y vuelca en estado terminal', () => {
    const ctx = `${RUN_A}::node::coding`
    const initialEvents: RunEvent[] = [
      makeStateChanged(1, null, 'Coding'),
      makeOutput(2, 'l1\n'),
      makeOutput(3, 'l2\n'),
    ]
    const view1 = extractStageTerminalView(initialEvents, 'coding', 'node', RUN_A)
    const step1 = computeTerminalSyncAction('', ctx, [], view1.timelineItems, false)

    const merged = mergeRunEvents(initialEvents, [
      makeOutput(3, 'l2\n'),
      makeOutput(4, 'l3\n'),
      makeStateChanged(5, 'Coding', 'Failed'),
    ])
    const view2 = extractStageTerminalView(merged, 'coding', 'node', RUN_A)
    const step2 = computeTerminalSyncAction(
      ctx,
      ctx,
      step1.nextWrittenSeqs,
      view2.timelineItems,
      isRunInTerminalState(merged, RUN_A),
    )

    expect(step2.kind).toBe('append')
    if (step2.kind === 'append') {
      expect(step2.itemsToWrite.map((i) => i.event.seq)).toEqual([4])
      expect(step2.flushImmediately).toBe(true)
    }
  })
})
