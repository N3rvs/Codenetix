import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  cancelRun,
  fetchIgnoredEvents,
  fetchRunDetail,
  isRunCancellable,
  isSafeHttpUrl,
  UnauthorizedError,
} from '../api/client'
import type {
  IgnoredEventSummary,
  RunResponse,
  RunState,
  RunSummary,
  VerdictDecision,
} from '../api/generated'
import { IgnoredEventsList } from './IgnoredEventsList'
import { STATE_LABELS } from '../graph/buildPipelineGraph'
import {
  aggregateTokenUsage,
  extractLatestRiskClassification,
  filterHistoryRuns,
  formatCostDisplay,
  formatCostSubtext,
  formatDateTime,
  formatDuration,
  groupVerdictsByRound,
  HISTORY_FILTER_OPTIONS,
  REVIEWER_DISPLAY_LABELS,
  RISK_CLASS_LABELS,
  VERDICT_DECISION_LABELS,
  type HistoryStateFilter,
} from '../history/runMetrics'

export interface RunHistoryViewProps {
  token: string
  runs: readonly RunSummary[]
  loadingRuns: boolean
  runsError: string | null
  onRefreshRuns: () => void
  onOpenLive: (runId: string) => void
  onLogout: (reason?: string) => void
}

type DetailState =
  | { kind: 'idle' }
  | { kind: 'loading'; runId: string }
  | { kind: 'loaded'; detail: RunResponse }
  | { kind: 'error'; runId: string; message: string }

function statePillClass(state: RunState): string {
  if (state === 'PullRequestCreated') return 'pill pill-live'
  if (state === 'Failed') return 'pill pill-error'
  if (state === 'Cancelled') return 'pill pill-reconnecting'
  return 'pill'
}

function decisionPillClass(decision: VerdictDecision): string {
  if (decision === 'approved') return 'pill pill-live'
  if (decision === 'rejected') return 'pill pill-error'
  return 'pill pill-reconnecting'
}

function formatNumber(value: number): string {
  return value.toLocaleString('es-ES')
}

