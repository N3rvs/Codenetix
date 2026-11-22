import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cancelRun, isRunCancellable, UnauthorizedError } from '../api/client'
import type { CancelRunResponse, RunResponse, RunState } from '../api/generated'
import { RunHistoryDetail } from '../components/RunHistoryView'

const RUN_ID = '11111111-1111-1111-1111-111111111111'
const BASE_AT = '2026-11-22T10:00:00Z'

function makeDetail(state: RunState): RunResponse {
  return {
    runId: RUN_ID,
    shortId: 'a1b2c3d4',
    state,
    failureReason: state === 'Failed' ? 'TestsFailed' : null,
    failureDetail: state === 'Failed' ? 'Error en pruebas' : null,
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
}

describe('isRunCancellable', () => {
  const cases: ReadonlyArray<{ state: RunState; expected: boolean }> = [
    { state: 'Received', expected: true },
    { state: 'Analyzing', expected: true },
    { state: 'Coding', expected: true },
    { state: 'Reviewing', expected: true },
    { state: 'Deciding', expected: false },
    { state: 'PullRequestCreated', expected: false },
    { state: 'Failed', expected: false },
    { state: 'Cancelled', expected: false },
  ]

  it('devuelve true solo en Received, Analyzing, Coding y Reviewing y false en Deciding y estados terminales', () => {
    for (const { state, expected } of cases) {
      expect(isRunCancellable(state), `estado ${state}`).toBe(expected)
    }
  })
})

describe('cancelRun', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('envía POST con Bearer token y devuelve CancelRunResponse ante 202 Accepted', async () => {
    const expected: CancelRunResponse = {
      runId: RUN_ID,
      shortId: 'a1b2c3d4',
      state: 'Coding',
      cancellationRequested: true,
    }

    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(expected), {
        status: 202,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const controller = new AbortController()
    const result = await cancelRun('secret-token', RUN_ID, controller.signal)

    expect(result).toEqual(expected)
    expect(fetchMock).toHaveBeenCalledWith(`/api/runs/${encodeURIComponent(RUN_ID)}/cancel`, {
      method: 'POST',
      signal: controller.signal,
      headers: {
        Accept: 'application/json',
        Authorization: 'Bearer secret-token',
      },
    })
  })

  it('propaga el detalle de ProblemDetails ante 409 Conflict y 404 Not Found', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            title: 'No se puede cancelar la Ejecución',
            detail: 'La Ejecución ya está en Deciding (publicando el PR) y no puede cancelarse.',
          }),
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )
    await expect(cancelRun('tok', RUN_ID)).rejects.toThrow(
      'La Ejecución ya está en Deciding (publicando el PR) y no puede cancelarse.',
    )

    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            title: 'Ejecución no encontrada',
            detail: 'No existe ninguna Ejecución con ese id (GUID o shortId de 8 caracteres).',
          }),
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )
    await expect(cancelRun('tok', 'no-existe')).rejects.toThrow(
      'No existe ninguna Ejecución con ese id (GUID o shortId de 8 caracteres).',
    )
  })

  it('lanza UnauthorizedError ante 401', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValueOnce(new Response(null, { status: 401 })),
    )
    await expect(cancelRun('bad-token', RUN_ID)).rejects.toBeInstanceOf(UnauthorizedError)
  })
})

describe('RunHistoryDetail (botón Cancelar)', () => {
  function extractCancelButtonTag(html: string): string {
    const match = html.match(/<button[^>]*>Cancelar<\/button>/)
    expect(match, 'Debe existir un botón con texto Cancelar').not.toBeNull()
    return match![0]
  }

  it.each<RunState>(['Received', 'Analyzing', 'Coding', 'Reviewing'])(
    'renderiza el botón Cancelar habilitado en %s',
    (state) => {
      const html = renderToStaticMarkup(
        createElement(RunHistoryDetail, {
          detail: makeDetail(state),
          onOpenLive: () => undefined,
        }),
      )
      const buttonTag = extractCancelButtonTag(html)
      expect(buttonTag).not.toContain('disabled')
    },
  )

  it.each<RunState>(['Deciding', 'PullRequestCreated', 'Failed', 'Cancelled'])(
    'renderiza el botón Cancelar deshabilitado en %s',
    (state) => {
      const html = renderToStaticMarkup(
        createElement(RunHistoryDetail, {
          detail: makeDetail(state),
          onOpenLive: () => undefined,
        }),
      )
      const buttonTag = extractCancelButtonTag(html)
      expect(buttonTag).toContain('disabled')
    },
  )

  it('renderiza el botón deshabilitado con texto Cancelando… cuando cancelling es true y muestra cancelError en role="alert"', () => {
    const html = renderToStaticMarkup(
      createElement(RunHistoryDetail, {
        detail: makeDetail('Coding'),
        onOpenLive: () => undefined,
        cancelling: true,
        cancelError: 'La Ejecución ya entró en Deciding.',
      }),
    )
    const cancellingMatch = html.match(/<button[^>]*>Cancelando…<\/button>/)
    expect(cancellingMatch, 'Debe mostrar botón Cancelando…').not.toBeNull()
    expect(cancellingMatch![0]).toContain('disabled')
    expect(html).toContain('role="alert"')
    expect(html).toContain('La Ejecución ya entró en Deciding.')
  })
})
