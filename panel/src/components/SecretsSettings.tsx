import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import {
  fetchSecretsSettings,
  SECRET_FIELDS,
  SecretsUpdateError,
  UnauthorizedError,
  updateSecrets,
  type HealthInfo,
} from '../api/client'
import type {
  PanelSecretId,
  SecretSettingResponse,
  SecretsSettingsResponse,
  UpdateSecretsRequest,
} from '../api/generated'

/** Enlaces de ayuda (se abren en otra pestaña, sin `opener`). */
export const SECRET_HELP_LINKS = {
  gitHubToken: 'https://github.com/settings/personal-access-tokens/new',
  anthropicKeys: 'https://console.anthropic.com/settings/keys',
  openAIKeys: 'https://platform.openai.com/api-keys',
} as const

interface SecretCopy {
  label: string
  placeholder: string
  help: ReactNode
}

function HelpLink({ href, children }: { href: string; children: ReactNode }) {
  return (
    <a href={href} target="_blank" rel="noopener noreferrer">
      {children}
    </a>
  )
}

/** Textos de cada clave: lenguaje llano y qué hay que hacer para conseguirla. */
export const SECRET_COPY: Record<PanelSecretId, SecretCopy> = {
  GitHubToken: {
    label: 'Token de GitHub',
    placeholder: 'github_pat_… o ghp_…',
    help: (
      <>
        Crea un{' '}
        <HelpLink href={SECRET_HELP_LINKS.gitHubToken}>token fine-grained en GitHub</HelpLink>, dale acceso a los
        repositorios que quieras arreglar y estos permisos: <strong>Contents</strong>, <strong>Issues</strong> y{' '}
        <strong>Pull requests</strong> en «Read and write», y <strong>Metadata</strong> en «Read-only».
      </>
    ),
  },
  AnthropicApiKey: {
    label: 'Key de Anthropic',
    placeholder: 'sk-ant-…',
    help: (
      <>
        Crea una key en la <HelpLink href={SECRET_HELP_LINKS.anthropicKeys}>consola de Anthropic</HelpLink> (API keys).
        Es la que usan el Analista, el Programador y los Revisores.
      </>
    ),
  },
  OpenAIApiKey: {
    label: 'Key de OpenAI (opcional)',
    placeholder: 'sk-…',
    help: (
      <>
        Solo si quieres usar modelos de OpenAI. Créala en{' '}
        <HelpLink href={SECRET_HELP_LINKS.openAIKeys}>platform.openai.com</HelpLink>. Si también hay key de Anthropic,
        CodeNetix usa Anthropic salvo que se indique otra cosa.
      </>
    ),
  },
}

/** Las claves se aplican al momento en todo el proceso (el Egress Gateway las lee en cada petición). */
export const ROTATION_NOTICE =
  'Las claves nuevas se aplican al momento, también a las Ejecuciones que ya estén en marcha: si puedes, cámbialas cuando no haya ninguna en curso.'

/**
 * La pantalla de configuración solo tiene sentido si la instancia está en diagnóstico porque falta una clave y al menos
 * un campo se puede editar desde el Panel; si no (Docker caído, claves solo por entorno…), la guía de siempre.
 */
export function shouldOfferSetup(settings: SecretsSettingsResponse): boolean {
  return settings.keysMissing && settings.secrets.some((secret) => secret.editable)
}

/** Frase de estado de una clave, sin el valor (como mucho, sus 4 últimos caracteres). */
export function describeSecretStatus(secret: SecretSettingResponse): string {
  switch (secret.source) {
    case 'Environment':
      return `Configurada por variable de entorno (${secret.environmentVariable}). Solo se puede cambiar allí.`
    case 'ConfigFile':
      return 'Configurada en el archivo de instancia (codenetix.instance.yml). Solo se puede cambiar allí.'
    case 'Panel':
      return `Guardada desde el Panel${secret.hint ? ` (termina en ${secret.hint})` : ''}. Pega otra para cambiarla.`
    default:
      return 'Sin configurar.'
  }
}

export type SecretDrafts = Partial<Record<PanelSecretId, string>>

/** Cuerpo del `PUT`: solo las claves editables con valor (las demás no cambian). */
export function buildSecretsRequest(
  drafts: SecretDrafts,
  secrets: readonly SecretSettingResponse[],
): Partial<UpdateSecretsRequest> {
  const request: Partial<UpdateSecretsRequest> = {}
  for (const secret of secrets) {
    const value = drafts[secret.id]?.trim()
    if (secret.editable && value) {
      request[SECRET_FIELDS[secret.id]] = value
    }
  }
  return request
}

/** Estado de salud a partir de la respuesta de configuración (mismo formato que `/api/health`). */
export function healthFromSettings(settings: SecretsSettingsResponse): HealthInfo {
  const status = settings.status === 'Ready' || settings.status === 'Starting' ? settings.status : 'Diagnostic'
  return { status, reasons: settings.reasons }
}

export interface ExecuteSaveSecretsParams {
  token: string
  drafts: SecretDrafts
  secrets: readonly SecretSettingResponse[]
  signal: AbortSignal
  onError: (message: string | null) => void
  onFieldErrors: (errors: Partial<Record<PanelSecretId, string>>) => void
  onSaved: (settings: SecretsSettingsResponse) => void
  onLogout: (reason?: string) => void
}

