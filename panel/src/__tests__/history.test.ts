import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fetchRunDetail, isSafeHttpUrl, UnauthorizedError } from '../api/client'
import type { RunEvent, RunResponse, RunSummary, RunVerdict } from '../api/generated'
import { RunHistoryDetail } from '../components/RunHistoryView'
import {
  aggregateTokenUsage,
  extractLatestRiskClassification,
  filterHistoryRuns,
  formatCostDisplay,
  formatDateTime,
  formatDuration,
  groupVerdictsByRound,
} from '../history/runMetrics'

const RUN_ID = '11111111-1111-1111-1111-111111111111'
const BASE_AT = '2026-11-22T10:00:00Z'

function makeSummary(overrides: Partial<RunSummary> = {}): RunSummary {
  return {
    runId: RUN_ID,
    shortId: 'a1b2c3d4',
    state: 'PullRequestCreated',
    failureReason: null,
    failureDetail: null,
    eventId: '22222222-2222-2222-2222-222222222222',
    objectKey: 'github:acme/web#42',
    createdAt: BASE_AT,
    updatedAt: '2026-11-22T10:02:15Z',
    ...overrides,
  }
}

describe('formatDuration', () => {
  it('formatea duraciones en segundos, minutos y horas', () => {
    expect(formatDuration('2026-11-22T10:00:00Z', '2026-11-22T10:00:00Z')).toBe('0s')
    expect(formatDuration('2026-11-22T10:00:00Z', '2026-11-22T10:00:42Z')).toBe('42s')
    expect(formatDuration('2026-11-22T10:00:00Z', '2026-11-22T10:02:15Z')).toBe('2m 15s')
    expect(formatDuration('2026-11-22T10:00:00Z', '2026-11-22T11:05:30Z')).toBe('1h 05m')
  })

  it('acota updatedAt < createdAt a 0s sin valores negativos', () => {
    expect(formatDuration('2026-11-22T10:05:00Z', '2026-11-22T10:00:00Z')).toBe('0s')
  })

  it('devuelve guion largo ante fechas inválidas y nunca NaN', () => {
    expect(formatDuration('fecha-invalida', '2026-11-22T10:00:00Z')).toBe('—')
    expect(formatDuration('2026-11-22T10:00:00Z', 'no-es-iso')).toBe('—')
  })
})

describe('formatDateTime', () => {
  it('formatea una fecha ISO válida y devuelve guion largo si es inválida', () => {
    expect(formatDateTime('2026-11-22T10:00:00Z')).not.toBe('—')
    expect(formatDateTime('invalida')).toBe('—')
  })
})

describe('aggregateTokenUsage', () => {
  it('suma múltiples eventos TokensConsumed y agrupa por role/provider/model', () => {
    const events: RunEvent[] = [
      {
        type: 'RunCreated',
        seq: 1,
        runId: RUN_ID,
        at: BASE_AT,
        eventId: '22222222-2222-2222-2222-222222222222',
        objectKey: 'github:acme/web#42',
      },
      {
        type: 'TokensConsumed',
        seq: 2,
        runId: RUN_ID,
        at: BASE_AT,
        role: 'analyst',
        provider: 'anthropic',
        model: 'claude-sonnet-4-5',
        inputTokens: 1200,
        outputTokens: 450,
      },
      {
        type: 'TokensConsumed',
        seq: 3,
        runId: RUN_ID,
        at: BASE_AT,
        role: 'coder',
        provider: 'anthropic',
        model: 'claude-sonnet-4-5',
        inputTokens: 5000,
        outputTokens: 1800,
      },
      {
        type: 'TokensConsumed',
        seq: 4,
        runId: RUN_ID,
        at: BASE_AT,
        role: 'analyst',
        provider: 'anthropic',
        model: 'claude-sonnet-4-5',
        inputTokens: 300,
        outputTokens: 150,
      },
    ]

    const summary = aggregateTokenUsage(events)
    expect(summary.inputTokens).toBe(6500)
    expect(summary.outputTokens).toBe(2400)
    expect(summary.totalTokens).toBe(8900)
    expect(summary.eventCount).toBe(3)
    expect(summary.breakdown).toEqual([
      {
        role: 'analyst',
        provider: 'anthropic',
        model: 'claude-sonnet-4-5',
        inputTokens: 1500,
        outputTokens: 600,
        totalTokens: 2100,
      },
      {
        role: 'coder',
        provider: 'anthropic',
        model: 'claude-sonnet-4-5',
        inputTokens: 5000,
        outputTokens: 1800,
        totalTokens: 6800,
      },
    ])
  })

  it('devuelve ceros y desglose vacío cuando no hay eventos TokensConsumed', () => {
    const summary = aggregateTokenUsage([])
    expect(summary).toEqual({
      inputTokens: 0,
      outputTokens: 0,
      totalTokens: 0,
      eventCount: 0,
      breakdown: [],
    })
  })

  it('acota tokens negativos o NaN a 0 sin propagar valores inválidos', () => {
    const events: RunEvent[] = [
      {
        type: 'TokensConsumed',
        seq: 1,
        runId: RUN_ID,
        at: BASE_AT,
        role: 'analyst',
        provider: 'anthropic',
        model: 'claude-sonnet-4-5',
        inputTokens: -50,
        outputTokens: Number.NaN,
      },
      {
        type: 'TokensConsumed',
        seq: 2,
        runId: RUN_ID,
        at: BASE_AT,
        role: 'analyst',
        provider: 'anthropic',
        model: 'claude-sonnet-4-5',
        inputTokens: 120,
        outputTokens: 30,
      },
    ]

    const summary = aggregateTokenUsage(events)
    expect(summary.inputTokens).toBe(120)
    expect(summary.outputTokens).toBe(30)
    expect(summary.totalTokens).toBe(150)
    expect(summary.eventCount).toBe(2)
  })
})

