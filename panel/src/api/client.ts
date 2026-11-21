import type { ProblemDetails, RunEvent, RunSummary } from './generated'

export const ADMIN_TOKEN_STORAGE_KEY = 'codenetix.adminToken'

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
    if (problem.detail) return problem.detail
    if (problem.title) return problem.title
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
    throw new Error(await readProblemDetail(response))
  }

  return (await response.json()) as RunSummary[]
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
    throw new Error(await readProblemDetail(response))
  }

  return (await response.json()) as RunEvent[]
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