/** «Probar y guardar»: valida en el servidor y guarda todo o nada. Devuelve `true` si se guardó. */
export async function executeSaveSecrets({
  token,
  drafts,
  secrets,
  signal,
  onError,
  onFieldErrors,
  onSaved,
  onLogout,
}: ExecuteSaveSecretsParams): Promise<boolean> {
  const request = buildSecretsRequest(drafts, secrets)
  if (Object.keys(request).length === 0) {
    onError('Pega al menos una clave para guardarla.')
    return false
  }

  onError(null)
  onFieldErrors({})
  try {
    const settings = await updateSecrets(token, request, signal)
    if (signal.aborted) {
      return false
    }
    onSaved(settings)
    return true
  } catch (err) {
    if (signal.aborted || (err instanceof Error && err.name === 'AbortError')) {
      return false
    }
    if (err instanceof UnauthorizedError) {
      onLogout(err.message)
      return false
    }
    if (err instanceof SecretsUpdateError) {
      onFieldErrors(err.fieldErrors)
      onError(
        Object.keys(err.fieldErrors).length > 0
          ? 'No se ha guardado nada: revisa los mensajes de cada clave.'
          : err.message,
      )
      return false
    }
    onError(err instanceof Error ? err.message : 'No se han podido guardar las claves.')
    return false
  }
}

export interface SecretsSettingsProps {
  token: string
  /** `setup`: pantalla completa en modo diagnóstico; `modal`: diálogo desde «Configuración». */
  mode: 'setup' | 'modal'
  /** Solo en `modal`. */
  open?: boolean
  /** Estado ya cargado (evita la primera petición; útil en pruebas). */
  initialSettings?: SecretsSettingsResponse | null
  onClose?: () => void
  onSaved: (settings: SecretsSettingsResponse) => void
  onLogout: (reason?: string) => void
}