describe('formatCostDisplay', () => {
  it('formatea costes válidos y devuelve No disponible para null, undefined, NaN o negativos', () => {
    expect(formatCostDisplay(0)).toBe('$0.00')
    expect(formatCostDisplay(0.0042)).toBe('$0.0042')
    expect(formatCostDisplay(1.25)).toBe('$1.25')
    expect(formatCostDisplay(null)).toBe('No disponible')
    expect(formatCostDisplay(undefined)).toBe('No disponible')
    expect(formatCostDisplay(Number.NaN)).toBe('No disponible')
    expect(formatCostDisplay(-0.5)).toBe('No disponible')
  })
})

describe('filterHistoryRuns', () => {
  const runs: RunSummary[] = [
    makeSummary({ runId: 'r1', shortId: 'aaaa1111', state: 'PullRequestCreated', objectKey: 'github:acme/api#10' }),
    makeSummary({ runId: 'r2', shortId: 'bbbb2222', state: 'Failed', failureReason: 'ReviewRejected', objectKey: 'github:acme/web#20' }),
    makeSummary({ runId: 'r3', shortId: 'cccc3333', state: 'Cancelled', objectKey: 'github:acme/cli#30' }),
    makeSummary({ runId: 'r4', shortId: 'dddd4444', state: 'Coding', objectKey: 'github:acme/api#40' }),
    makeSummary({ runId: 'r5', shortId: 'eeee5555', state: 'Reviewing', objectKey: 'github:acme/web#50' }),
  ]

  it('soporta todos los filtros de estado preservando el orden original', () => {
    expect(filterHistoryRuns(runs, 'all', '').map((r) => r.runId)).toEqual(['r1', 'r2', 'r3', 'r4', 'r5'])
    expect(filterHistoryRuns(runs, 'PullRequestCreated', '').map((r) => r.runId)).toEqual(['r1'])
    expect(filterHistoryRuns(runs, 'Failed', '').map((r) => r.runId)).toEqual(['r2'])
    expect(filterHistoryRuns(runs, 'Cancelled', '').map((r) => r.runId)).toEqual(['r3'])
    expect(filterHistoryRuns(runs, 'active', '').map((r) => r.runId)).toEqual(['r4', 'r5'])
  })

  it('filtra por búsqueda insensible a mayúsculas sobre shortId y objectKey', () => {
    expect(filterHistoryRuns(runs, 'all', 'BBBB22').map((r) => r.runId)).toEqual(['r2'])
    expect(filterHistoryRuns(runs, 'all', '#bbbb22').map((r) => r.runId)).toEqual(['r2'])
    expect(filterHistoryRuns(runs, 'all', '#').map((r) => r.runId)).toEqual(['r1', 'r2', 'r3', 'r4', 'r5'])
    expect(filterHistoryRuns(runs, 'all', 'ACME/API').map((r) => r.runId)).toEqual(['r1', 'r4'])
    expect(filterHistoryRuns(runs, 'active', 'api').map((r) => r.runId)).toEqual(['r4'])
    expect(filterHistoryRuns(runs, 'all', 'sin-coincidencia')).toEqual([])
  })
})

