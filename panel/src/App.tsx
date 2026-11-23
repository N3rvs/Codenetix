import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import {
  Background,
  Controls,
  Handle,
  Position,
  ReactFlow,
  type NodeProps,
} from '@xyflow/react'
import {
  cancelRun,
  clearStoredToken,
  fetchHealth,
  fetchRunDetail,
  fetchRunEvents,
  fetchRuns,
  isRunCancellable,
  isSafeHttpUrl,
  loadStoredToken,
  mergeRunEvents,
  saveStoredToken,
  UnauthorizedError,
  type HealthInfo,
} from './api/client'
import type { RunEvent, RunState, RunSummary } from './api/generated'
import {
  buildPipelineGraph,
  deriveCurrentState,
  STATE_LABELS,
  type PipelineFlowNode,
} from './graph/buildPipelineGraph'
import { LiveTerminal } from './components/LiveTerminal'
import { LaunchWorkModal } from './components/LaunchWorkModal'
import { RunHistoryView } from './components/RunHistoryView'
import { formatCostDisplay } from './history/runMetrics'
import { selectDefaultNodeId, type TerminalScopeMode } from './terminal/stageOutput'

const exampleCommand = 'docker exec codenetix codenetix fix <url-de-issue>'
const retryDelayMs = 2000

type HealthViewState =
  | { kind: 'loading' }
  | { kind: 'error' }
  | { kind: 'health'; health: HealthInfo }

type HubStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting'

type PanelTab = 'live' | 'history'

function isHubUnauthorizedError(err: unknown): boolean {
  if (!err) return false
  if (typeof err === 'object' && 'statusCode' in err && (err as { statusCode?: unknown }).statusCode === 401) {
    return true
  }
  const text = err instanceof Error ? err.message : String(err)
  return /\b401\b|unauthorized/i.test(text)
}

function computeLastContiguousSeq(sortedEvents: readonly RunEvent[]): number {
  let contiguous = 0
  for (const ev of sortedEvents) {
    if (ev.seq === contiguous + 1) {
      contiguous = ev.seq
    } else if (ev.seq > contiguous + 1) {
      break
    }
  }
  return contiguous
}

function StageNode({ data }: NodeProps<PipelineFlowNode>) {
  return (
    <div
      className={`stage-node stage-node--${data.status}${data.selected ? ' stage-node--selected' : ''}`}
      data-selected={data.selected ? 'true' : 'false'}
    >
      <Handle type="target" position={Position.Left} style={{ background: '#475569' }} />
      <div className="stage-node-head">
        <span className="stage-node-title">{data.label}</span>
        {data.badge ? <span className="stage-node-badge">{data.badge}</span> : null}
      </div>
      {data.subtitle ? <div className="stage-node-subtitle">{data.subtitle}</div> : null}
      <Handle type="source" position={Position.Right} style={{ background: '#475569' }} />
    </div>
  )
}

const nodeTypes = { stage: StageNode }

function statePillClass(state: RunState): string {
  if (state === 'PullRequestCreated') return 'pill pill-live'
  if (state === 'Failed') return 'pill pill-error'
  if (state === 'Cancelled') return 'pill pill-reconnecting'
  return 'pill'
}

function summarizeEvent(ev: RunEvent): string {
  switch (ev.type) {
    case 'RunCreated':
      return ev.objectKey
    case 'StateChanged':
      return `${ev.from ?? 'Inicio'} → ${ev.to}${ev.failureReason ? ` (${ev.failureReason})` : ''}`
    case 'RepositoryPrepared':
      return `BaseSha ${ev.baseSha.slice(0, 8)}`
    case 'RepoConfigResolved':
      return `Origen: ${ev.source}`
    case 'SpecGenerated':
      return `${ev.model} (${ev.attempts} intento/s)`
    case 'AnalystOutputRejected':
      return `Intento ${ev.attempt}: ${ev.error}`
    case 'AnalysisRejected':
      return `${ev.category} (${ev.model})`
    case 'SentryContextAttached':
      return `Sentry ${ev.sentryIssueId}: ${ev.frames} marcos, ${ev.breadcrumbs} migas${ev.truncated ? ' (recortado)' : ''}`
    case 'SentryContextUnavailable':
      return `Sin contexto de Sentry: ${ev.reason}`
    case 'SandboxCreated':
      return ev.image
    case 'SandboxReleased':
      return 'Sandbox liberado'
    case 'CodingAttemptFailed':
      return `Intento ${ev.attempt}: ${ev.reason}`
    case 'ReviewerOutputRejected':
      return `R${ev.round} ${ev.reviewer}: ${ev.error}`
    case 'VerdictRecorded':
      return `R${ev.round} ${ev.reviewer} → ${ev.decision} (${ev.findingCount} hallazgos)`
    case 'RiskClassified':
      return `${ev.riskClass} · ${ev.diffLines} líneas`
    case 'PullRequestOpened':
      return `PR #${ev.number} (${ev.branch})`
    case 'IssueCommented':
      return `${ev.kind}: ${ev.commentUrl}`
    case 'NotificationFailed':
      return `${ev.kind}: ${ev.error}`
    case 'TokensConsumed':
      return `${ev.role} · ${ev.inputTokens} in / ${ev.outputTokens} out`
    case 'EgressBlocked':
      return `${ev.target}: ${ev.reason}`
    case 'OutputAppended':
      return ev.text.trim() || '(salida vacía)'
    case 'OutputTruncated':
      return `Salida truncada (${ev.maxBytes ?? 0} bytes)`
  }
}

function formatTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleTimeString('es-ES', { hour12: false })
}

function TokenGate({
  notice,
  onAuthenticated,
}: {
  notice: string | null
  onAuthenticated: (token: string, initialRuns: RunSummary[]) => void
}) {
  const [input, setInput] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(notice)

  useEffect(() => {
    setError(notice)
  }, [notice])

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault()
    const trimmed = input.trim()
    if (!trimmed) {
      setError('Introduce el admin-token para acceder al Panel.')
      return
    }

    setSubmitting(true)
    setError(null)
    try {
      const runs = await fetchRuns(trimmed)
      saveStoredToken(trimmed)
      onAuthenticated(trimmed, runs)
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        setError(err.message)
      } else {
        setError(err instanceof Error ? err.message : 'No se pudo validar el token.')
      }
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <main className="status-screen">
      <section className="status-card">
        <h1>Acceso al Panel de CodeNetix</h1>
        <p>
          Introduce el <code>admin-token</code> de esta instancia (guardado en <code>/data/admin.token</code> o en{' '}
          <code>CODENETIX__Host__AccessToken</code>). Se guardará localmente en tu navegador.
        </p>
        <form className="token-form" onSubmit={handleSubmit}>
          <label htmlFor="admin-token-input">admin-token</label>
          <input
            id="admin-token-input"
            type="password"
            autoComplete="current-password"
            placeholder="cnx_..."
            value={input}
            onChange={(e) => setInput(e.target.value)}
            disabled={submitting}
          />
          {error ? (
            <div className="error-banner" role="alert">
              {error}
            </div>
          ) : null}
          <button type="submit" className="btn btn-primary" disabled={submitting}>
            {submitting ? 'Comprobando token…' : 'Entrar al Panel'}
          </button>
        </form>
      </section>
    </main>
  )
}

