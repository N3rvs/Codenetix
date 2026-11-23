import type { IgnoredEventSummary } from '../api/generated'
import { formatDateTime } from '../history/runMetrics'

export const SOURCE_LABELS: Record<string, string> = {
  Manual: 'Manual',
  GitHub: 'GitHub',
  GitHubAction: 'GitHub Action',
  Sentry: 'Sentry',
}

export interface IgnoredEventsListProps {
  events: readonly IgnoredEventSummary[]
  loading: boolean
  error: string | null
}

/**
 * Eventos que no generaron Ejecución, con su motivo (FR-8): proyecto de Sentry sin Repositorio objetivo,
 * disparo no autorizado, objeto ocupado… El texto se muestra siempre como texto plano.
 */
export function IgnoredEventsList({ events, loading, error }: IgnoredEventsListProps) {
  return (
    <details className="ignored-events">
      <summary className="ignored-events-summary">Eventos ignorados ({events.length})</summary>
      {error ? (
        <div className="error-banner ignored-events-error" role="alert">
          {error}
        </div>
      ) : null}
      {events.length === 0 ? (
        <p className="run-meta history-list-empty">
          {loading ? 'Cargando Eventos ignorados…' : 'No hay Eventos ignorados recientes.'}
        </p>
      ) : (
        <ul className="ignored-events-list">
          {events.map((event) => (
            <li key={event.eventId} className="ignored-event-item">
              <div className="run-item-top">
                <span className="pill">{SOURCE_LABELS[event.source] ?? event.source}</span>
                <span className="run-meta">{formatDateTime(event.receivedAt)}</span>
              </div>
              <div className="run-object-key" title={event.objectKey}>
                {event.objectKey}
              </div>
              <div className="ignored-event-reason">{event.reason ?? 'Sin motivo registrado.'}</div>
            </li>
          ))}
        </ul>
      )}
    </details>
  )
}