describe('groupVerdictsByRound', () => {
  it('agrupa por ronda ascendente y ordena revisores como security, quality, tests', () => {
    const makeVerdict = (round: number, reviewer: RunVerdict['reviewer']): RunVerdict => ({
      stageId: `s-${round}-${reviewer}`,
      round,
      reviewer,
      decision: 'approved',
      summary: 'Correcto',
      model: 'claude-sonnet-4-5',
      attempts: 1,
      createdAt: BASE_AT,
      findings: [],
    })

    const grouped = groupVerdictsByRound([
      makeVerdict(2, 'tests'),
      makeVerdict(1, 'quality'),
      makeVerdict(2, 'security'),
      makeVerdict(1, 'tests'),
      makeVerdict(1, 'security'),
    ])

    expect(grouped.map((g) => g.round)).toEqual([1, 2])
    expect(grouped[0].verdicts.map((v) => v.reviewer)).toEqual(['security', 'quality', 'tests'])
    expect(grouped[1].verdicts.map((v) => v.reviewer)).toEqual(['security', 'tests'])
  })
})

describe('isSafeHttpUrl', () => {
  it('acepta únicamente https y http y rechaza esquemas peligrosos o inválidos', () => {
    expect(isSafeHttpUrl('https://github.com/acme/repo/pull/7')).toBe(true)
    expect(isSafeHttpUrl('http://localhost:5000/pr/1')).toBe(true)
    expect(isSafeHttpUrl('javascript:alert(1)')).toBe(false)
    expect(isSafeHttpUrl('data:text/html,<script>alert(1)</script>')).toBe(false)
    expect(isSafeHttpUrl('no es una url')).toBe(false)
  })
})

describe('extractLatestRiskClassification', () => {
  it('devuelve null sin eventos RiskClassified y elige el de mayor seq cuando hay varios', () => {
    expect(extractLatestRiskClassification([])).toBeNull()

    const single = extractLatestRiskClassification([
      {
        type: 'RiskClassified',
        seq: 5,
        runId: RUN_ID,
        at: BASE_AT,
        riskClass: 'Minor',
        diffLines: 18,
        matchedProtectedPaths: [],
      },
    ])
    expect(single?.riskClass).toBe('Minor')
    expect(single?.seq).toBe(5)

    const latest = extractLatestRiskClassification([
      {
        type: 'RiskClassified',
        seq: 12,
        runId: RUN_ID,
        at: BASE_AT,
        riskClass: 'NeedsAttention',
        diffLines: 90,
        matchedProtectedPaths: ['src/auth.ts'],
      },
      {
        type: 'RiskClassified',
        seq: 4,
        runId: RUN_ID,
        at: BASE_AT,
        riskClass: 'Minor',
        diffLines: 10,
        matchedProtectedPaths: [],
      },
    ])
    expect(latest?.seq).toBe(12)
    expect(latest?.riskClass).toBe('NeedsAttention')
    expect(latest?.matchedProtectedPaths).toEqual(['src/auth.ts'])
  })
})

describe('fetchRunDetail', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('envía Authorization Bearer, codifica runId y propaga AbortSignal en 200 OK', async () => {
    const sampleDetail: RunResponse = {
      runId: 'run/with space',
      shortId: 'a1b2c3d4',
      state: 'PullRequestCreated',
      failureReason: null,
      failureDetail: null,
      eventId: '22222222-2222-2222-2222-222222222222',
      objectKey: 'github:acme/web#42',
      createdAt: BASE_AT,
      updatedAt: '2026-11-22T10:02:15Z',
      spec: null,
      testReport: null,
      verdicts: [],
      pullRequest: null,
      events: [],
    }

    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(sampleDetail), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const controller = new AbortController()
    const result = await fetchRunDetail('secret-token', 'run/with space', controller.signal)

    expect(result).toEqual(sampleDetail)
    expect(fetchMock).toHaveBeenCalledWith('/api/runs/run%2Fwith%20space', {
      signal: controller.signal,
      headers: {
        Accept: 'application/json',
        Authorization: 'Bearer secret-token',
      },
    })
  })

  it('lanza UnauthorizedError ante 401 y propaga el detalle de ProblemDetails ante 404', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValueOnce(new Response(null, { status: 401 })),
    )
    await expect(fetchRunDetail('bad-token', RUN_ID)).rejects.toBeInstanceOf(UnauthorizedError)

    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValueOnce(
        new Response(
          JSON.stringify({ title: 'No encontrada', detail: 'No existe ninguna Ejecución con ese id.' }),
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )
    await expect(fetchRunDetail('tok', RUN_ID)).rejects.toThrow(
      'No existe ninguna Ejecución con ese id.',
    )
  })
})