export function SecretsSettings({
  token,
  mode,
  open = true,
  initialSettings = null,
  onClose,
  onSaved,
  onLogout,
}: SecretsSettingsProps) {
  const [settings, setSettings] = useState<SecretsSettingsResponse | null>(initialSettings)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [drafts, setDrafts] = useState<SecretDrafts>({})
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<PanelSecretId, string>>>({})
  const [success, setSuccess] = useState<SecretsSettingsResponse | null>(null)
  const abortRef = useRef<AbortController | null>(null)

  const visible = mode === 'setup' || open

  useEffect(() => {
    if (!visible) {
      abortRef.current?.abort()
      abortRef.current = null
      setDrafts({})
      setError(null)
      setFieldErrors({})
      setSuccess(null)
      setSaving(false)
      return
    }

    const controller = new AbortController()
    fetchSecretsSettings(token, controller.signal)
      .then((loaded) => {
        setSettings(loaded)
        setLoadError(null)
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return
        if (err instanceof UnauthorizedError) {
          onLogout(err.message)
          return
        }
        setLoadError(err instanceof Error ? err.message : 'No se ha podido cargar la configuración.')
      })

    return () => controller.abort()
  }, [visible, token, onLogout])

  useEffect(() => () => abortRef.current?.abort(), [])

  const handleClose = useCallback(() => {
    if (!saving) {
      onClose?.()
    }
  }, [saving, onClose])

  useEffect(() => {
    if (mode !== 'modal' || !open) {
      return
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        handleClose()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [mode, open, handleClose])

  if (!visible) {
    return null
  }

  const secrets = settings?.secrets ?? []
  const canSave = !saving && Object.keys(buildSecretsRequest(drafts, secrets)).length > 0
  // En la pantalla inicial: "Ir al Panel" en cuanto la instancia está lista (tras guardar aquí o por otra vía).
  const readySettings = success?.status === 'Ready' ? success : settings?.status === 'Ready' ? settings : null

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!canSave) {
      return
    }

    const controller = new AbortController()
    abortRef.current = controller
    setSaving(true)
    setSuccess(null)
    try {
      await executeSaveSecrets({
        token,
        drafts,
        secrets,
        signal: controller.signal,
        onError: setError,
        onFieldErrors: setFieldErrors,
        onSaved: (saved) => {
          setSettings(saved)
          setDrafts({})
          setSuccess(saved)
          // En la pantalla inicial, con la instancia ya lista, se muestra el éxito y se pasa al Panel con un botón.
          if (mode === 'modal' || saved.status !== 'Ready') {
            onSaved(saved)
          }
        },
        onLogout,
      })
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null
        setSaving(false)
      }
    }
  }

  const reasons = settings?.reasons ?? []
  const form = (
    <form className="modal-form secrets-form" onSubmit={(e) => void handleSubmit(e)} noValidate>
      {settings === null && !loadError ? <p className="modal-description">Cargando la configuración…</p> : null}
      {loadError ? (
        <div className="error-banner" role="alert">
          {loadError}
        </div>
      ) : null}
      {settings?.notice ? (
        <div className="warning-banner" role="status">
          {settings.notice}
        </div>
      ) : null}

      {secrets.map((secret) => {
        const copy = SECRET_COPY[secret.id]
        const inputId = `secret-${secret.id}`
        const statusId = `${inputId}-status`
        const fieldError = fieldErrors[secret.id]
        return (
          <fieldset key={secret.id} className="secret-field" disabled={saving}>
            <div className="secret-field-head">
              <label htmlFor={inputId}>{copy.label}</label>
              <span className={secret.configured ? 'pill pill-live' : 'pill'}>
                {secret.configured ? 'Configurada' : 'Pendiente'}
              </span>
            </div>
            <p className="secret-help">{copy.help}</p>
            <input
              id={inputId}
              type="password"
              autoComplete="new-password"
              spellCheck={false}
              placeholder={secret.editable ? copy.placeholder : 'Gestionada fuera del Panel'}
              value={drafts[secret.id] ?? ''}
              onChange={(e) => {
                const value = e.target.value
                setDrafts((current) => ({ ...current, [secret.id]: value }))
              }}
              disabled={!secret.editable}
              aria-describedby={statusId}
              aria-invalid={fieldError ? true : undefined}
            />
            <p id={statusId} className="secret-status">
              {describeSecretStatus(secret)}
            </p>
            {fieldError ? (
              <div className="field-error" role="alert">
                {fieldError}
              </div>
            ) : null}
          </fieldset>
        )
      })}

      {error ? (
        <div className="error-banner" role="alert">
          {error}
        </div>
      ) : null}

      {success ? (
        <div className="success-banner" role="status">
          <strong>
            {success.status === 'Ready'
              ? 'Claves guardadas. CodeNetix está listo.'
              : 'Claves guardadas, pero aún falta algo (mira la lista de arriba).'}
          </strong>
          {success.checks.length > 0 ? (
            <ul>
              {success.checks.map((check) => (
                <li key={check.id}>{check.message}</li>
              ))}
            </ul>
          ) : null}
        </div>
      ) : null}

      <p className="secret-footnote">
        Las claves se comprueban con GitHub y con el Proveedor antes de guardarse, se guardan cifradas en el directorio de
        datos y nunca se vuelven a mostrar. Se aplican sin reiniciar.{mode === 'setup' ? '' : ` ${ROTATION_NOTICE}`}
      </p>

      <div className="modal-actions">
        {mode === 'modal' ? (
          <button type="button" className="btn" onClick={handleClose} disabled={saving}>
            Cerrar
          </button>
        ) : null}
        {mode === 'setup' && readySettings ? (
          <button type="button" className="btn btn-primary" onClick={() => onSaved(readySettings)}>
            Ir al Panel
          </button>
        ) : (
          <button type="submit" className="btn btn-primary" disabled={!canSave}>
            {saving ? 'Probando…' : 'Probar y guardar'}
          </button>
        )}
      </div>
    </form>
  )

  if (mode === 'setup' && settings && settings.status === 'Diagnostic' && !success && !shouldOfferSetup(settings)) {
    // Diagnóstico que no se arregla con una clave desde aquí: la guía de siempre con los motivos.
    return (
      <main className="status-screen">
        <section className="status-card">
          <h1>CodeNetix está en modo diagnóstico</h1>
          <p>La instancia está en marcha, pero no procesará trabajo hasta que completes la configuración:</p>
          <ul>
            {reasons.map((reason) => (
              <li key={reason}>{reason}</li>
            ))}
          </ul>
          <p>Define las variables que faltan y reinicia el contenedor.</p>
        </section>
      </main>
    )
  }

  if (mode === 'setup') {
    return (
      <main className="status-screen">
        <section className="status-card status-card-wide" aria-labelledby="secrets-setup-title">
          <div className="secret-field-head">
            <h1 id="secrets-setup-title">Configura CodeNetix</h1>
            <span className={settings?.status === 'Ready' ? 'pill pill-live' : 'pill pill-reconnecting'}>
              {settings?.status === 'Ready' ? 'Listo' : 'Modo diagnóstico'}
            </span>
          </div>
          <p>
            CodeNetix está en marcha, pero necesita tus claves para trabajar. Pégalas aquí: se comprueban al momento y
            no hace falta reiniciar nada.
          </p>
          {reasons.length > 0 ? (
            <ul>
              {reasons.map((reason) => (
                <li key={reason}>{reason}</li>
              ))}
            </ul>
          ) : null}
          {form}
        </section>
      </main>
    )
  }

  return (
    <div
      className="modal-backdrop"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          handleClose()
        }
      }}
    >
      <div className="modal-card secrets-modal" role="dialog" aria-modal="true" aria-labelledby="secrets-settings-title">
        <div className="modal-header">
          <h2 id="secrets-settings-title">Configuración</h2>
          <button type="button" className="btn btn-sm" onClick={handleClose} disabled={saving} aria-label="Cerrar">
            Cerrar
          </button>
        </div>
        <p className="modal-description">
          Cambia o renueva el token de GitHub y las keys de los Proveedores. {ROTATION_NOTICE}
        </p>
        {form}
      </div>
    </div>
  )
}
