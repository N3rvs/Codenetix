import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  fetchSecretsSettings,
  SecretsUpdateError,
  UnauthorizedError,
  updateSecrets,
} from '../api/client'
import type { SecretSettingResponse, SecretsSettingsResponse } from '../api/generated'
import {
  buildSecretsRequest,
  describeSecretStatus,
  executeSaveSecrets,
  healthFromSettings,
  ROTATION_NOTICE,
  SECRET_HELP_LINKS,
  SecretsSettings,
  shouldOfferSetup,
} from '../components/SecretsSettings'

const GITHUB_TOKEN = 'github_pat_11ABCDEFG0123456789_secretvalue'
const ANTHROPIC_KEY = 'sk-ant-api03-secretvalue-0123456789'

function secret(overrides: Partial<SecretSettingResponse> & Pick<SecretSettingResponse, 'id'>): SecretSettingResponse {
  return {
    configured: false,
    source: 'None',
    editable: true,
    environmentVariable: `CODENETIX__${overrides.id}`,
    hint: null,
    ...overrides,
  }
}

const DIAGNOSTIC_SETTINGS: SecretsSettingsResponse = {
  status: 'Diagnostic',
  reasons: ['Falta el token de GitHub: pégalo en Configuración del Panel o define la variable CODENETIX__GitHub__Token.'],
  keysMissing: true,
  secrets: [
    secret({ id: 'GitHubToken', environmentVariable: 'CODENETIX__GitHub__Token' }),
    secret({
      id: 'AnthropicApiKey',
      configured: true,
      source: 'Environment',
      editable: false,
      environmentVariable: 'CODENETIX__Providers__Anthropic__ApiKey',
      hint: '…6789',
    }),
    secret({ id: 'OpenAIApiKey', environmentVariable: 'CODENETIX__Providers__OpenAI__ApiKey' }),
  ],
  notice: null,
  checks: [],
}

const READY_SETTINGS: SecretsSettingsResponse = {
  status: 'Ready',
  reasons: [],
  keysMissing: false,
  secrets: [
    secret({ id: 'GitHubToken', configured: true, source: 'Panel', hint: '…alue' }),
    DIAGNOSTIC_SETTINGS.secrets[1],
    DIAGNOSTIC_SETTINGS.secrets[2],
  ],
  notice: null,
  checks: [{ id: 'GitHubToken', message: 'Token válido para la cuenta @octocat.' }],
}

function jsonResponse(body: unknown, status = 200, contentType = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': contentType } })
}

describe('fetchSecretsSettings / updateSecrets', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('GET con Bearer y PUT solo con las claves que llevan valor (recortadas)', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(DIAGNOSTIC_SETTINGS))
      .mockResolvedValueOnce(jsonResponse(READY_SETTINGS))
    vi.stubGlobal('fetch', fetchMock)

    expect(await fetchSecretsSettings('tok')).toEqual(DIAGNOSTIC_SETTINGS)
    expect(fetchMock).toHaveBeenNthCalledWith(1, '/api/settings/secrets', {
      signal: undefined,
      headers: { Accept: 'application/json', Authorization: 'Bearer tok' },
    })

    const saved = await updateSecrets('tok', { gitHubToken: `  ${GITHUB_TOKEN}  `, anthropicApiKey: '   ', openAIApiKey: null })
    expect(saved).toEqual(READY_SETTINGS)
    const [url, init] = fetchMock.mock.calls[1] as [string, RequestInit]
    expect(url).toBe('/api/settings/secrets')
    expect(init.method).toBe('PUT')
    expect(init.headers).toEqual({
      Accept: 'application/json',
      'Content-Type': 'application/json',
      Authorization: 'Bearer tok',
    })
    expect(JSON.parse(String(init.body))).toEqual({ gitHubToken: GITHUB_TOKEN })
  })

  it('422 → SecretsUpdateError con los motivos por clave; 401 → UnauthorizedError', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(
          jsonResponse(
            {
              title: 'No se ha guardado ninguna clave',
              detail: 'Alguna clave no ha pasado la comprobación: no se ha guardado nada.',
              errors: { gitHubToken: 'GitHub rechaza el token: no es válido, está mal copiado o ha caducado.' },
            },
            422,
            'application/problem+json',
          ),
        )
        .mockResolvedValueOnce(new Response(null, { status: 401 })),
    )

    const rejected = await updateSecrets('tok', { gitHubToken: GITHUB_TOKEN }).catch((err: unknown) => err)
    expect(rejected).toBeInstanceOf(SecretsUpdateError)
    expect(rejected).toMatchObject({
      status: 422,
      message: 'Alguna clave no ha pasado la comprobación: no se ha guardado nada.',
      fieldErrors: { GitHubToken: 'GitHub rechaza el token: no es válido, está mal copiado o ha caducado.' },
    })

    await expect(updateSecrets('bad', { gitHubToken: GITHUB_TOKEN })).rejects.toBeInstanceOf(UnauthorizedError)
  })
})

