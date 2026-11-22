import type {
  CancelRunResponse,
  EventResponse,
  ProblemDetails,
  RunEvent,
  RunResponse,
  RunState,
  RunSummary,
  SubmitManualEventRequest,
  SubmitManualEventResponse,
} from './generated'

export const ADMIN_TOKEN_STORAGE_KEY = 'codenetix.adminToken'

/**
 * Comprueba que una URL externa usa exclusivamente esquema http: o https: antes de renderizar un enlace.
 */
export function isSafeHttpUrl(rawUrl: string): boolean {
  try {
    const parsed = new URL(rawUrl)
    return parsed.protocol === 'https:' || parsed.protocol === 'http:'
  } catch {
    return false
  }
}

export type HealthStatus = 'Starting' | 'Ready' | 'Diagnostic'

export interface HealthInfo {
  status: HealthStatus
  reasons: string[]
}

export class UnauthorizedError extends Error {
  constructor(message = 'El admin-token no es válido o ha caducado.') {
    super(message)
    this.name = 'UnauthorizedError'
  }
}

export class ApiHttpError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiHttpError'
    this.status = status
  }
}

export function loadStoredToken(): string | null {
  try {
    const value = window.localStorage.getItem(ADMIN_TOKEN_STORAGE_KEY)?.trim()
    return value ? value : null
  } catch {
    return null
  }
}

export function saveStoredToken(token: string): void {
  try {
    window.localStorage.setItem(ADMIN_TOKEN_STORAGE_KEY, token.trim())
  } catch {
    // Almacenamiento bloqueado o cuota excedida (p. ej. modo privado estricto).
  }
}

export function clearStoredToken(): void {
  try {
    window.localStorage.removeItem(ADMIN_TOKEN_STORAGE_KEY)
  } catch {
    // Sin acceso a localStorage.
  }
}

export async function fetchHealth(signal?: AbortSignal): Promise<HealthInfo> {
  const response = await fetch('/api/health', {
    signal,
    headers: { Accept: 'application/json' },
  })
  // 503 (Starting) también trae el cuerpo con el estado.
  const body = (await response.json()) as Partial<HealthInfo>
  if (body.status !== 'Starting' && body.status !== 'Ready' && body.status !== 'Diagnostic') {
    throw new Error('Respuesta de salud no reconocida.')
  }
  return { status: body.status, reasons: Array.isArray(body.reasons) ? body.reasons : [] }
}

async function readProblemDetail(response: Response): Promise<string> {
  try {
    const problem = (await response.json()) as ProblemDetails
    const detail = problem.detail?.trim()
    if (detail) return detail
    const title = problem.title?.trim()
    if (title) return title
  } catch {
    // Cuerpo no JSON.
  }
  return `Error HTTP ${response.status}`
}

export async function fetchRuns(
  token: string,
  limit = 50,
  signal?: AbortSignal,
): Promise<RunSummary[]> {
  const response = await fetch(`/api/runs?limit=${encodeURIComponent(String(limit))}`, {
    signal,
    headers: {
      Accept: 'application/json',
      Authorization: `Bearer ${token}`,
    },
  })

  if (response.status === 401) {
    throw new UnauthorizedError('Token rechazado por CodeNetix. Comprueba el admin-token e inténtalo de nuevo.')
  }

  if (!response.ok) {
    throw new ApiHttpError(response.status, await readProblemDetail(response))
  }

  return (await response.json()) as RunSummary[]
}

export async function fetchRunDetail(
  token: string,
  runId: string,
  signal?: AbortSignal,
): Promise<RunResponse> {
  const response = await fetch(`/api/runs/${encodeURIComponent(runId)}`, {
    signal,
    headers: {
      Accept: 'application/json',
      Authorization: `Bearer ${token}`,
    },
  })

  if (response.status === 401) {
    throw new UnauthorizedError()
  }

  if (!response.ok) {
    throw new ApiHttpError(response.status, await readProblemDetail(response))
  }

  return (await response.json()) as RunResponse
}

export async function fetchRunEvents(
  token: string,
  runId: string,
  afterSeq = 0,
  signal?: AbortSignal,
): Promise<RunEvent[]> {
  const response = await fetch(
    `/api/runs/${encodeURIComponent(runId)}/events?afterSeq=${encodeURIComponent(String(afterSeq))}`,
    {
      signal,
      headers: {
        Accept: 'application/json',
        Authorization: `Bearer ${token}`,
      },
    },
  )

  if (response.status === 401) {
    throw new UnauthorizedError()
  }

  if (!response.ok) {
    throw new ApiHttpError(response.status, await readProblemDetail(response))
  }

  return (await response.json()) as RunEvent[]
}

export function isRunCancellable(state: RunState): boolean {
  return (
    state === 'Received' ||
    state === 'Analyzing' ||
    state === 'Coding' ||
    state === 'Reviewing'
  )
}

