import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fetchIgnoredEvents, UnauthorizedError } from '../api/client'
import type { IgnoredEventSummary } from '../api/generated'
import { IgnoredEventsList } from '../components/IgnoredEventsList'

const SENTRY_EVENT: IgnoredEventSummary = {
  eventId: '33333333-3333-3333-3333-333333333333',
  source: 'Sentry',
  objectKey: 'sentry:123',
  reason:
    "el proyecto de Sentry 'front-end' no tiene Repositorio objetivo: define CODENETIX__Sentry__Projects__front-end=owner/repo",
  receivedAt: '2026-11-23T10:00:00Z',
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('fetchIgnoredEvents', () => {
  it('pide /api/events/ignored con el admin-token y devuelve la lista', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify([SENTRY_EVENT]), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    const events = await fetchIgnoredEvents('tok', 20)

    expect(events).toEqual([SENTRY_EVENT])
    expect(fetchMock).toHaveBeenCalledWith('/api/events/ignored?limit=20', {
      signal: undefined,
      headers: { Accept: 'application/json', Authorization: 'Bearer tok' },
    })
  })

  it('lanza UnauthorizedError con 401', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 401 })))

    await expect(fetchIgnoredEvents('bad')).rejects.toBeInstanceOf(UnauthorizedError)
  })
})

describe('IgnoredEventsList', () => {
  it('muestra la Fuente, el objeto y el motivo de cada Evento ignorado', () => {
    const html = renderToStaticMarkup(
      createElement(IgnoredEventsList, { events: [SENTRY_EVENT], loading: false, error: null }),
    )

    expect(html).toContain('Eventos ignorados (1)')
    expect(html).toContain('Sentry')
    expect(html).toContain('sentry:123')
    expect(html).toContain('CODENETIX__Sentry__Projects__front-end')
  })

  it('escapa el texto (nunca HTML) y muestra un estado vacío', () => {
    const hostile = { ...SENTRY_EVENT, reason: '<img src=x onerror=alert(1)>' }
    const html = renderToStaticMarkup(
      createElement(IgnoredEventsList, { events: [hostile], loading: false, error: null }),
    )
    expect(html).not.toContain('<img')
    expect(html).toContain('&lt;img')

    const empty = renderToStaticMarkup(createElement(IgnoredEventsList, { events: [], loading: false, error: null }))
    expect(empty).toContain('No hay Eventos ignorados recientes.')
  })
})
