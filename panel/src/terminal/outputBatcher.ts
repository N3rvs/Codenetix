export const DEFAULT_BATCH_WINDOW_MS = 250

export interface OutputBatcher {
  /** Acumula un fragmento para volcarlo en el siguiente lote (≤ windowMs). */
  push(chunk: string): void
  /** Vuelca inmediatamente todos los fragmentos pendientes y cancela el temporizador activo. */
  flushNow(): void
  /** Cancela cualquier temporizador pendiente y descarta el búfer sin volver a escribir. */
  dispose(): void
}

/**
 * Agrupador determinista de salida para la terminal xterm.js (FR-21).
 * Garantiza que ráfagas rápidas se combinan en una única escritura en como máximo
 * `windowMs` milisegundos (250 ms por defecto) sin perder fragmentos ni alterar el orden.
 */
export function createOutputBatcher(
  onFlush: (combinedChunk: string) => void,
  windowMs: number = DEFAULT_BATCH_WINDOW_MS,
): OutputBatcher {
  const effectiveWindowMs = Number.isFinite(windowMs)
    ? Math.max(0, Math.min(windowMs, DEFAULT_BATCH_WINDOW_MS))
    : DEFAULT_BATCH_WINDOW_MS
  let buffer: string[] = []
  let timer: ReturnType<typeof setTimeout> | null = null
  let disposed = false

  const clearPendingTimer = () => {
    if (timer !== null) {
      clearTimeout(timer)
      timer = null
    }
  }

  const flushNow = () => {
    clearPendingTimer()
    if (disposed || buffer.length === 0) {
      return
    }
    const combined = buffer.join('')
    buffer = []
    onFlush(combined)
  }

  const push = (chunk: string) => {
    if (disposed || chunk.length === 0) {
      return
    }
    buffer.push(chunk)
    if (timer === null) {
      timer = setTimeout(() => {
        timer = null
        flushNow()
      }, effectiveWindowMs)
    }
  }

  const dispose = () => {
    disposed = true
    clearPendingTimer()
    buffer = []
  }

  return {
    push,
    flushNow,
    dispose,
  }
}
