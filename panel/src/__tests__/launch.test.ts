import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  ApiHttpError,
  generateRequestId,
  submitManualEvent,
  UnauthorizedError,
  waitForEventRun,
} from '../api/client'
import type { EventResponse, SubmitManualEventResponse } from '../api/generated'
import { executeLaunchWork, LaunchWorkModal } from '../components/LaunchWorkModal'

const UUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const EVENT_ID = '33333333-3333-3333-3333-333333333333'
const RUN_ID = '11111111-1111-1111-1111-111111111111'

describe('generateRequestId', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('genera UUIDs válidos y distintos en llamadas consecutivas', () => {
    const first = generateRequestId()
    const second = generateRequestId()

    expect(first).toMatch(UUID_REGEX)
    expect(second).toMatch(UUID_REGEX)
    expect(first).not.toBe(second)
  })

  it('usa el respaldo RFC 4122 v4 cuando crypto.randomUUID no está disponible', () => {
    const originalCrypto = globalThis.crypto
    vi.stubGlobal('crypto', {
      getRandomValues: originalCrypto.getRandomValues.bind(originalCrypto),
    })

    try {
      const id = generateRequestId()
      expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i)
    } finally {
      vi.unstubAllGlobals()
    }
  })
})

describe('submitManualEvent', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('devuelve SubmitManualEventResponse ante 201 Created, 200 OK e Ignored', async () => {
    const createdBody: SubmitManualEventResponse = {
      eventId: EVENT_ID,
      created: true,
      status: 'Accepted',
      reason: null,
      actor: 'octocat',
      repository: 'acme/web',
    }
    const existingBody: SubmitManualEventResponse = {
      ...createdBody,
      created: false,
    }
    const ignoredBody: SubmitManualEventResponse = {
      eventId: EVENT_ID,
      created: true,
      status: 'Ignored',
      reason: 'no autorizado',
      actor: 'externo',
      repository: 'acme/web',
    }

    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify(createdBody), {
          status: 201,
          headers: { 'Content-Type': 'application/json' },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(existingBody), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(ignoredBody), {
          status: 201,
          headers: { 'Content-Type': 'application/json' },
        }),
      )
    vi.stubGlobal('fetch', fetchMock)

    const controller = new AbortController()
    const reqId = '44444444-4444-4444-8444-444444444444'

    const first = await submitManualEvent(
      'secret-token',
      'https://github.com/acme/web/issues/42',
      reqId,
      controller.signal,
    )
    expect(first).toEqual(createdBody)
    expect(fetchMock).toHaveBeenCalledWith('/api/events/manual', {
      method: 'POST',
      signal: controller.signal,
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        Authorization: 'Bearer secret-token',
      },
      body: JSON.stringify({
        url: 'https://github.com/acme/web/issues/42',
        requestId: reqId,
      }),
    })

    const second = await submitManualEvent(
      'secret-token',
      'https://github.com/acme/web/issues/42',
      reqId,
    )
    expect(second).toEqual(existingBody)

    const ignored = await submitManualEvent(
      'secret-token',
      'https://github.com/acme/web/issues/43',
      generateRequestId(),
    )
    expect(ignored).toEqual(ignoredBody)
  })

  it('preserva exactamente el detail de ProblemDetails ante 400, 501 y 503 con ApiHttpError', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            title: 'Petición no válida',
            detail: 'Solo se admiten issues de GitHub (https://github.com/<owner>/<repo>/issues/<número>).',
          }),
          { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            title: 'Fuente no disponible',
            detail: 'Soporte de Sentry disponible próximamente.',
          }),
          { status: 501, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            title: 'CodeNetix no está listo',
            detail: 'CodeNetix está en modo diagnóstico y no acepta trabajo hasta completar la configuración:\n- Falta token',
          }),
          { status: 503, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      )
    vi.stubGlobal('fetch', fetchMock)

    await expect(
      submitManualEvent('tok', 'https://github.com/acme/web/pull/10', generateRequestId()),
    ).rejects.toMatchObject({
      name: 'ApiHttpError',
      status: 400,
      message: 'Solo se admiten issues de GitHub (https://github.com/<owner>/<repo>/issues/<número>).',
    })

    await expect(
      submitManualEvent('tok', 'https://acme.sentry.io/issues/123/', generateRequestId()),
    ).rejects.toMatchObject({
      name: 'ApiHttpError',
      status: 501,
      message: 'Soporte de Sentry disponible próximamente.',
    })

    await expect(
      submitManualEvent('tok', 'https://github.com/acme/web/issues/42', generateRequestId()),
    ).rejects.toMatchObject({
      name: 'ApiHttpError',
      status: 503,
      message: 'CodeNetix está en modo diagnóstico y no acepta trabajo hasta completar la configuración:\n- Falta token',
    })
  })

  it('lanza UnauthorizedError ante 401', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValueOnce(new Response(null, { status: 401 })),
    )

    await expect(
      submitManualEvent('bad-token', 'https://github.com/acme/web/issues/42', generateRequestId()),
    ).rejects.toBeInstanceOf(UnauthorizedError)
  })
})

