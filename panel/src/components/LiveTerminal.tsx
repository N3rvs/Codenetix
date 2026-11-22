import { useEffect, useMemo, useRef } from 'react'
import { FitAddon } from '@xterm/addon-fit'
import { Terminal } from '@xterm/xterm'
import '@xterm/xterm/css/xterm.css'
import type { RunEvent } from '../api/generated'
import {
  createOutputBatcher,
  DEFAULT_BATCH_WINDOW_MS,
  type OutputBatcher,
} from '../terminal/outputBatcher'
import {
  computeTerminalSyncAction,
  extractStageTerminalView,
  formatByteLimit,
  formatTimelineItemForTerminal,
  isRunInTerminalState,
  type TerminalScopeMode,
} from '../terminal/stageOutput'

export interface LiveTerminalProps {
  runId: string
  selectedNodeId: string | null
  selectedNodeLabel: string
  events: readonly RunEvent[]
  scopeMode: TerminalScopeMode
  onScopeModeChange: (mode: TerminalScopeMode) => void
}

export function LiveTerminal({
  runId,
  selectedNodeId,
  selectedNodeLabel,
  events,
  scopeMode,
  onScopeModeChange,
}: LiveTerminalProps) {
  const containerRef = useRef<HTMLDivElement | null>(null)
  const terminalRef = useRef<Terminal | null>(null)
  const batcherRef = useRef<OutputBatcher | null>(null)
  const writtenSeqsRef = useRef<number[]>([])
  const contextKeyRef = useRef<string>('')

  const stageView = useMemo(
    () => extractStageTerminalView(events, selectedNodeId, scopeMode, runId),
    [events, selectedNodeId, scopeMode, runId],
  )

  const isTerminalState = useMemo(() => isRunInTerminalState(events, runId), [events, runId])

  const contextKey = `${runId}::${scopeMode}::${scopeMode === 'all' ? '*' : (selectedNodeId ?? '')}`

  // Montaje único de la instancia de xterm.js + FitAddon + ResizeObserver protegido con rAF.
  useEffect(() => {
    const container = containerRef.current
    if (!container) {
      return
    }

    const terminal = new Terminal({
      convertEol: false,
      disableStdin: true,
      cursorBlink: false,
      cursorStyle: 'bar',
      fontSize: 12.5,
      lineHeight: 1.35,
      fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
      scrollback: 5000,
      theme: {
        background: '#090d14',
        foreground: '#e6edf7',
        cursor: '#38bdf8',
        selectionBackground: 'rgba(56, 189, 248, 0.25)',
        black: '#0f172a',
        red: '#f87171',
        green: '#34d399',
        yellow: '#fbbf24',
        blue: '#38bdf8',
        magenta: '#c084fc',
        cyan: '#22d3ee',
        white: '#e6edf7',
      },
    })

    const fitAddon = new FitAddon()
    terminal.loadAddon(fitAddon)
    terminal.open(container)

    terminalRef.current = terminal

    let rafId: number | null = null
    const scheduleSafeFit = () => {
      if (rafId !== null) {
        cancelAnimationFrame(rafId)
      }
      rafId = requestAnimationFrame(() => {
        rafId = null
        if (container.clientWidth > 0 && container.clientHeight > 0) {
          try {
            fitAddon.fit()
          } catch {
            // Ignorar errores transitorios de medición durante cambios de layout.
          }
        }
      })
    }

    scheduleSafeFit()

    let observer: ResizeObserver | null = null
    if (typeof ResizeObserver !== 'undefined') {
      observer = new ResizeObserver(() => {
        scheduleSafeFit()
      })
      observer.observe(container)
    }

    return () => {
      if (rafId !== null) {
        cancelAnimationFrame(rafId)
      }
      batcherRef.current?.dispose()
      batcherRef.current = null
      observer?.disconnect()
      terminal.dispose()
      terminalRef.current = null
      writtenSeqsRef.current = []
      contextKeyRef.current = ''
    }
  }, [])

  // Sincronización incremental por prefijo exacto (≤ 250 ms) y reset limpio ante cambios de contexto,
  // rellenos de huecos `afterSeq` o re-correlación tardía de `stageId`.
  useEffect(() => {
    const terminal = terminalRef.current
    if (!terminal) {
      return
    }

    const action = computeTerminalSyncAction(
      contextKeyRef.current,
      contextKey,
      writtenSeqsRef.current,
      stageView.timelineItems,
      isTerminalState,
    )

    if (action.kind === 'noop') {
      return
    }

    if (action.kind === 'reset') {
      contextKeyRef.current = contextKey
      batcherRef.current?.dispose()
      batcherRef.current = createOutputBatcher((combined) => {
        terminalRef.current?.write(combined)
      }, DEFAULT_BATCH_WINDOW_MS)

      terminal.reset()
      writtenSeqsRef.current = action.nextWrittenSeqs

      for (const item of action.itemsToWrite) {
        const formatted = formatTimelineItemForTerminal(item)
        if (formatted.length > 0) {
          batcherRef.current.push(formatted)
        }
      }
      batcherRef.current.flushNow()
      return
    }

    if (!batcherRef.current) {
      batcherRef.current = createOutputBatcher((combined) => {
        terminalRef.current?.write(combined)
      }, DEFAULT_BATCH_WINDOW_MS)
    }

    for (const item of action.itemsToWrite) {
      const formatted = formatTimelineItemForTerminal(item)
      if (formatted.length > 0) {
        batcherRef.current.push(formatted)
      }
    }
    writtenSeqsRef.current = action.nextWrittenSeqs

    if (action.flushImmediately) {
      batcherRef.current.flushNow()
    }
  }, [contextKey, stageView, isTerminalState])

  const formattedLimit = formatByteLimit(stageView.truncated.maxBytes)
  const chunkCount = stageView.outputEvents.length
  const isEmpty = chunkCount === 0 && !stageView.truncated.isTruncated

  return (
    <section className="terminal-panel" aria-label="Terminal en vivo">
      <div className="terminal-header">
        <div className="terminal-title-group">
          <span className="terminal-title">Terminal en vivo</span>
          <span className="terminal-node-badge">
            {scopeMode === 'all' ? 'Toda la Ejecución' : selectedNodeLabel}
          </span>
          <span className="terminal-chunk-count">
            {chunkCount} {chunkCount === 1 ? 'fragmento' : 'fragmentos'}
          </span>
        </div>

        <div className="terminal-scope-toggle" role="group" aria-label="Ámbito de salida">
          <button
            type="button"
            className={`terminal-scope-btn${scopeMode === 'node' ? ' active' : ''}`}
            aria-pressed={scopeMode === 'node'}
            onClick={() => onScopeModeChange('node')}
          >
            Solo este nodo
          </button>
          <button
            type="button"
            className={`terminal-scope-btn${scopeMode === 'all' ? ' active' : ''}`}
            aria-pressed={scopeMode === 'all'}
            onClick={() => onScopeModeChange('all')}
          >
            Toda la Ejecución
          </button>
        </div>
      </div>

      {stageView.truncated.isTruncated ? (
        <div className="terminal-truncation-banner" role="alert">
          Salida truncada: se alcanzó el límite de <strong>{formattedLimit}</strong> por Etapa. Los fragmentos
          posteriores han sido descartados.
        </div>
      ) : null}

      <div className="terminal-viewport-wrapper">
        <div ref={containerRef} className="terminal-viewport" />
        {isEmpty ? (
          <div className="terminal-empty-overlay">
            {scopeMode === 'all'
              ? 'Esta Ejecución todavía no ha emitido salida.'
              : `El nodo «${selectedNodeLabel}» todavía no ha emitido salida.`}
          </div>
        ) : null}
      </div>
    </section>
  )
}