describe('buildSecretsRequest / describeSecretStatus / healthFromSettings', () => {
  it('solo envía claves editables con valor', () => {
    expect(
      buildSecretsRequest(
        { GitHubToken: ` ${GITHUB_TOKEN} `, AnthropicApiKey: ANTHROPIC_KEY, OpenAIApiKey: '  ' },
        DIAGNOSTIC_SETTINGS.secrets,
      ),
    ).toEqual({ gitHubToken: GITHUB_TOKEN })
  })

  it('describe el origen sin mostrar nunca el valor', () => {
    expect(describeSecretStatus(DIAGNOSTIC_SETTINGS.secrets[0])).toBe('Sin configurar.')
    expect(describeSecretStatus(DIAGNOSTIC_SETTINGS.secrets[1])).toBe(
      'Configurada por variable de entorno (CODENETIX__Providers__Anthropic__ApiKey). Solo se puede cambiar allí.',
    )
    expect(describeSecretStatus(READY_SETTINGS.secrets[0])).toBe(
      'Guardada desde el Panel (termina en …alue). Pega otra para cambiarla.',
    )
    expect(describeSecretStatus(secret({ id: 'GitHubToken', configured: true, source: 'ConfigFile', editable: false }))).toContain(
      'archivo de instancia',
    )
  })

  it('traduce la respuesta al estado de salud de la App', () => {
    expect(healthFromSettings(READY_SETTINGS)).toEqual({ status: 'Ready', reasons: [] })
    expect(healthFromSettings(DIAGNOSTIC_SETTINGS)).toEqual({ status: 'Diagnostic', reasons: DIAGNOSTIC_SETTINGS.reasons })
    expect(healthFromSettings({ ...DIAGNOSTIC_SETTINGS, status: 'Desconocido' }).status).toBe('Diagnostic')
  })
})

describe('executeSaveSecrets', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('sin claves que guardar no llama al servidor', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const onError = vi.fn()
    const onSaved = vi.fn()

    const saved = await executeSaveSecrets({
      token: 'tok',
      drafts: { AnthropicApiKey: ANTHROPIC_KEY },
      secrets: DIAGNOSTIC_SETTINGS.secrets,
      signal: new AbortController().signal,
      onError,
      onFieldErrors: vi.fn(),
      onSaved,
      onLogout: vi.fn(),
    })

    expect(saved).toBe(false)
    expect(fetchMock).not.toHaveBeenCalled()
    expect(onError).toHaveBeenCalledWith('Pega al menos una clave para guardarla.')
    expect(onSaved).not.toHaveBeenCalled()
  })

  it('guarda y entrega el estado nuevo (Ready)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(jsonResponse(READY_SETTINGS)))
    const onSaved = vi.fn()
    const onFieldErrors = vi.fn()

    const saved = await executeSaveSecrets({
      token: 'tok',
      drafts: { GitHubToken: GITHUB_TOKEN },
      secrets: DIAGNOSTIC_SETTINGS.secrets,
      signal: new AbortController().signal,
      onError: vi.fn(),
      onFieldErrors,
      onSaved,
      onLogout: vi.fn(),
    })

    expect(saved).toBe(true)
    expect(onFieldErrors).toHaveBeenCalledWith({})
    expect(onSaved).toHaveBeenCalledWith(READY_SETTINGS)
  })

  it('un rechazo muestra el motivo junto a la clave; un 401 cierra la sesión', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(
          jsonResponse({ detail: 'x', errors: { gitHubToken: 'GitHub rechaza el token.' } }, 422, 'application/problem+json'),
        )
        .mockResolvedValueOnce(new Response(null, { status: 401 })),
    )
    const onError = vi.fn()
    const onFieldErrors = vi.fn()
    const onLogout = vi.fn()
    const onSaved = vi.fn()
    const params = {
      token: 'tok',
      drafts: { GitHubToken: GITHUB_TOKEN },
      secrets: DIAGNOSTIC_SETTINGS.secrets,
      signal: new AbortController().signal,
      onError,
      onFieldErrors,
      onSaved,
      onLogout,
    }

    expect(await executeSaveSecrets(params)).toBe(false)
    expect(onFieldErrors).toHaveBeenLastCalledWith({ GitHubToken: 'GitHub rechaza el token.' })
    expect(onError).toHaveBeenLastCalledWith('No se ha guardado nada: revisa los mensajes de cada clave.')

    expect(await executeSaveSecrets(params)).toBe(false)
    expect(onLogout).toHaveBeenCalledTimes(1)
    expect(onSaved).not.toHaveBeenCalled()
  })
})