describe('waitForEventRun', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('sondea hasta recibir runId', async () => {
    const pending: EventResponse = {
      eventId: EVENT_ID,
      status: 'Accepted',
      reason: null,
      runId: null,
    }
    const ready: EventResponse = {
      eventId: EVENT_ID,
      status: 'Accepted',
      reason: null,
      runId: RUN_ID,
    }

    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify(pending), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(ready), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      )
    vi.stubGlobal('fetch', fetchMock)

    const result = await waitForEventRun('tok', EVENT_ID, {
      pollIntervalMs: 1,
      timeoutMs: 500,
    })

    expect(result).toEqual({ kind: 'run', runId: RUN_ID })
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('se detiene al recibir Ignored con su reason', async () => {
    const ignored: EventResponse = {
      eventId: EVENT_ID,
      status: 'Ignored',
      reason: 'objeto ocupado',
      runId: null,
    }

    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValueOnce(
        new Response(JSON.stringify(ignored), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )

    const result = await waitForEventRun('tok', EVENT_ID, {
      pollIntervalMs: 1,
      timeoutMs: 500,
    })

    expect(result).toEqual({ kind: 'ignored', reason: 'objeto ocupado' })
  })

  it('relanza UnauthorizedError inmediatamente ante 401', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(new Response(null, { status: 401 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(
      waitForEventRun('bad-tok', EVENT_ID, { pollIntervalMs: 1, timeoutMs: 500 }),
    ).rejects.toBeInstanceOf(UnauthorizedError)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('relanza ApiHttpError inmediatamente ante 4xx (p. ej. 404)', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      new Response(JSON.stringify({ detail: 'Evento no encontrado.' }), {
        status: 404,
        headers: { 'Content-Type': 'application/problem+json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await expect(
      waitForEventRun('tok', EVENT_ID, { pollIntervalMs: 1, timeoutMs: 500 }),
    ).rejects.toBeInstanceOf(ApiHttpError)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('reintenta ante errores transitorios 503 y de red hasta recibir runId', async () => {
    const ready: EventResponse = {
      eventId: EVENT_ID,
      status: 'Accepted',
      reason: null,
      runId: RUN_ID,
    }

    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ detail: 'Temporalmente no disponible' }), {
          status: 503,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      )
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce(
        new Response(JSON.stringify(ready), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      )
    vi.stubGlobal('fetch', fetchMock)

    const result = await waitForEventRun('tok', EVENT_ID, {
      pollIntervalMs: 1,
      timeoutMs: 500,
    })

    expect(result).toEqual({ kind: 'run', runId: RUN_ID })
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('rechaza inmediatamente sin llamar a fetch si AbortSignal ya estaba abortado', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const controller = new AbortController()
    controller.abort()

    await expect(
      waitForEventRun('tok', EVENT_ID, {
        pollIntervalMs: 1,
        timeoutMs: 500,
        signal: controller.signal,
      }),
    ).rejects.toMatchObject({ name: 'AbortError' })
    expect(fetchMock).toHaveBeenCalledTimes(0)
  })

  it('cancela limpiamente cuando se aborta AbortSignal durante el sleep entre sondeos', async () => {
    const pending: EventResponse = {
      eventId: EVENT_ID,
      status: 'Accepted',
      reason: null,
      runId: null,
    }

    const controller = new AbortController()
    const fetchMock = vi.fn().mockImplementation(async () => {
      setTimeout(() => controller.abort(), 5)
      return new Response(JSON.stringify(pending), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    })
    vi.stubGlobal('fetch', fetchMock)

    await expect(
      waitForEventRun('tok', EVENT_ID, {
        pollIntervalMs: 5_000,
        timeoutMs: 10_000,
        signal: controller.signal,
      }),
    ).rejects.toMatchObject({ name: 'AbortError' })
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('devuelve timeout al agotar timeoutMs sin runId', async () => {
    const pending: EventResponse = {
      eventId: EVENT_ID,
      status: 'Accepted',
      reason: null,
      runId: null,
    }

    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(
        async () =>
          new Response(JSON.stringify(pending), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    const result = await waitForEventRun('tok', EVENT_ID, {
      pollIntervalMs: 1,
      timeoutMs: 15,
    })

    expect(result).toEqual({ kind: 'timeout' })
  })
})

describe('executeLaunchWork', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('envía dos lanzamientos consecutivos con requestId distintos y navega vía onRunLaunched', async () => {
    const sentRequestIds: string[] = []
    const fetchMock = vi.fn().mockImplementation(async (input: string, init?: RequestInit) => {
      if (input === '/api/events/manual') {
        const parsed = JSON.parse(String(init?.body)) as { requestId: string }
        sentRequestIds.push(parsed.requestId)
        return new Response(
          JSON.stringify({
            eventId: EVENT_ID,
            created: true,
            status: 'Accepted',
            reason: null,
            actor: 'octocat',
            repository: 'acme/web',
          } satisfies SubmitManualEventResponse),
          { status: 201, headers: { 'Content-Type': 'application/json' } },
        )
      }
      return new Response(
        JSON.stringify({
          eventId: EVENT_ID,
          status: 'Accepted',
          reason: null,
          runId: RUN_ID,
        } satisfies EventResponse),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      )
    })
    vi.stubGlobal('fetch', fetchMock)

    const onRunLaunched = vi.fn()
    const onRefreshRuns = vi.fn()
    const onLogout = vi.fn()
    const onInfo = vi.fn()
    const onError = vi.fn()

    await executeLaunchWork({
      token: 'tok',
      url: '  https://github.com/acme/web/issues/42  ',
      signal: new AbortController().signal,
      pollIntervalMs: 1,
      timeoutMs: 200,
      onInfo,
      onError,
      onRunLaunched,
      onRefreshRuns,
      onLogout,
    })

    await executeLaunchWork({
      token: 'tok',
      url: 'https://github.com/acme/web/issues/43',
      signal: new AbortController().signal,
      pollIntervalMs: 1,
      timeoutMs: 200,
      onInfo,
      onError,
      onRunLaunched,
      onRefreshRuns,
      onLogout,
    })

    expect(sentRequestIds).toHaveLength(2)
    expect(sentRequestIds[0]).toMatch(UUID_REGEX)
    expect(sentRequestIds[1]).toMatch(UUID_REGEX)
    expect(sentRequestIds[0]).not.toBe(sentRequestIds[1])
    expect(onRunLaunched).toHaveBeenCalledTimes(2)
    expect(onRunLaunched).toHaveBeenCalledWith(RUN_ID)
    expect(onLogout).not.toHaveBeenCalled()
  })

  it('propaga exactamente el error 400 y 501 del servidor en onError sin llamar onRunLaunched', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ detail: 'URL inválida exacta del servidor.' }), {
          status: 400,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ detail: 'Soporte de Sentry disponible próximamente.' }), {
          status: 501,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      )
    vi.stubGlobal('fetch', fetchMock)

    const onRunLaunched = vi.fn()
    const onError = vi.fn()

    await executeLaunchWork({
      token: 'tok',
      url: 'no-es-una-url',
      signal: new AbortController().signal,
      onInfo: vi.fn(),
      onError,
      onRunLaunched,
      onRefreshRuns: vi.fn(),
      onLogout: vi.fn(),
    })

    await executeLaunchWork({
      token: 'tok',
      url: 'https://acme.sentry.io/issues/123/',
      signal: new AbortController().signal,
      onInfo: vi.fn(),
      onError,
      onRunLaunched,
      onRefreshRuns: vi.fn(),
      onLogout: vi.fn(),
    })

    expect(onError).toHaveBeenCalledWith('URL inválida exacta del servidor.')
    expect(onError).toHaveBeenCalledWith('Soporte de Sentry disponible próximamente.')
    expect(onRunLaunched).not.toHaveBeenCalled()
  })

  it('muestra el motivo cuando POST o el sondeo responden Ignored sin llamar onRunLaunched', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            eventId: EVENT_ID,
            created: true,
            status: 'Ignored',
            reason: 'no autorizado',
            actor: 'externo',
            repository: 'acme/web',
          } satisfies SubmitManualEventResponse),
          { status: 201, headers: { 'Content-Type': 'application/json' } },
        ),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            eventId: EVENT_ID,
            created: true,
            status: 'Accepted',
            reason: null,
            actor: 'octocat',
            repository: 'acme/web',
          } satisfies SubmitManualEventResponse),
          { status: 201, headers: { 'Content-Type': 'application/json' } },
        ),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            eventId: EVENT_ID,
            status: 'Ignored',
            reason: 'objeto ocupado',
            runId: null,
          } satisfies EventResponse),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      )
    vi.stubGlobal('fetch', fetchMock)

    const onError = vi.fn()
    const onRunLaunched = vi.fn()

    await executeLaunchWork({
      token: 'tok',
      url: 'https://github.com/acme/web/issues/42',
      signal: new AbortController().signal,
      pollIntervalMs: 1,
      timeoutMs: 200,
      onInfo: vi.fn(),
      onError,
      onRunLaunched,
      onRefreshRuns: vi.fn(),
      onLogout: vi.fn(),
    })

    await executeLaunchWork({
      token: 'tok',
      url: 'https://github.com/acme/web/issues/42',
      signal: new AbortController().signal,
      pollIntervalMs: 1,
      timeoutMs: 200,
      onInfo: vi.fn(),
      onError,
      onRunLaunched,
      onRefreshRuns: vi.fn(),
      onLogout: vi.fn(),
    })

    expect(onError).toHaveBeenCalledWith('Evento ignorado: no autorizado')
    expect(onError).toHaveBeenCalledWith('Evento ignorado: objeto ocupado')
    expect(onRunLaunched).not.toHaveBeenCalled()
  })

  it('invoca onLogout ante 401 en POST o en polling', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            eventId: EVENT_ID,
            created: true,
            status: 'Accepted',
            reason: null,
            actor: 'octocat',
            repository: 'acme/web',
          } satisfies SubmitManualEventResponse),
          { status: 201, headers: { 'Content-Type': 'application/json' } },
        ),
      )
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
    vi.stubGlobal('fetch', fetchMock)

    const onLogout = vi.fn()

    await executeLaunchWork({
      token: 'bad',
      url: 'https://github.com/acme/web/issues/42',
      signal: new AbortController().signal,
      onInfo: vi.fn(),
      onError: vi.fn(),
      onRunLaunched: vi.fn(),
      onRefreshRuns: vi.fn(),
      onLogout,
    })

    await executeLaunchWork({
      token: 'expired',
      url: 'https://github.com/acme/web/issues/42',
      signal: new AbortController().signal,
      pollIntervalMs: 1,
      timeoutMs: 200,
      onInfo: vi.fn(),
      onError: vi.fn(),
      onRunLaunched: vi.fn(),
      onRefreshRuns: vi.fn(),
      onLogout,
    })

    expect(onLogout).toHaveBeenCalledTimes(2)
  })

  it('informa de Evento registrado y llama onRefreshRuns cuando el sondeo agota timeoutMs', async () => {
    const fetchMock = vi.fn().mockImplementation(async (input: string) => {
      if (input === '/api/events/manual') {
        return new Response(
          JSON.stringify({
            eventId: EVENT_ID,
            created: true,
            status: 'Accepted',
            reason: null,
            actor: 'octocat',
            repository: 'acme/web',
          } satisfies SubmitManualEventResponse),
          { status: 201, headers: { 'Content-Type': 'application/json' } },
        )
      }
      return new Response(
        JSON.stringify({
          eventId: EVENT_ID,
          status: 'Accepted',
          reason: null,
          runId: null,
        } satisfies EventResponse),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      )
    })
    vi.stubGlobal('fetch', fetchMock)

    const onInfo = vi.fn()
    const onRefreshRuns = vi.fn()
    const onRunLaunched = vi.fn()

    await executeLaunchWork({
      token: 'tok',
      url: 'https://github.com/acme/web/issues/42',
      signal: new AbortController().signal,
      pollIntervalMs: 1,
      timeoutMs: 10,
      onInfo,
      onError: vi.fn(),
      onRunLaunched,
      onRefreshRuns,
      onLogout: vi.fn(),
    })

    expect(onRefreshRuns).toHaveBeenCalledTimes(1)
    expect(onRunLaunched).not.toHaveBeenCalled()
    expect(onInfo).toHaveBeenLastCalledWith(
      'El Evento ya fue registrado y aparecerá en la lista en cuanto el Dispatcher lo tome.',
    )
  })

  it('no dispara callbacks finales si se aborta con AbortSignal durante el sondeo', async () => {
    const controller = new AbortController()
    const fetchMock = vi.fn().mockImplementation(async (input: string) => {
      if (input === '/api/events/manual') {
        return new Response(
          JSON.stringify({
            eventId: EVENT_ID,
            created: true,
            status: 'Accepted',
            reason: null,
            actor: 'octocat',
            repository: 'acme/web',
          } satisfies SubmitManualEventResponse),
          { status: 201, headers: { 'Content-Type': 'application/json' } },
        )
      }
      setTimeout(() => controller.abort(), 5)
      return new Response(
        JSON.stringify({
          eventId: EVENT_ID,
          status: 'Accepted',
          reason: null,
          runId: null,
        } satisfies EventResponse),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      )
    })
    vi.stubGlobal('fetch', fetchMock)

    const onRunLaunched = vi.fn()
    const onRefreshRuns = vi.fn()
    const onLogout = vi.fn()
    const onError = vi.fn()

    await executeLaunchWork({
      token: 'tok',
      url: 'https://github.com/acme/web/issues/42',
      signal: controller.signal,
      pollIntervalMs: 5_000,
      timeoutMs: 10_000,
      onInfo: vi.fn(),
      onError,
      onRunLaunched,
      onRefreshRuns,
      onLogout,
    })

    expect(onRunLaunched).not.toHaveBeenCalled()
    expect(onRefreshRuns).not.toHaveBeenCalled()
    expect(onLogout).not.toHaveBeenCalled()
    expect(onError).toHaveBeenCalledTimes(1)
    expect(onError).toHaveBeenCalledWith(null)
  })
})

describe('LaunchWorkModal (SSR)', () => {
  it('devuelve marcado vacío cuando open es false', () => {
    const html = renderToStaticMarkup(
      createElement(LaunchWorkModal, {
        open: false,
        token: 'tok',
        onClose: () => undefined,
        onRunLaunched: () => undefined,
        onRefreshRuns: () => undefined,
        onLogout: () => undefined,
      }),
    )

    expect(html).toBe('')
  })

  it('renderiza diálogo accesible con input type="text" y botones de acción cuando está abierto', () => {
    const html = renderToStaticMarkup(
      createElement(LaunchWorkModal, {
        open: true,
        token: 'tok',
        onClose: () => undefined,
        onRunLaunched: () => undefined,
        onRefreshRuns: () => undefined,
        onLogout: () => undefined,
      }),
    )

    expect(html).toContain('role="dialog"')
    expect(html).toContain('aria-modal="true"')
    expect(html).toContain('aria-labelledby="launch-work-title"')
    expect(html).toContain('type="text"')
    expect(html).not.toContain('type="url"')
    expect(html).toContain('placeholder="https://github.com/owner/repo/issues/123"')
    expect(html).toContain('>Lanzar</button>')
    expect(html).toContain('>Cancelar</button>')
  })
})
