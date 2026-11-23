import { useCallback, useEffect, useRef, useState } from 'react'
import {
  generateRequestId,
  submitManualEvent,
  UnauthorizedError,
  waitForEventRun,
} from '../api/client'

export interface LaunchWorkModalProps {
  open: boolean
  token: string
  onClose: () => void
  onRunLaunched: (runId: string) => void
  onRefreshRuns: () => void
  onLogout: (reason?: string) => void
}

export type LaunchPhase = 'idle' | 'submitting' | 'waiting'

export interface ExecuteLaunchWorkParams {
  token: string
  url: string
  signal: AbortSignal
  generateId?: () => string
  pollIntervalMs?: number
  timeoutMs?: number
  onPhaseChange?: (phase: LaunchPhase) => void
  onInfo: (message: string | null) => void
  onError: (message: string | null) => void
  onRunLaunched: (runId: string) => void
  onRefreshRuns: () => void
  onLogout: (reason?: string) => void
}

function isAbortError(err: unknown): boolean {
  return err instanceof Error && err.name === 'AbortError'
}

const ACCEPTED_PENDING_MESSAGE =
  'El Evento ya fue registrado y aparecerá en la lista en cuanto el Dispatcher lo tome.'

export async function executeLaunchWork({
  token,
  url,
  signal,
  generateId = generateRequestId,
  pollIntervalMs,
  timeoutMs,
  onPhaseChange,
  onInfo,
  onError,
  onRunLaunched,
  onRefreshRuns,
  onLogout,
}: ExecuteLaunchWorkParams): Promise<void> {
  const trimmedUrl = url.trim()
  if (!trimmedUrl || signal.aborted) {
    return
  }

  const requestId = generateId()
  onPhaseChange?.('submitting')
  onError(null)
  onInfo(null)

  let response
  try {
    response = await submitManualEvent(token, trimmedUrl, requestId, signal)
  } catch (err) {
    if (signal.aborted || isAbortError(err)) {
      return
    }
    onPhaseChange?.('idle')
    onInfo(null)
    if (err instanceof UnauthorizedError) {
      onLogout(err.message)
      return
    }
    onError(err instanceof Error ? err.message : 'No se pudo lanzar el trabajo.')
    return
  }

  if (signal.aborted) {
    return
  }

  if (response.status === 'Ignored') {
    onPhaseChange?.('idle')
    onError(`Evento ignorado: ${response.reason ?? 'sin motivo especificado'}`)
    return
  }

  if (response.status !== 'Accepted') {
    onPhaseChange?.('idle')
    onError(`Respuesta inesperada del servidor (estado: ${response.status}).`)
    return
  }

  onPhaseChange?.('waiting')
  onInfo('Evento aceptado. Esperando a que arranque la Ejecución…')

  try {
    const waitResult = await waitForEventRun(token, response.eventId, {
      pollIntervalMs,
      timeoutMs,
      signal,
    })
    if (signal.aborted) {
      return
    }

    if (waitResult.kind === 'run') {
      onPhaseChange?.('idle')
      onRunLaunched(waitResult.runId)
      return
    }

    if (waitResult.kind === 'ignored') {
      onPhaseChange?.('idle')
      onInfo(null)
      onError(`Evento ignorado: ${waitResult.reason ?? 'sin motivo especificado'}`)
      return
    }

    onPhaseChange?.('idle')
    onInfo(ACCEPTED_PENDING_MESSAGE)
    onRefreshRuns()
  } catch (err) {
    if (signal.aborted || isAbortError(err)) {
      return
    }
    onPhaseChange?.('idle')
    if (err instanceof UnauthorizedError) {
      onInfo(null)
      onLogout(err.message)
      return
    }
    // El Evento ya fue registrado por el servidor: conservar el aviso y evitar fomentar reenvíos duplicados.
    onInfo(ACCEPTED_PENDING_MESSAGE)
    onError(err instanceof Error ? err.message : 'No se pudo comprobar el estado del Evento.')
    onRefreshRuns()
  }
}

export function LaunchWorkModal({
  open,
  token,
  onClose,
  onRunLaunched,
  onRefreshRuns,
  onLogout,
}: LaunchWorkModalProps) {
  const [url, setUrl] = useState('')
  const [phase, setPhase] = useState<LaunchPhase>('idle')
  const [error, setError] = useState<string | null>(null)
  const [info, setInfo] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)

  const cancelPending = useCallback(() => {
    abortRef.current?.abort()
    abortRef.current = null
  }, [])

  const handleClose = useCallback(() => {
    if (phase === 'submitting') {
      return
    }
    const wasWaiting = phase === 'waiting'
    cancelPending()
    setPhase('idle')
    if (wasWaiting) {
      onRefreshRuns()
    }
    onClose()
  }, [phase, cancelPending, onRefreshRuns, onClose])

  useEffect(() => {
    if (!open) {
      cancelPending()
      setUrl('')
      setPhase('idle')
      setError(null)
      setInfo(null)
      return
    }

    return () => {
      cancelPending()
    }
  }, [open, cancelPending])

  useEffect(() => {
    if (!open) {
      return
    }

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        handleClose()
      }
    }

    window.addEventListener('keydown', onKeyDown)
    return () => {
      window.removeEventListener('keydown', onKeyDown)
    }
  }, [open, handleClose])

  if (!open) {
    return null
  }

  const busy = phase !== 'idle'
  const submitting = phase === 'submitting'
  const trimmedUrl = url.trim()

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!trimmedUrl || busy || abortRef.current !== null) {
      return
    }

    const controller = new AbortController()
    abortRef.current = controller

    try {
      await executeLaunchWork({
        token,
        url: trimmedUrl,
        signal: controller.signal,
        onPhaseChange: setPhase,
        onInfo: setInfo,
        onError: setError,
        onRunLaunched: (runId) => {
          abortRef.current = null
          onRunLaunched(runId)
        },
        onRefreshRuns,
        onLogout,
      })
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null
      }
    }
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
      <div
        className="modal-card"
        role="dialog"
        aria-modal="true"
        aria-labelledby="launch-work-title"
      >
        <div className="modal-header">
          <h2 id="launch-work-title">Lanzar trabajo</h2>
          <button
            type="button"
            className="btn btn-sm"
            onClick={handleClose}
            disabled={submitting}
            aria-label="Cerrar"
          >
            Cerrar
          </button>
        </div>

        <p className="modal-description">
          Pega la URL de una issue de GitHub para registrar el Evento y abrir la Ejecución en directo.
        </p>

        <form className="modal-form" onSubmit={(e) => void handleSubmit(e)}>
          <label htmlFor="launch-work-url">URL de la issue</label>
          <input
            id="launch-work-url"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder="https://github.com/owner/repo/issues/123"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            disabled={busy}
            autoFocus
          />

          {error ? (
            <div className="error-banner" role="alert">
              {error}
            </div>
          ) : null}

          {info ? (
            <div className="info-banner" role="status">
              {info}
            </div>
          ) : null}

          <div className="modal-actions">
            <button
              type="button"
              className="btn"
              onClick={handleClose}
              disabled={submitting}
            >
              Cancelar
            </button>
            <button
              type="submit"
              className="btn btn-primary"
              disabled={busy || trimmedUrl.length === 0}
            >
              {phase === 'submitting'
                ? 'Enviando…'
                : phase === 'waiting'
                  ? 'Esperando Ejecución…'
                  : 'Lanzar'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