function LivePanel({
  token,
  initialRuns,
  onLogout,
}: {
  token: string
  initialRuns: RunSummary[] | null
  onLogout: (reason?: string) => void
}) {
  const [activeTab, setActiveTab] = useState<PanelTab>('live')
  const [runs, setRuns] = useState<RunSummary[]>(initialRuns ?? [])
  const [historyRuns, setHistoryRuns] = useState<RunSummary[]>(initialRuns ?? [])
  const [loadingHistoryRuns, setLoadingHistoryRuns] = useState(false)
  const [historyError, setHistoryError] = useState<string | null>(null)
  const [selectedRunId, setSelectedRunId] = useState<string | null>(initialRuns?.[0]?.runId ?? null)
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null)
  const [hasManualNodeSelection, setHasManualNodeSelection] = useState(false)
  const [terminalScopeMode, setTerminalScopeMode] = useState<TerminalScopeMode>('node')
  const [events, setEvents] = useState<RunEvent[]>([])
  const [hubStatus, setHubStatus] = useState<HubStatus>('connecting')
  const [reconnectAttempt, setReconnectAttempt] = useState(0)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [loadingRuns, setLoadingRuns] = useState(initialRuns === null)
  const [cancellingRunIds, setCancellingRunIds] = useState<ReadonlySet<string>>(() => new Set())
  const [launchModalOpen, setLaunchModalOpen] = useState(false)

  const lastContiguousSeqRef = useRef<number>(0)
  const selectedRunIdRef = useRef<string | null>(selectedRunId)
  selectedRunIdRef.current = selectedRunId
  const runsAbortRef = useRef<AbortController | null>(null)
  const historyAbortRef = useRef<AbortController | null>(null)
  const costRefreshAbortRef = useRef<AbortController | null>(null)
  const costRefreshTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const cancelPendingCostRefresh = useCallback(() => {
    if (costRefreshTimerRef.current !== null) {
      clearTimeout(costRefreshTimerRef.current)
      costRefreshTimerRef.current = null
    }
    costRefreshAbortRef.current?.abort()
    costRefreshAbortRef.current = null
  }, [])

  const scheduleCostRefresh = useCallback(
    (runId: string) => {
      if (costRefreshTimerRef.current !== null) {
        clearTimeout(costRefreshTimerRef.current)
      }
      costRefreshTimerRef.current = setTimeout(() => {
        costRefreshTimerRef.current = null
        costRefreshAbortRef.current?.abort()
        const controller = new AbortController()
        costRefreshAbortRef.current = controller

        void fetchRunDetail(token, runId, controller.signal)
          .then((detail) => {
            if (controller.signal.aborted || selectedRunIdRef.current !== runId) {
              return
            }
            setRuns((prev) =>
              prev.map((r) => (r.runId === runId ? { ...r, cost: detail.cost ?? null } : r)),
            )
          })
          .catch((err: unknown) => {
            if (controller.signal.aborted) {
              return
            }
            if (err instanceof UnauthorizedError) {
              onLogout(err.message)
            }
          })
      }, 300)
    },
    [token, onLogout],
  )

  useEffect(() => {
    return () => {
      cancelPendingCostRefresh()
    }
  }, [cancelPendingCostRefresh])

  const syncRunStateFromEvents = useCallback((runId: string, incomingEvents: readonly RunEvent[]) => {
    let lastChange: Extract<RunEvent, { type: 'StateChanged' }> | null = null
    for (const ev of incomingEvents) {
      if (ev.type === 'StateChanged' && ev.runId === runId) {
        if (!lastChange || ev.seq >= lastChange.seq) {
          lastChange = ev
        }
      }
    }

    if (!lastChange) {
      return
    }

    const change = lastChange
    setRuns((prev) =>
      prev.map((r) =>
        r.runId === runId
          ? {
              ...r,
              state: change.to,
              failureReason: change.failureReason ?? r.failureReason,
              failureDetail: change.detail ?? r.failureDetail,
              updatedAt: change.at,
            }
          : r,
      ),
    )
  }, [])

  const refreshRuns = useCallback(async (preferredRunId?: string) => {
    runsAbortRef.current?.abort()
    const controller = new AbortController()
    runsAbortRef.current = controller

    setLoadingRuns(true)
    try {
      let list = await fetchRuns(token, 50, controller.signal)
      if (controller.signal.aborted) {
        return
      }

      if (preferredRunId && !list.some((r) => r.runId === preferredRunId)) {
        try {
          const detail = await fetchRunDetail(token, preferredRunId, controller.signal)
          if (controller.signal.aborted) {
            return
          }
          const summary: RunSummary = {
            runId: detail.runId,
            shortId: detail.shortId,
            state: detail.state,
            failureReason: detail.failureReason,
            failureDetail: detail.failureDetail,
            eventId: detail.eventId,
            objectKey: detail.objectKey,
            createdAt: detail.createdAt,
            updatedAt: detail.updatedAt,
            cost: detail.cost ?? null,
          }
          list = [summary, ...list]
        } catch (detailErr) {
          if (controller.signal.aborted) {
            return
          }
          if (detailErr instanceof UnauthorizedError) {
            throw detailErr
          }
        }
      }

      setRuns(list)
      setErrorMessage(null)
      setSelectedRunId((current) => {
        if (preferredRunId && list.some((r) => r.runId === preferredRunId)) {
          return preferredRunId
        }
        if (current && list.some((r) => r.runId === current)) {
          return current
        }
        return preferredRunId ?? list[0]?.runId ?? null
      })
    } catch (err) {
      if (controller.signal.aborted) {
        return
      }
      if (err instanceof UnauthorizedError) {
        onLogout(err.message)
      } else {
        setErrorMessage(err instanceof Error ? err.message : 'Error al cargar las Ejecuciones.')
      }
    } finally {
      if (!controller.signal.aborted) {
        setLoadingRuns(false)
      }
    }
  }, [token, onLogout])

  const refreshHistoryRuns = useCallback(async () => {
    historyAbortRef.current?.abort()
    const controller = new AbortController()
    historyAbortRef.current = controller

    setLoadingHistoryRuns(true)
    try {
      const list = await fetchRuns(token, 200, controller.signal)
      if (controller.signal.aborted) {
        return
      }
      setHistoryRuns(list)
      setHistoryError(null)
    } catch (err) {
      if (controller.signal.aborted) {
        return
      }
      if (err instanceof UnauthorizedError) {
        onLogout(err.message)
      } else {
        setHistoryError(
          err instanceof Error ? err.message : 'Error al cargar el historial de Ejecuciones.',
        )
      }
    } finally {
      if (!controller.signal.aborted) {
        setLoadingHistoryRuns(false)
      }
    }
  }, [token, onLogout])

  useEffect(() => {
    if (initialRuns === null) {
      void refreshRuns()
    }
    return () => {
      runsAbortRef.current?.abort()
      runsAbortRef.current = null
    }
  }, [initialRuns, refreshRuns])

  useEffect(() => {
    if (activeTab !== 'history') {
      return
    }
    void refreshHistoryRuns()
    return () => {
      historyAbortRef.current?.abort()
      historyAbortRef.current = null
    }
  }, [activeTab, refreshHistoryRuns])

  const handleCloseLaunchModal = useCallback(() => {
    setLaunchModalOpen(false)
  }, [])

  const handleRefreshRunsFromModal = useCallback(() => {
    void refreshRuns()
  }, [refreshRuns])

  const handleRunLaunched = useCallback(
    (runId: string) => {
      setLaunchModalOpen(false)
      if (runId !== selectedRunIdRef.current) {
        setEvents([])
        setSelectedNodeId(null)
        setHasManualNodeSelection(false)
        lastContiguousSeqRef.current = 0
        setSelectedRunId(runId)
      }
      setActiveTab('live')
      void refreshRuns(runId)
    },
    [refreshRuns],
  )

  const handleOpenLiveFromHistory = useCallback(
    (runId: string) => {
      const fromHistory = historyRuns.find((r) => r.runId === runId)
      if (fromHistory) {
        setRuns((prev) =>
          prev.some((r) => r.runId === runId)
            ? prev.map((r) => (r.runId === runId ? fromHistory : r))
            : [fromHistory, ...prev],
        )
      }
      if (runId !== selectedRunIdRef.current) {
        setEvents([])
        setSelectedNodeId(null)
        setHasManualNodeSelection(false)
        lastContiguousSeqRef.current = 0
        setSelectedRunId(runId)
      }
      setActiveTab('live')
    },
    [historyRuns],
  )

  const catchUpEvents = useCallback(
    async (runId: string, afterSeq: number, signal?: AbortSignal) => {
      try {
        const fetched = await fetchRunEvents(token, runId, afterSeq, signal)
        if (signal?.aborted || selectedRunIdRef.current !== runId) {
          return
        }
        setEvents((prev) => {
          const merged = mergeRunEvents(prev, fetched)
          lastContiguousSeqRef.current = computeLastContiguousSeq(merged)
          return merged
        })
        syncRunStateFromEvents(runId, fetched)
        if (fetched.some((ev) => ev.type === 'TokensConsumed')) {
          scheduleCostRefresh(runId)
        }
      } catch (err) {
        if (signal?.aborted || selectedRunIdRef.current !== runId) {
          return
        }
        if (err instanceof UnauthorizedError) {
          onLogout(err.message)
        } else {
          setErrorMessage(err instanceof Error ? err.message : 'Error al recuperar eventos.')
        }
      }
    },
    [token, onLogout, syncRunStateFromEvents, scheduleCostRefresh],
  )

  // Carga inicial de eventos al cambiar de Ejecución seleccionada.
  useEffect(() => {
    setEvents([])
    setSelectedNodeId(null)
    setHasManualNodeSelection(false)
    lastContiguousSeqRef.current = 0
    if (!selectedRunId) {
      return
    }
    const controller = new AbortController()
    void catchUpEvents(selectedRunId, 0, controller.signal)
      return () => {
      controller.abort()
      cancelPendingCostRefresh()
    }
  }, [selectedRunId, catchUpEvents, cancelPendingCostRefresh])

  // Conexión SignalR a /hubs/runs con reconexión automática y reanudación por afterSeq.
  useEffect(() => {
    if (activeTab !== 'live' || !selectedRunId) {
      setHubStatus('disconnected')
      return
    }

    let cancelled = false
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/runs', {
        accessTokenFactory: () => token,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('RunEvent', (incoming: RunEvent) => {
      if (cancelled || incoming.runId !== selectedRunId) {
        return
      }

      const previousContiguous = lastContiguousSeqRef.current
      setEvents((prev) => {
        const merged = mergeRunEvents(prev, [incoming])
        lastContiguousSeqRef.current = computeLastContiguousSeq(merged)
        return merged
      })
      syncRunStateFromEvents(selectedRunId, [incoming])

      // Si se detecta un hueco respecto al último seq contiguo, recuperar eventos perdidos vía HTTP.
      if (incoming.seq > previousContiguous + 1) {
        void catchUpEvents(selectedRunId, previousContiguous)
      }

      if (incoming.type === 'TokensConsumed') {
        scheduleCostRefresh(selectedRunId)
      }
    })

    connection.onreconnecting(() => {
      if (!cancelled) {
        setHubStatus('reconnecting')
      }
    })

    connection.onreconnected(() => {
      if (cancelled) return
      void connection
        .invoke('SubscribeRun', selectedRunId)
        .then(async () => {
          if (cancelled) return
          setHubStatus('connected')
          setErrorMessage(null)
          await catchUpEvents(selectedRunId, lastContiguousSeqRef.current)
        })
        .catch((err: unknown) => {
          if (cancelled) return
          if (isHubUnauthorizedError(err)) {
            onLogout('Tu sesión ha caducado o el admin-token ya no es válido.')
            return
          }
          setHubStatus('disconnected')
          setErrorMessage('No se pudo reanudar la suscripción en vivo tras reconectar.')
        })
    })

    connection.onclose((err) => {
      if (cancelled) return
      if (isHubUnauthorizedError(err)) {
        onLogout('Tu sesión ha caducado o el admin-token ya no es válido.')
        return
      }
      setHubStatus('disconnected')
    })

    setHubStatus('connecting')
    void connection
      .start()
      .then(async () => {
        if (cancelled) return
        await connection.invoke('SubscribeRun', selectedRunId)
        if (!cancelled) {
          setHubStatus('connected')
          setErrorMessage(null)
          // Recuperar cualquier evento emitido entre la carga HTTP inicial y SubscribeRun.
          await catchUpEvents(selectedRunId, lastContiguousSeqRef.current)
        }
      })
      .catch((err: unknown) => {
        if (cancelled) return
        if (isHubUnauthorizedError(err)) {
          onLogout('Tu sesión ha caducado o el admin-token ya no es válido.')
          return
        }
        setHubStatus('disconnected')
      })

    return () => {
      cancelled = true
      if (connection.state === HubConnectionState.Connected) {
        void connection
          .invoke('UnsubscribeRun', selectedRunId)
          .catch(() => undefined)
          .finally(() => {
            void connection.stop().catch(() => undefined)
          })
      } else {
        void connection.stop().catch(() => undefined)
      }
    }
  }, [activeTab, token, selectedRunId, reconnectAttempt, catchUpEvents, syncRunStateFromEvents, scheduleCostRefresh, onLogout])

  const selectedRun = useMemo(
    () => runs.find((r) => r.runId === selectedRunId) ?? null,
    [runs, selectedRunId],
  )

  const runEvents = useMemo(
    () => (selectedRunId ? events.filter((e) => e.runId === selectedRunId) : []),
    [events, selectedRunId],
  )

  const liveRunInfo = useMemo(
    () => (selectedRun ? deriveCurrentState(selectedRun, runEvents) : null),
    [selectedRun, runEvents],
  )

  const baseGraph = useMemo(
    () => (selectedRun ? buildPipelineGraph(selectedRun, runEvents) : { nodes: [], edges: [] }),
    [selectedRun, runEvents],
  )

  const effectiveSelectedNodeId = useMemo(() => {
    if (
      hasManualNodeSelection &&
      selectedNodeId &&
      baseGraph.nodes.some((n) => n.id === selectedNodeId)
    ) {
      return selectedNodeId
    }
    return selectDefaultNodeId(baseGraph.nodes, runEvents)
  }, [hasManualNodeSelection, selectedNodeId, baseGraph.nodes, runEvents])

  const graph = useMemo(
    () => ({
      edges: baseGraph.edges,
      nodes: baseGraph.nodes.map((node) => {
        const isSelected = effectiveSelectedNodeId !== null && node.id === effectiveSelectedNodeId
        return {
          ...node,
          selected: isSelected,
          data: {
            ...node.data,
            selected: isSelected,
          },
        }
      }),
    }),
    [baseGraph, effectiveSelectedNodeId],
  )

  const selectedNodeLabel = useMemo(() => {
    const match = graph.nodes.find((n) => n.id === effectiveSelectedNodeId)
    return match?.data.label ?? 'Sin nodo'
  }, [graph.nodes, effectiveSelectedNodeId])

  const handleCancelLiveRun = useCallback(
    async (runId: string) => {
      setCancellingRunIds((prev) => {
        const next = new Set(prev)
        next.add(runId)
        return next
      })
      if (selectedRunIdRef.current === runId) {
        setErrorMessage(null)
      }
      try {
        const result = await cancelRun(token, runId)
        setRuns((prev) =>
          prev.map((r) => {
            if (r.runId !== runId) {
              return r
            }
            const alreadyTerminal =
              r.state === 'PullRequestCreated' || r.state === 'Failed' || r.state === 'Cancelled'
            const nextTerminal =
              result.state === 'PullRequestCreated' ||
              result.state === 'Failed' ||
              result.state === 'Cancelled'
            if (alreadyTerminal && !nextTerminal) {
              return r
            }
            return { ...r, state: result.state }
          }),
        )
        if (selectedRunIdRef.current === runId) {
          void catchUpEvents(runId, lastContiguousSeqRef.current)
        }
      } catch (err) {
        if (err instanceof UnauthorizedError) {
          onLogout(err.message)
        } else if (selectedRunIdRef.current === runId) {
          setErrorMessage(
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
    [token, onLogout, catchUpEvents],
  )

  const handleNodeClick = useCallback((_event: React.MouseEvent, node: PipelineFlowNode) => {
    setSelectedNodeId(node.id)
    setHasManualNodeSelection(true)
  }, [])

  const prOpenedEvent = useMemo(
    () => runEvents.find((e) => e.type === 'PullRequestOpened') ?? null,
    [runEvents],
  )

  const hubPill =
    hubStatus === 'connected'
      ? { className: 'pill pill-live', label: 'En vivo' }
      : hubStatus === 'reconnecting'
        ? { className: 'pill pill-reconnecting', label: 'Reconectando…' }
        : hubStatus === 'connecting'
          ? { className: 'pill', label: 'Conectando…' }
          : { className: 'pill pill-error', label: 'Sin conexión en vivo' }

  return (
    <div className="panel-shell">
      <header className="topbar">
        <div className="topbar-left">
          <span className="brand">CodeNetix</span>
          <nav className="view-tabs" role="tablist" aria-label="Vistas del Panel">
            <button
              id="tab-live"
              type="button"
              role="tab"
              aria-selected={activeTab === 'live'}
              aria-controls="panel-live"
              className={`view-tab${activeTab === 'live' ? ' active' : ''}`}
              onClick={() => setActiveTab('live')}
            >
              En vivo
            </button>
            <button
              id="tab-history"
              type="button"
              role="tab"
              aria-selected={activeTab === 'history'}
              aria-controls="panel-history"
              className={`view-tab${activeTab === 'history' ? ' active' : ''}`}
              onClick={() => setActiveTab('history')}
            >
              Historial
            </button>
          </nav>
          <span className="pill pill-live">Listo</span>
          {activeTab === 'live' && selectedRun ? (
            <span className={hubPill.className}>{hubPill.label}</span>
          ) : null}
          {activeTab === 'live' && selectedRun && hubStatus === 'disconnected' ? (
            <button
              type="button"
              className="btn"
              onClick={() => setReconnectAttempt((n) => n + 1)}
            >
              Reconectar
            </button>
          ) : null}
        </div>
        <div className="topbar-right">
          <button
            type="button"
            className="btn btn-primary"
            onClick={() => setLaunchModalOpen(true)}
          >
            Lanzar trabajo
          </button>
          {activeTab === 'live' ? (
            <button type="button" className="btn" onClick={() => void refreshRuns()} disabled={loadingRuns}>
              {loadingRuns ? 'Actualizando…' : 'Actualizar'}
            </button>
          ) : null}
          <button type="button" className="btn" onClick={() => onLogout()}>
            Cambiar token
          </button>
        </div>
      </header>

      {activeTab === 'history' ? (
        <RunHistoryView
          token={token}
          runs={historyRuns}
          loadingRuns={loadingHistoryRuns}
          runsError={historyError}
          onRefreshRuns={() => void refreshHistoryRuns()}
          onOpenLive={handleOpenLiveFromHistory}
          onLogout={onLogout}
        />
      ) : (
      <div id="panel-live" role="tabpanel" aria-labelledby="tab-live" className="panel-body">
        <aside className="sidebar">
          <div className="sidebar-header">
            <h2>Ejecuciones ({runs.length})</h2>
          </div>
          <div className="runs-list">
            {runs.length === 0 ? (
              <p className="run-meta" style={{ padding: '0.75rem' }}>
                Todavía no hay Ejecuciones registradas.
              </p>
            ) : (
              runs.map((run) => (
                <button
                  key={run.runId}
                  type="button"
                  className={`run-item${run.runId === selectedRunId ? ' selected' : ''}`}
                  onClick={() => {
                    if (run.runId !== selectedRunId) {
                      setEvents([])
                      setSelectedNodeId(null)
                      setHasManualNodeSelection(false)
                      lastContiguousSeqRef.current = 0
                      setSelectedRunId(run.runId)
                    }
                  }}
                >
                  <div className="run-item-top">
                    <span className="run-short-id">#{run.shortId}</span>
                    <span className={statePillClass(run.state)}>{STATE_LABELS[run.state]}</span>
                  </div>
                  <div className="run-object-key" title={run.objectKey}>
                    {run.objectKey}
                  </div>
                  <div className="run-meta">{new Date(run.createdAt).toLocaleString('es-ES')}</div>
                </button>
              ))
            )}
          </div>
        </aside>

        <main className="workspace">
          {errorMessage ? (
            <div className="error-banner" style={{ margin: '0.75rem 1.25rem 0' }} role="alert">
              {errorMessage}
            </div>
          ) : null}

          {selectedRun && liveRunInfo ? (
            <>
              <div className="run-header">
                <div>
                  <h1>
                    Ejecución <code>#{selectedRun.shortId}</code> · {selectedRun.objectKey}
                  </h1>
                  <div className="run-header-sub">
                    Estado: <strong>{STATE_LABELS[liveRunInfo.state]}</strong>
                    {' · '}Coste: <strong>{formatCostDisplay(selectedRun.cost)}</strong>
                    {liveRunInfo.failureReason ? ` · Motivo: ${liveRunInfo.failureReason}` : ''}
                    {liveRunInfo.failureDetail ? ` — ${liveRunInfo.failureDetail}` : ''}
                  </div>
                </div>
                <div className="run-header-actions">
                  <button
                    type="button"
                    className="btn btn-danger"
                    disabled={
                      !isRunCancellable(liveRunInfo.state) ||
                      cancellingRunIds.has(selectedRun.runId)
                    }
                    title={
                      isRunCancellable(liveRunInfo.state)
                        ? 'Cancelar esta Ejecución sin abrir un PR'
                        : liveRunInfo.state === 'Deciding'
                          ? 'No se puede cancelar mientras la Ejecución está en Deciding (publicando el PR).'
                          : `No se puede cancelar una Ejecución en estado ${STATE_LABELS[liveRunInfo.state]}.`
                    }
                    onClick={() => void handleCancelLiveRun(selectedRun.runId)}
                  >
                    {cancellingRunIds.has(selectedRun.runId) ? 'Cancelando…' : 'Cancelar'}
                  </button>
                  {prOpenedEvent &&
                  prOpenedEvent.type === 'PullRequestOpened' &&
                  isSafeHttpUrl(prOpenedEvent.url) ? (
                    <a
                      href={prOpenedEvent.url}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="btn btn-primary"
                    >
                      Ver PR #{prOpenedEvent.number}
                    </a>
                  ) : null}
                </div>
              </div>

              <div className="graph-container">
                <ReactFlow
                  nodes={graph.nodes}
                  edges={graph.edges}
                  nodeTypes={nodeTypes}
                  onNodeClick={handleNodeClick}
                  fitView
                  fitViewOptions={{ padding: 0.2 }}
                  nodesDraggable={false}
                  nodesConnectable={false}
                  elementsSelectable
                  proOptions={{ hideAttribution: true }}
                >
                  <Background color="#1e293b" gap={20} />
                  <Controls showInteractive={false} />
                </ReactFlow>
              </div>

              <div className="inspector-panel">
                <LiveTerminal
                  runId={selectedRun.runId}
                  selectedNodeId={effectiveSelectedNodeId}
                  selectedNodeLabel={selectedNodeLabel}
                  events={runEvents}
                  scopeMode={terminalScopeMode}
                  onScopeModeChange={setTerminalScopeMode}
                />

                <section className="events-panel">
                  <div className="events-panel-header">
                    <span>Eventos en vivo ({runEvents.length})</span>
                    <span>Último seq: {runEvents.length > 0 ? runEvents[runEvents.length - 1].seq : 0}</span>
                  </div>
                  <div className="events-list">
                    {[...runEvents].reverse().map((ev) => (
                      <div key={ev.seq} className="event-row">
                        <span className="event-seq">#{ev.seq}</span>
                        <span className="event-type">{ev.type}</span>
                        <span className="event-detail">{summarizeEvent(ev)}</span>
                        <span className="event-time">{formatTime(ev.at)}</span>
                      </div>
                    ))}
                  </div>
                </section>
              </div>
            </>
          ) : selectedRunId !== null && loadingRuns ? (
            <div className="empty-workspace">
              <p>Cargando Ejecución…</p>
            </div>
          ) : (
            <div className="empty-workspace">
              <h2>CodeNetix está en marcha</h2>
              <p>Lanza una Ejecución sobre una issue de GitHub para ver la cadena de montaje en directo:</p>
              <pre>
                <code>{exampleCommand}</code>
              </pre>
            </div>
          )}
        </main>
      </div>
      )}

      <LaunchWorkModal
        open={launchModalOpen}
        token={token}
        onClose={handleCloseLaunchModal}
        onRunLaunched={handleRunLaunched}
        onRefreshRuns={handleRefreshRunsFromModal}
        onLogout={onLogout}
      />
    </div>
  )
}

export default function App() {
  const [healthState, setHealthState] = useState<HealthViewState>({ kind: 'loading' })
  const [token, setToken] = useState<string | null>(() => loadStoredToken())
  const [initialRuns, setInitialRuns] = useState<RunSummary[] | null>(null)
  const [logoutReason, setLogoutReason] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined

    const load = async () => {
      try {
        const health = await fetchHealth(controller.signal)
        setHealthState({ kind: 'health', health })
        if (health.status === 'Starting') {
          timer = setTimeout(load, retryDelayMs)
        }
      } catch {
        if (controller.signal.aborted) return
        setHealthState({ kind: 'error' })
        timer = setTimeout(load, retryDelayMs)
      }
    }

    void load()
    return () => {
      controller.abort()
      clearTimeout(timer)
    }
  }, [])

  const handleAuthenticated = useCallback((validatedToken: string, loadedRuns: RunSummary[]) => {
    setLogoutReason(null)
    setInitialRuns(loadedRuns)
    setToken(validatedToken)
  }, [])

  const handleLogout = useCallback((reason?: string) => {
    clearStoredToken()
    setInitialRuns(null)
    setLogoutReason(reason ?? null)
    setToken(null)
  }, [])

  if (healthState.kind === 'loading') {
    return (
      <main className="status-screen">
        <section className="status-card">
          <p>Comprobando el estado de CodeNetix…</p>
        </section>
      </main>
    )
  }

  if (healthState.kind === 'error') {
    return (
      <main className="status-screen">
        <section className="status-card">
          <h1>No se puede contactar con CodeNetix</h1>
          <p>Reintentando…</p>
        </section>
      </main>
    )
  }

  const { health } = healthState

  if (health.status === 'Starting') {
    return (
      <main className="status-screen">
        <section className="status-card">
          <h1>CodeNetix está arrancando…</h1>
        </section>
      </main>
    )
  }

  if (health.status === 'Diagnostic') {
    return (
      <main className="status-screen">
        <section className="status-card">
          <h1>CodeNetix está en modo diagnóstico</h1>
          <p>La instancia está en marcha, pero no procesará trabajo hasta que completes la configuración:</p>
          <ul>
            {health.reasons.map((reason) => (
              <li key={reason}>{reason}</li>
            ))}
          </ul>
          <p>Define las variables que faltan y reinicia el contenedor.</p>
        </section>
      </main>
    )
  }

  if (!token) {
    return <TokenGate notice={logoutReason} onAuthenticated={handleAuthenticated} />
  }

  return <LivePanel token={token} initialRuns={initialRuns} onLogout={handleLogout} />
}