export function RunHistoryDetail({
  detail,
  onOpenLive,
  onCancelRun,
  cancelling = false,
  cancelError = null,
}: {
  detail: RunResponse
  onOpenLive: (runId: string) => void
  onCancelRun?: (runId: string) => void
  cancelling?: boolean
  cancelError?: string | null
}) {
  const tokenUsage = useMemo(() => aggregateTokenUsage(detail.events), [detail.events])
  const verdictRounds = useMemo(() => groupVerdictsByRound(detail.verdicts), [detail.verdicts])
  const riskClassification = useMemo(
    () => extractLatestRiskClassification(detail.events),
    [detail.events],
  )
  const durationLabel = formatDuration(detail.createdAt, detail.updatedAt)
  const costDisplay = formatCostDisplay(detail.cost)
  const costSubtext = formatCostSubtext(detail.cost)
  const cancellable = isRunCancellable(detail.state)
  const cancelTitle = cancellable
    ? 'Cancelar esta Ejecución sin abrir un PR'
    : detail.state === 'Deciding'
      ? 'No se puede cancelar mientras la Ejecución está en Deciding (publicando el PR).'
      : `No se puede cancelar una Ejecución en estado ${STATE_LABELS[detail.state]}.`

  return (
    <div className="history-detail-content">
      {/* 1. Cabecera y métricas */}
      <header className="history-detail-header">
        <div className="history-detail-title-block">
          <div className="history-detail-title-row">
            <span className={statePillClass(detail.state)}>{STATE_LABELS[detail.state]}</span>
            <h1>
              Ejecución <code>#{detail.shortId}</code>
            </h1>
          </div>
          <div className="history-detail-object-key">{detail.objectKey}</div>
        </div>
        <div className="history-detail-actions">
          <button
            type="button"
            className="btn btn-danger"
            disabled={!cancellable || cancelling}
            title={cancelTitle}
            onClick={() => onCancelRun?.(detail.runId)}
          >
            {cancelling ? 'Cancelando…' : 'Cancelar'}
          </button>
          <button
            type="button"
            className="btn btn-primary"
            onClick={() => onOpenLive(detail.runId)}
          >
            Ver en vivo
          </button>
        </div>
      </header>

      {cancelError ? (
        <div className="error-banner" role="alert">
          {cancelError}
        </div>
      ) : null}

      <section className="history-metrics-grid" aria-label="Resumen de ejecución">
        <div className="history-metric-card">
          <span className="history-metric-label">Duración</span>
          <strong className="history-metric-value">{durationLabel}</strong>
        </div>
        <div className="history-metric-card">
          <span className="history-metric-label">Inicio</span>
          <strong className="history-metric-value history-metric-value--sm">
            {formatDateTime(detail.createdAt)}
          </strong>
        </div>
        <div className="history-metric-card">
          <span className="history-metric-label">Última actualización</span>
          <strong className="history-metric-value history-metric-value--sm">
            {formatDateTime(detail.updatedAt)}
          </strong>
        </div>
        <div className="history-metric-card">
          <span className="history-metric-label">Tokens consumidos</span>
          <strong className="history-metric-value">{formatNumber(tokenUsage.totalTokens)}</strong>
          <span className="history-metric-sub">
            {formatNumber(tokenUsage.inputTokens)} entrada · {formatNumber(tokenUsage.outputTokens)}{' '}
            salida
          </span>
        </div>
        <div className="history-metric-card">
          <span className="history-metric-label">Coste estimado</span>
          <strong className="history-metric-value history-metric-value--sm">{costDisplay}</strong>
          <span className="history-metric-sub">{costSubtext}</span>
        </div>
      </section>

      {tokenUsage.breakdown.length > 0 ? (
        <section className="history-section">
          <h2>Desglose de tokens</h2>
          <div className="history-token-breakdown">
            {tokenUsage.breakdown.map((entry) => (
              <div
                key={`${entry.role}:${entry.provider}:${entry.model}`}
                className="history-token-row"
              >
                <span className="history-token-role">{entry.role}</span>
                <span className="history-token-model">
                  {entry.provider} / <code>{entry.model}</code>
                </span>
                <span className="history-token-counts">
                  {formatNumber(entry.inputTokens)} in · {formatNumber(entry.outputTokens)} out (
                  <strong>{formatNumber(entry.totalTokens)}</strong>)
                </span>
              </div>
            ))}
          </div>
        </section>
      ) : null}

      {/* 2. Alerta destacada de fallo */}
      {detail.state === 'Failed' || detail.failureReason || detail.failureDetail ? (
        <section className="history-failure-alert" role="alert">
          <div className="history-failure-title">
            Fallo en la Ejecución
            {detail.failureReason ? (
              <>
                : <code>{detail.failureReason}</code>
              </>
            ) : null}
          </div>
          {detail.failureDetail ? (
            <p className="history-failure-detail">{detail.failureDetail}</p>
          ) : null}
        </section>
      ) : null}

      {/* 3. Tarjeta de Pull Request */}
      {detail.pullRequest ? (
        <section className="history-section history-pr-card">
          <div className="history-section-head">
            <h2>Pull Request #{detail.pullRequest.number}</h2>
            {riskClassification ? (
              <span
                className={
                  riskClassification.riskClass === 'Minor'
                    ? 'pill pill-live'
                    : 'pill pill-reconnecting'
                }
              >
                {RISK_CLASS_LABELS[riskClassification.riskClass]}
              </span>
            ) : null}
          </div>
          <div className="history-pr-title">{detail.pullRequest.title}</div>
          <div className="history-pr-meta">
            <span>
              Rama: <code>{detail.pullRequest.branchName}</code>
            </span>
            <span>
              Commit: <code>{detail.pullRequest.commitSha.slice(0, 8)}</code>
            </span>
            {riskClassification ? (
              <span>Diff: {formatNumber(riskClassification.diffLines)} líneas</span>
            ) : null}
          </div>
          <div className="history-pr-link-row">
            {isSafeHttpUrl(detail.pullRequest.url) ? (
              <a
                href={detail.pullRequest.url}
                target="_blank"
                rel="noopener noreferrer"
                className="btn btn-primary"
              >
                Abrir PR #{detail.pullRequest.number} en GitHub
              </a>
            ) : (
              <span className="history-unsafe-url">
                Enlace omitido por esquema no permitido: <code>{detail.pullRequest.url}</code>
              </span>
            )}
          </div>
          {riskClassification && riskClassification.matchedProtectedPaths.length > 0 ? (
            <div className="history-protected-paths">
              <span className="history-sublabel">Rutas protegidas coincidentes:</span>
              <ul className="history-code-list">
                {riskClassification.matchedProtectedPaths.map((path, idx) => (
                  <li key={`${idx}-${path}`}>
                    <code>{path}</code>
                  </li>
                ))}
              </ul>
            </div>
          ) : null}
        </section>
      ) : null}

      {/* 4. Sección Spec */}
      <section className="history-section">
        <h2>Spec</h2>
        {detail.spec ? (
          <div className="history-spec-grid">
            <div className="history-spec-block">
              <h3>Problema</h3>
              <p>{detail.spec.problem}</p>
            </div>
            <div className="history-spec-block">
              <h3>Causa probable</h3>
              <p>{detail.spec.probableCause}</p>
            </div>
            <div className="history-spec-block">
              <h3>Cambio propuesto</h3>
              <p>{detail.spec.proposedChange}</p>
            </div>
            <div className="history-spec-block">
              <h3>Criterios de aceptación</h3>
              {detail.spec.acceptanceCriteria.length > 0 ? (
                <ul className="history-bullet-list">
                  {detail.spec.acceptanceCriteria.map((criterion, idx) => (
                    <li key={`${idx}-${criterion}`}>{criterion}</li>
                  ))}
                </ul>
              ) : (
                <p className="history-empty-note">Sin criterios especificados.</p>
              )}
            </div>
            <div className="history-spec-block">
              <h3>Archivos afectados</h3>
              {detail.spec.affectedFiles.length > 0 ? (
                <ul className="history-code-list">
                  {detail.spec.affectedFiles.map((file, idx) => (
                    <li key={`${idx}-${file}`}>
                      <code>{file}</code>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="history-empty-note">Sin archivos listados.</p>
              )}
            </div>
          </div>
        ) : (
          <p className="history-empty-note">Esta Ejecución no generó una Spec.</p>
        )}
      </section>

      {/* 5. Sección Veredictos */}
      <section className="history-section">
        <h2>Veredictos ({detail.verdicts.length})</h2>
        {verdictRounds.length > 0 ? (
          <div className="history-rounds">
            {verdictRounds.map((group) => (
              <div key={group.round} className="history-round-group">
                <h3 className="history-round-title">Ronda {group.round}</h3>
                <div className="history-verdicts-list">
                  {group.verdicts.map((verdict) => (
                    <article
                      key={`${verdict.stageId}-${verdict.reviewer}`}
                      className="history-verdict-card"
                    >
                      <div className="history-verdict-head">
                        <div className="history-verdict-reviewer">
                          <strong>
                            {REVIEWER_DISPLAY_LABELS[verdict.reviewer] ?? verdict.reviewer}
                          </strong>
                          <span className={decisionPillClass(verdict.decision)}>
                            {VERDICT_DECISION_LABELS[verdict.decision]}
                          </span>
                        </div>
                        <div className="history-verdict-meta">
                          <code>{verdict.model}</code> · {verdict.attempts} intento/s
                        </div>
                      </div>
                      <p className="history-verdict-summary">{verdict.summary}</p>
                      {verdict.findings.length > 0 ? (
                        <ul className="history-findings-list">
                          {verdict.findings.map((finding, idx) => (
                            <li
                              key={`${finding.file}-${finding.line ?? 'none'}-${idx}`}
                              className="history-finding-item"
                            >
                              <div className="history-finding-head">
                                <span className="history-finding-severity">{finding.severity}</span>
                                <code className="history-finding-location">
                                  {finding.file}
                                  {finding.line != null ? `:${finding.line}` : ''}
                                </code>
                              </div>
                              <p className="history-finding-message">{finding.message}</p>
                            </li>
                          ))}
                        </ul>
                      ) : (
                        <p className="history-empty-note">Sin hallazgos en esta revisión.</p>
                      )}
                    </article>
                  ))}
                </div>
              </div>
            ))}
          </div>
        ) : (
          <p className="history-empty-note">No hay Veredictos registrados para esta Ejecución.</p>
        )}
      </section>

      {/* 6. Sección Pruebas */}
      <section className="history-section">
        <h2>Pruebas</h2>
        {detail.testReport ? (
          <div className="history-tests-box">
            <div className="history-tests-summary">
              <span
                className={detail.testReport.buildSucceeded ? 'pill pill-live' : 'pill pill-error'}
              >
                {detail.testReport.buildSucceeded
                  ? 'Compilación correcta'
                  : 'Compilación fallida'}
              </span>
              <span className="history-test-stat history-test-stat--pass">
                Pasados: <strong>{detail.testReport.passedTests}</strong>
              </span>
              <span className="history-test-stat history-test-stat--fail">
                Fallados: <strong>{detail.testReport.failedTests}</strong>
              </span>
              <span className="history-test-stat">
                Omitidos: <strong>{detail.testReport.skippedTests}</strong>
              </span>
              <span className="history-test-stat">
                Total: <strong>{detail.testReport.totalTests}</strong>
              </span>
              <span className="history-test-stat">
                Duración: <code>{detail.testReport.duration}</code>
              </span>
            </div>

            {detail.testReport.newTests.length > 0 ? (
              <div className="history-tests-subblock">
                <h3>Nuevos tests ({detail.testReport.newTests.length})</h3>
                <ul className="history-code-list">
                  {detail.testReport.newTests.map((testName, idx) => (
                    <li key={`${idx}-${testName}`}>
                      <code>{testName}</code>
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}

            {detail.testReport.failedTestCases.length > 0 ? (
              <div className="history-tests-subblock">
                <h3>Casos fallados ({detail.testReport.failedTestCases.length})</h3>
                <ul className="history-failed-tests-list">
                  {detail.testReport.failedTestCases.map((tc, idx) => (
                    <li key={`${tc.name}-${idx}`} className="history-failed-test-item">
                      <div className="history-failed-test-head">
                        <code>{tc.name}</code>
                        <span className="pill pill-error">{tc.outcome}</span>
                      </div>
                      {tc.errorMessage ? (
                        <pre className="history-failed-test-error">{tc.errorMessage}</pre>
                      ) : null}
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}
          </div>
        ) : (
          <p className="history-empty-note">
            No hay informe de pruebas registrado para esta Ejecución.
          </p>
        )}
      </section>
    </div>
  )
}

export function RunHistoryView({
  token,
  runs,
  loadingRuns,
  runsError,
  onRefreshRuns,
  onOpenLive,
  onLogout,
}: RunHistoryViewProps) {
  const [stateFilter, setStateFilter] = useState<HistoryStateFilter>('all')
  const [query, setQuery] = useState('')
  const [selectedHistoryRunId, setSelectedHistoryRunId] = useState<string | null>(
    () => runs[0]?.runId ?? null,
  )
  const [detailReloadTick, setDetailReloadTick] = useState(0)
  const [detailState, setDetailState] = useState<DetailState>({ kind: 'idle' })
  const [cancellingRunIds, setCancellingRunIds] = useState<ReadonlySet<string>>(() => new Set())
  const [cancelError, setCancelError] = useState<string | null>(null)
  const [ignoredEvents, setIgnoredEvents] = useState<readonly IgnoredEventSummary[]>([])
  const [ignoredLoading, setIgnoredLoading] = useState(true)
  const [ignoredError, setIgnoredError] = useState<string | null>(null)
  const [ignoredReloadTick, setIgnoredReloadTick] = useState(0)

  // Eventos ignorados (FR-8): se cargan al abrir el Historial y con «Actualizar».
  useEffect(() => {
    const controller = new AbortController()
    setIgnoredLoading(true)
    void fetchIgnoredEvents(token, 20, controller.signal)
      .then((events) => {
        if (controller.signal.aborted) return
        setIgnoredEvents(events)
        setIgnoredError(null)
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return
        if (err instanceof UnauthorizedError) {
          onLogout(err.message)
          return
        }
        setIgnoredError(err instanceof Error ? err.message : 'No se pudieron cargar los Eventos ignorados.')
      })
      .finally(() => {
        if (!controller.signal.aborted) setIgnoredLoading(false)
      })
    return () => {
      controller.abort()
    }
  }, [token, ignoredReloadTick, onLogout])

  const selectedRunIdRef = useRef<string | null>(selectedHistoryRunId)
  selectedRunIdRef.current = selectedHistoryRunId

  const handleCancelHistoryRun = useCallback(
    async (runId: string) => {
      setCancellingRunIds((prev) => {
        const next = new Set(prev)
        next.add(runId)
        return next
      })
      if (selectedRunIdRef.current === runId) {
        setCancelError(null)
      }
      try {
        await cancelRun(token, runId)
        onRefreshRuns()
        if (selectedRunIdRef.current === runId) {
          setDetailReloadTick((n) => n + 1)
        }
      } catch (err) {
        if (err instanceof UnauthorizedError) {
          onLogout(err.message)
        } else if (selectedRunIdRef.current === runId) {
          setCancelError(
            err instanceof Error ? err.message : 'No se pudo cancelar la Ejecución.',
          )
        }
      } finally {
        setCancellingRunIds((prev) => {
          if (!prev.has(runId)) {
            return prev
          }
          const next = new Set(prev)
          next.delete(runId)
          return next
        })
      }
    },
    [token, onRefreshRuns, onLogout],
  )

  const filteredRuns = useMemo(
    () => filterHistoryRuns(runs, stateFilter, query),
    [runs, stateFilter, query],
  )

  // Si no hay selección activa o ya no existe en la lista filtrada, selecciona el primero filtrado.
  useEffect(() => {
    if (
      selectedHistoryRunId &&
      filteredRuns.some((run) => run.runId === selectedHistoryRunId)
    ) {
      return
    }
    setSelectedHistoryRunId(filteredRuns[0]?.runId ?? null)
  }, [filteredRuns, selectedHistoryRunId])

  // Carga el detalle completo con AbortController y guarda contra condiciones de carrera.
  useEffect(() => {
    if (!selectedHistoryRunId) {
      setDetailState({ kind: 'idle' })
      return
    }

    const runId = selectedHistoryRunId
    const controller = new AbortController()
    setCancelError(null)
    setDetailState({ kind: 'loading', runId })

    void fetchRunDetail(token, runId, controller.signal)
      .then((detail) => {
        if (controller.signal.aborted || selectedRunIdRef.current !== runId) {
          return
        }
        setDetailState({ kind: 'loaded', detail })
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted || selectedRunIdRef.current !== runId) {
          return
        }
        if (err instanceof UnauthorizedError) {
          onLogout(err.message)
          return
        }
        setDetailState({
          kind: 'error',
          runId,
          message:
            err instanceof Error
              ? err.message
              : 'No se pudo cargar el detalle de la Ejecución.',
        })
      })

    return () => {
      controller.abort()
    }
  }, [token, selectedHistoryRunId, detailReloadTick, onLogout])

  return (
    <div
      id="panel-history"
      role="tabpanel"
      aria-labelledby="tab-history"
      className="panel-body history-body"
    >
      <aside className="sidebar history-sidebar">
        <div className="sidebar-header">
          <h2>
            Historial ({filteredRuns.length}/{runs.length})
          </h2>
          <button
            type="button"
            className="btn btn-sm"
            onClick={() => {
              onRefreshRuns()
              setDetailReloadTick((n) => n + 1)
              setIgnoredReloadTick((n) => n + 1)
            }}
            disabled={loadingRuns}
          >
            {loadingRuns ? 'Actualizando…' : 'Actualizar'}
          </button>
        </div>

        <div className="history-controls">
          <input
            type="search"
            className="history-search"
            placeholder="Buscar por #shortId o repo/issue…"
            aria-label="Buscar Ejecuciones por shortId o repositorio"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />

          <div className="history-filters" role="group" aria-label="Filtrar por estado">
            {HISTORY_FILTER_OPTIONS.map((option) => (
              <button
                key={option.value}
                type="button"
                aria-pressed={stateFilter === option.value}
                className={`history-filter-btn${stateFilter === option.value ? ' active' : ''}`}
                onClick={() => setStateFilter(option.value)}
              >
                {option.label}
              </button>
            ))}
          </div>
        </div>

        {runsError ? (
          <div className="error-banner" style={{ margin: '0.5rem 0.75rem 0' }} role="alert">
            {runsError}
          </div>
        ) : null}

        <div className="runs-list">
          {runs.length === 0 ? (
            <p className="run-meta history-list-empty">
              {loadingRuns
                ? 'Cargando historial de Ejecuciones…'
                : 'Todavía no hay Ejecuciones registradas en el historial.'}
            </p>
          ) : filteredRuns.length === 0 ? (
            <p className="run-meta history-list-empty">
              Ninguna Ejecución coincide con los filtros actuales.
            </p>
          ) : (
            filteredRuns.map((run) => (
              <button
                key={run.runId}
                type="button"
                aria-current={run.runId === selectedHistoryRunId ? 'true' : undefined}
                className={`run-item${run.runId === selectedHistoryRunId ? ' selected' : ''}`}
                onClick={() => setSelectedHistoryRunId(run.runId)}
              >
                <div className="run-item-top">
                  <span className="run-short-id">#{run.shortId}</span>
                  <span className={statePillClass(run.state)}>{STATE_LABELS[run.state]}</span>
                </div>
                <div className="run-object-key" title={run.objectKey}>
                  {run.objectKey}
                </div>
                {run.failureReason ? (
                  <div className="run-failure-tag">{run.failureReason}</div>
                ) : null}
                <div className="run-meta history-item-footer">
                  <span>{formatDuration(run.createdAt, run.updatedAt)}</span>
                  {run.cost && (run.cost.isEstimable || run.cost.hasUnpricedModels) ? (
                    <span>{formatCostDisplay(run.cost)}</span>
                  ) : null}
                  <span>{formatDateTime(run.createdAt)}</span>
                </div>
              </button>
            ))
          )}
        </div>

        <IgnoredEventsList events={ignoredEvents} loading={ignoredLoading} error={ignoredError} />
      </aside>

      <main className="workspace history-detail-pane">
        {selectedHistoryRunId === null ? (
          <div className="empty-workspace">
            <h2>Historial de Ejecuciones</h2>
            <p>
              Selecciona una Ejecución en la lista de la izquierda para inspeccionar su Spec,
              Veredictos, pruebas, Pull Request y consumo de tokens.
            </p>
          </div>
        ) : detailState.kind === 'loaded' && detailState.detail.runId === selectedHistoryRunId ? (
          <RunHistoryDetail
            detail={detailState.detail}
            onOpenLive={onOpenLive}
            onCancelRun={(runId) => void handleCancelHistoryRun(runId)}
            cancelling={cancellingRunIds.has(detailState.detail.runId)}
            cancelError={cancelError}
          />
        ) : detailState.kind === 'error' && detailState.runId === selectedHistoryRunId ? (
          <div className="empty-workspace">
            <div className="error-banner" role="alert">
              {detailState.message}
            </div>
            <button
              type="button"
              className="btn"
              style={{ marginTop: '0.85rem' }}
              onClick={() => setDetailReloadTick((n) => n + 1)}
            >
              Reintentar
            </button>
          </div>
        ) : (
          <div className="empty-workspace">
            <p>Cargando detalle de la Ejecución…</p>
          </div>
        )}
      </main>
    </div>
  )
}
