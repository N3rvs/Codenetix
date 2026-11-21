import { describe, expect, it } from 'vitest'
import { mergeRunEvents } from '../api/client'
import type { RunEvent, RunSummary } from '../api/generated'
import { buildPipelineGraph, deriveCurrentState } from '../graph/buildPipelineGraph'

const RUN_ID = '11111111-1111-1111-1111-111111111111'
const BASE_AT = '2026-11-21T20:00:00Z'

function makeSummary(overrides: Partial<RunSummary> = {}): RunSummary {
  return {
    runId: RUN_ID,
    shortId: '11111111',
    state: 'Received',
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
  failureReason: RunSummary['failureReason'] = null,
  detail: string | null = null,
): RunEvent {
  return {
    type: 'StateChanged',
    seq,
    runId: RUN_ID,
    at: BASE_AT,
    from,
    to,
    failureReason,
    detail,
  }
}

describe('mergeRunEvents', () => {
  it('deduplica por seq y ordena ascendentemente al fusionar catch-up y eventos en vivo', () => {
    const existing: RunEvent[] = [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      { type: 'OutputAppended', seq: 5, runId: RUN_ID, at: BASE_AT, text: 'live-5' },
    ]
    const incoming: RunEvent[] = [
      { type: 'OutputAppended', seq: 4, runId: RUN_ID, at: BASE_AT, text: 'catchup-4' },
      { type: 'SpecGenerated', seq: 3, runId: RUN_ID, at: BASE_AT, attempts: 1, model: 'claude' },
      { type: 'OutputAppended', seq: 5, runId: RUN_ID, at: BASE_AT, text: 'live-5' },
    ]

    const merged = mergeRunEvents(existing, incoming)
    expect(merged.map((e) => e.seq)).toEqual([1, 2, 3, 4, 5])
  })
})

describe('deriveCurrentState', () => {
  it('toma el último StateChanged sobre el RunSummary inicial', () => {
    const run = makeSummary({ state: 'Received' })
    const events: RunEvent[] = [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      makeStateChanged(3, 'Analyzing', 'Failed', 'TestsFailed', '2 tests fallaron'),
    ]

    const derived = deriveCurrentState(run, events)
    expect(derived.state).toBe('Failed')
    expect(derived.failureReason).toBe('TestsFailed')
    expect(derived.failureDetail).toBe('2 tests fallaron')
  })
})

describe('buildPipelineGraph', () => {
  it('mantiene los 3 revisores canónicos cuando llega el primer veredicto en R1', () => {
    const run = makeSummary({ state: 'Reviewing' })
    const events: RunEvent[] = [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      makeStateChanged(3, 'Analyzing', 'Coding'),
      makeStateChanged(4, 'Coding', 'Reviewing'),
      {
        type: 'VerdictRecorded',
        seq: 5,
        runId: RUN_ID,
        at: BASE_AT,
        round: 1,
        reviewer: 'security',
        decision: 'approved',
        findingCount: 0,
        model: 'claude',
      },
    ]

    const { nodes } = buildPipelineGraph(run, events)
    const security = nodes.find((n) => n.id === 'review-security')
    const quality = nodes.find((n) => n.id === 'review-quality')
    const tests = nodes.find((n) => n.id === 'review-tests')

    expect(security?.data.status).toBe('completed')
    expect(quality?.data.status).toBe('active')
    expect(tests?.data.status).toBe('active')
  })

  it('maneja cambios pedidos en R1 -> vuelta a Coding -> nueva ronda R2 en Reviewing', () => {
    const run = makeSummary({ state: 'Coding' })
    const r1Events: RunEvent[] = [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      makeStateChanged(3, 'Analyzing', 'Coding'),
      makeStateChanged(4, 'Coding', 'Reviewing'),
      {
        type: 'VerdictRecorded',
        seq: 5,
        runId: RUN_ID,
        at: BASE_AT,
        round: 1,
        reviewer: 'security',
        decision: 'approved',
        findingCount: 0,
        model: 'claude',
      },
      {
        type: 'VerdictRecorded',
        seq: 6,
        runId: RUN_ID,
        at: BASE_AT,
        round: 1,
        reviewer: 'quality',
        decision: 'changes_requested',
        findingCount: 2,
        model: 'claude',
      },
      makeStateChanged(7, 'Reviewing', 'Coding'),
    ]

    const inCoding = buildPipelineGraph(run, r1Events)
    const qualityDuringCoding = inCoding.nodes.find((n) => n.id === 'review-quality')
    expect(qualityDuringCoding?.data.status).toBe('pending')
    expect(qualityDuringCoding?.data.subtitle).toContain('Cambios pedidos')

    // Al entrar en Reviewing de nuevo (R2), todos los revisores sin veredicto R2 pasan a active.
    const r2Events: RunEvent[] = [...r1Events, makeStateChanged(8, 'Coding', 'Reviewing')]
    const inReviewR2 = buildPipelineGraph(makeSummary({ state: 'Reviewing' }), r2Events)
    expect(inReviewR2.nodes.find((n) => n.id === 'review-security')?.data.status).toBe('active')
    expect(inReviewR2.nodes.find((n) => n.id === 'review-security')?.data.badge).toBe('R2')
    expect(inReviewR2.nodes.find((n) => n.id === 'review-quality')?.data.status).toBe('active')
    expect(inReviewR2.nodes.find((n) => n.id === 'review-tests')?.data.status).toBe('active')
  })

  it('marca los nodos terminales correctamente para PullRequestCreated, Failed y Cancelled', () => {
    const prGraph = buildPipelineGraph(makeSummary({ state: 'PullRequestCreated' }), [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      makeStateChanged(3, 'Analyzing', 'Coding'),
      makeStateChanged(4, 'Coding', 'Reviewing'),
      makeStateChanged(5, 'Reviewing', 'Deciding'),
      {
        type: 'PullRequestOpened',
        seq: 6,
        runId: RUN_ID,
        at: BASE_AT,
        number: 77,
        url: 'https://github.com/owner/repo/pull/77',
        branch: 'codenetix/fix-42',
        commitSha: 'abcdef01',
      },
      makeStateChanged(7, 'Deciding', 'PullRequestCreated'),
    ])
    expect(prGraph.nodes.find((n) => n.id === 'terminal')?.data.status).toBe('completed')
    expect(prGraph.nodes.find((n) => n.id === 'terminal')?.data.subtitle).toBe('PR #77')

    const failedGraph = buildPipelineGraph(makeSummary({ state: 'Failed', failureReason: 'TestsFailed' }), [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      makeStateChanged(3, 'Analyzing', 'Coding'),
      makeStateChanged(4, 'Coding', 'Failed', 'TestsFailed', 'Fallo en tests'),
    ])
    expect(failedGraph.nodes.find((n) => n.id === 'testing')?.data.status).toBe('failed')
    expect(failedGraph.nodes.find((n) => n.id === 'terminal')?.data.status).toBe('failed')

    const cancelledGraph = buildPipelineGraph(makeSummary({ state: 'Cancelled' }), [
      makeStateChanged(1, null, 'Received'),
      makeStateChanged(2, 'Received', 'Analyzing'),
      makeStateChanged(3, 'Analyzing', 'Cancelled'),
    ])
    expect(cancelledGraph.nodes.find((n) => n.id === 'analyzing')?.data.status).toBe('cancelled')
    expect(cancelledGraph.nodes.find((n) => n.id === 'terminal')?.data.status).toBe('cancelled')
  })
})