describe('RunHistoryDetail (renderizado)', () => {
  function makeFullDetail(prUrl: string): RunResponse {
    return {
      runId: RUN_ID,
      shortId: 'a1b2c3d4',
      state: 'PullRequestCreated',
      failureReason: null,
      failureDetail: null,
      eventId: '22222222-2222-2222-2222-222222222222',
      objectKey: 'github:acme/web#42',
      createdAt: BASE_AT,
      updatedAt: '2026-11-22T10:02:15Z',
      spec: {
        problem: 'Fallo al validar parámetros.',
        probableCause: 'Falta sanitización.',
        proposedChange: 'Validar antes de ejecutar.',
        acceptanceCriteria: ['Rechaza valores negativos'],
        affectedFiles: ['src/App.cs'],
      },
      testReport: {
        buildSucceeded: true,
        totalTests: 10,
        passedTests: 10,
        failedTests: 0,
        skippedTests: 0,
        newTests: ['AppTests.ValidatesInput'],
        failedTestCases: [],
        duration: '00:00:02.500',
      },
      verdicts: [
        {
          stageId: 's-1',
          round: 1,
          reviewer: 'security',
          decision: 'approved',
          summary: 'Sin problemas críticos.',
          model: 'claude-sonnet-4-5',
          attempts: 1,
          createdAt: BASE_AT,
          findings: [
            {
              severity: 'low',
              file: 'src/App.cs',
              line: 42,
              message: 'Considerar constante compartida.',
            },
          ],
        },
      ],
      pullRequest: {
        number: 77,
        url: prUrl,
        branchName: 'codenetix/fix-42',
        commitSha: 'abcdef0123456789',
        title: 'Fix: validar parámetros',
        createdAt: BASE_AT,
      },
      events: [
        {
          type: 'TokensConsumed',
          seq: 1,
          runId: RUN_ID,
          at: BASE_AT,
          role: 'analyst',
          provider: 'anthropic',
          model: 'claude-sonnet-4-5',
          inputTokens: 1000,
          outputTokens: 250,
        },
        {
          type: 'RiskClassified',
          seq: 2,
          runId: RUN_ID,
          at: BASE_AT,
          riskClass: 'NeedsAttention',
          diffLines: 45,
          matchedProtectedPaths: ['src/Security/Auth.cs'],
        },
      ],
    }
  }

  it('renderiza PullRequestCreated completo con enlace seguro rel="noopener noreferrer" y bloquea javascript:', () => {
    const safeHtml = renderToStaticMarkup(
      createElement(RunHistoryDetail, {
        detail: makeFullDetail('https://github.com/acme/web/pull/77'),
        onOpenLive: () => undefined,
      }),
    )
    expect(safeHtml).toContain('href="https://github.com/acme/web/pull/77"')
    expect(safeHtml).toContain('rel="noopener noreferrer"')
    expect(safeHtml).toContain('Requiere atención')
    expect(safeHtml).toContain('src/Security/Auth.cs')
    expect(safeHtml).toContain('src/App.cs:42')
    expect(safeHtml).toContain('No disponible')

    const unsafeHtml = renderToStaticMarkup(
      createElement(RunHistoryDetail, {
        detail: makeFullDetail('javascript:alert(1)'),
        onOpenLive: () => undefined,
      }),
    )
    expect(unsafeHtml).not.toContain('<a href=')
    expect(unsafeHtml).toContain('Enlace omitido por esquema no permitido')
  })

  it('renderiza una Ejecución Failed sin Spec, Veredictos, TestReport ni PR', () => {
    const failedDetail: RunResponse = {
      runId: RUN_ID,
      shortId: 'deadbeef',
      state: 'Failed',
      failureReason: 'CloneFailed',
      failureDetail: 'No se pudo clonar el repositorio remoto.',
      eventId: '22222222-2222-2222-2222-222222222222',
      objectKey: 'github:acme/private#99',
      createdAt: BASE_AT,
      updatedAt: '2026-11-22T10:00:10Z',
      spec: null,
      testReport: null,
      verdicts: [],
      pullRequest: null,
      events: [],
    }

    const html = renderToStaticMarkup(
      createElement(RunHistoryDetail, {
        detail: failedDetail,
        onOpenLive: () => undefined,
      }),
    )
    expect(html).toContain('CloneFailed')
    expect(html).toContain('No se pudo clonar el repositorio remoto.')
    expect(html).toContain('Esta Ejecución no generó una Spec.')
    expect(html).toContain('No hay Veredictos registrados para esta Ejecución.')
    expect(html).toContain('No hay informe de pruebas registrado para esta Ejecución.')
    expect(html).not.toContain('Pull Request #')
  })
})