export async function cancelRun(
  token: string,
  runId: string,
  signal?: AbortSignal,
): Promise<CancelRunResponse> {
  const response = await fetch(`/api/runs/${encodeURIComponent(runId)}/cancel`, {
    method: 'POST',
    signal,
    headers: {
      Accept: 'application/json',
      Authorization: `Bearer ${token}`,
    },
  })

  if (response.status === 401) {
    throw new UnauthorizedError()
  }

  if (!response.ok) {
    throw new ApiHttpError(response.status, await readProblemDetail(response))
  }

  return (await response.json()) as CancelRunResponse
}

/**
 * Genera un UUID v4 válido para el `requestId` de cada lanzamiento manual (AD-4).
 * Usa `crypto.randomUUID()` cuando está disponible y respalda con RFC 4122 v4 sobre `crypto.getRandomValues`.
 */
export function generateRequestId(): string {
  const cryptoApi = globalThis.crypto
  if (cryptoApi && typeof cryptoApi.randomUUID === 'function') {
    return cryptoApi.randomUUID()
  }

  const bytes = new Uint8Array(16)
  if (cryptoApi && typeof cryptoApi.getRandomValues === 'function') {
    cryptoApi.getRandomValues(bytes)
  } else {
    for (let i = 0; i < bytes.length; i += 1) {
      bytes[i] = Math.floor(Math.random() * 256)
    }
  }

  // RFC 4122 versión 4 y variante 10xx.
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80

  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

export async function submitManualEvent(
  token: string,
  url: string,
  requestId: string,
  signal?: AbortSignal,
): Promise<SubmitManualEventResponse> {
  const payload: SubmitManualEventRequest = { url, requestId }
  const response = await fetch('/api/events/manual', {
    method: 'POST',
    signal,
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify(payload),
  })

  if (response.status === 401) {
    throw new UnauthorizedError()
  }

  if (!response.ok) {
    throw new ApiHttpError(response.status, await readProblemDetail(response))
  }

  return (await response.json()) as SubmitManualEventResponse
}

export async function fetchEvent(
  token: string,
  eventId: string,
  signal?: AbortSignal,
): Promise<EventResponse> {
  const response = await fetch(`/api/events/${encodeURIComponent(eventId)}`, {
    signal,
    headers: {
      Accept: 'application/json',
      Authorization: `Bearer ${token}`,
    },
  })

  if (response.status === 401) {
    throw new UnauthorizedError()
  }

  if (!response.ok) {
    throw new ApiHttpError(response.status, await readProblemDetail(response))
  }

  return (await response.json()) as EventResponse
}

export type WaitForEventRunResult =
  | { kind: 'run'; runId: string }
  | { kind: 'ignored'; reason: string | null }
  | { kind: 'timeout' }

export interface WaitForEventRunOptions {
  pollIntervalMs?: number
  timeoutMs?: number
  signal?: AbortSignal
}

function createAbortError(): Error {
  const err = new Error('Operación cancelada.')
  err.name = 'AbortError'
  return err
}

function sleepWithAbort(ms: number, signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) {
    return Promise.reject(createAbortError())
  }

  return new Promise<void>((resolve, reject) => {
    const onAbort = () => {
      clearTimeout(timer)
      signal?.removeEventListener('abort', onAbort)
      reject(createAbortError())
    }

    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort)
      resolve()
    }, ms)

    signal?.addEventListener('abort', onAbort, { once: true })
  })
}

export async function waitForEventRun(
  token: string,
  eventId: string,
  options?: WaitForEventRunOptions,
): Promise<WaitForEventRunResult> {
  const pollIntervalMs = options?.pollIntervalMs ?? 300
  const timeoutMs = options?.timeoutMs ?? 15_000
  const signal = options?.signal

  if (signal?.aborted) {
    throw createAbortError()
  }

  const deadline = Date.now() + timeoutMs

  for (;;) {
    if (signal?.aborted) {
      throw createAbortError()
    }

    try {
      const event = await fetchEvent(token, eventId, signal)
      if (event.status === 'Ignored') {
        return { kind: 'ignored', reason: event.reason ?? null }
      }
      if (event.runId) {
        return { kind: 'run', runId: event.runId }
      }
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        throw err
      }
      if (signal?.aborted || (err instanceof Error && err.name === 'AbortError')) {
        throw err
      }
      if (err instanceof ApiHttpError && err.status >= 400 && err.status < 500) {
        throw err
      }
      // Errores transitorios de red o 5xx: reintentar hasta agotar deadline.
    }

    const remaining = deadline - Date.now()
    if (remaining <= 0) {
      return { kind: 'timeout' }
    }

    await sleepWithAbort(Math.min(pollIntervalMs, remaining), signal)
  }
}

/**
 * Fusiona eventos por `seq` ascendente sin duplicados ni pérdida (AD-3).
 */
export function mergeRunEvents(existing: readonly RunEvent[], incoming: readonly RunEvent[]): RunEvent[] {
  if (incoming.length === 0) {
    return existing as RunEvent[]
  }

  const bySeq = new Map<number, RunEvent>()
  for (const ev of existing) {
    bySeq.set(ev.seq, ev)
  }
  for (const ev of incoming) {
    bySeq.set(ev.seq, ev)
  }

  return Array.from(bySeq.values()).sort((a, b) => a.seq - b.seq)
}