describe('SecretsSettings (SSR)', () => {
  it('pantalla inicial en diagnóstico: motivos, campos password, enlaces de ayuda y «Probar y guardar»', () => {
    const html = renderToStaticMarkup(
      createElement(SecretsSettings, {
        mode: 'setup',
        token: 'tok',
        initialSettings: DIAGNOSTIC_SETTINGS,
        onSaved: () => undefined,
        onLogout: () => undefined,
      }),
    )

    expect(html).toContain('Configura CodeNetix')
    expect(html).toContain('Modo diagnóstico')
    expect(html).toContain('Falta el token de GitHub')
    expect(html.match(/type="password"/g)).toHaveLength(3)
    expect(html).not.toContain('type="text"')
    expect(html).toContain('>Probar y guardar</button>')
    for (const href of Object.values(SECRET_HELP_LINKS)) {
      expect(html).toContain(`href="${href}"`)
    }
    expect(html).toContain('rel="noopener noreferrer"')
    expect(html).toContain('Contents')
    expect(html).toContain('Pull requests')
    expect(html).toContain('Metadata')
  })

  it('una clave del entorno se muestra de solo lectura con su variable y su pista', () => {
    const html = renderToStaticMarkup(
      createElement(SecretsSettings, {
        mode: 'setup',
        token: 'tok',
        initialSettings: DIAGNOSTIC_SETTINGS,
        onSaved: () => undefined,
        onLogout: () => undefined,
      }),
    )

    expect(html).toMatch(/<input id="secret-AnthropicApiKey" type="password"[^>]*disabled=""/)
    expect(html).not.toMatch(/<input id="secret-GitHubToken"[^>]*disabled=""/)
    expect(html).toContain('Configurada por variable de entorno (CODENETIX__Providers__Anthropic__ApiKey)')
    expect(html).toContain('placeholder="Gestionada fuera del Panel"')
  })

  it('el diálogo «Configuración» no se pinta cerrado y es un diálogo accesible abierto', () => {
    const closed = renderToStaticMarkup(
      createElement(SecretsSettings, {
        mode: 'modal',
        open: false,
        token: 'tok',
        initialSettings: READY_SETTINGS,
        onSaved: () => undefined,
        onLogout: () => undefined,
      }),
    )
    expect(closed).toBe('')

    const html = renderToStaticMarkup(
      createElement(SecretsSettings, {
        mode: 'modal',
        open: true,
        token: 'tok',
        initialSettings: READY_SETTINGS,
        onSaved: () => undefined,
        onLogout: () => undefined,
      }),
    )
    expect(html).toContain('role="dialog"')
    expect(html).toContain('aria-modal="true"')
    expect(html).toContain('aria-labelledby="secrets-settings-title"')
    expect(html).toContain('>Configuración</h2>')
    expect(html).toContain('Guardada desde el Panel (termina en …alue)')
    expect(html).toContain('>Cerrar</button>')
    expect(html).not.toContain(GITHUB_TOKEN)
    // Las claves se aplican al momento, también a las Ejecuciones en curso (no "terminan con las anteriores").
    expect(html).toContain(ROTATION_NOTICE)
    expect(html).not.toContain('terminan con las anteriores')
    expect(html.match(/autoComplete="new-password"|autocomplete="new-password"/gi)).toHaveLength(3)
  })

  it('solo ofrece la pantalla de configuración si falta una clave que se puede pegar aquí', () => {
    expect(shouldOfferSetup(DIAGNOSTIC_SETTINGS)).toBe(true)
    expect(shouldOfferSetup({ ...DIAGNOSTIC_SETTINGS, keysMissing: false })).toBe(false)
    expect(
      shouldOfferSetup({
        ...DIAGNOSTIC_SETTINGS,
        secrets: DIAGNOSTIC_SETTINGS.secrets.map((s) => ({ ...s, editable: false })),
      }),
    ).toBe(false)

    // Diagnóstico por Docker con las claves ya puestas: la guía de siempre, sin formulario.
    const html = renderToStaticMarkup(
      createElement(SecretsSettings, {
        mode: 'setup',
        token: 'tok',
        initialSettings: { ...DIAGNOSTIC_SETTINGS, keysMissing: false, reasons: ['Docker no responde.'] },
        onSaved: () => undefined,
        onLogout: () => undefined,
      }),
    )
    expect(html).toContain('CodeNetix está en modo diagnóstico')
    expect(html).toContain('Docker no responde.')
    expect(html).toContain('Define las variables que faltan y reinicia el contenedor.')
    expect(html).not.toContain('type="password"')
  })

  it('en la pantalla inicial ofrece «Ir al Panel» en cuanto la instancia está lista', () => {
    const html = renderToStaticMarkup(
      createElement(SecretsSettings, {
        mode: 'setup',
        token: 'tok',
        initialSettings: READY_SETTINGS,
        onSaved: () => undefined,
        onLogout: () => undefined,
      }),
    )
    expect(html).toContain('>Ir al Panel</button>')
    expect(html).not.toContain('>Probar y guardar</button>')
    expect(html).toContain('>Listo<')
  })
})
