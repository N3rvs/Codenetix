import { useEffect, useState } from 'react'

const exampleCommand = 'docker exec codenetix codenetix fix <url-de-issue>'
const retryDelayMs = 2000

type HealthStatus = 'Starting' | 'Ready' | 'Diagnostic'

interface Health {
  status: HealthStatus
  reasons: string[]
}

type ViewState = { kind: 'loading' } | { kind: 'error' } | { kind: 'health'; health: Health }

async function fetchHealth(signal: AbortSignal): Promise<Health> {
  const response = await fetch('/api/health', { signal, headers: { Accept: 'application/json' } })
  // 503 (Starting) también trae el cuerpo con el estado.
  const body = (await response.json()) as Partial<Health>
  if (body.status !== 'Starting' && body.status !== 'Ready' && body.status !== 'Diagnostic') {
    throw new Error('Respuesta de salud no reconocida')
  }
  return { status: body.status, reasons: Array.isArray(body.reasons) ? body.reasons : [] }
}

export default function App() {
  const [state, setState] = useState<ViewState>({ kind: 'loading' })

  useEffect(() => {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined

    const load = async () => {
      try {
        const health = await fetchHealth(controller.signal)
        setState({ kind: 'health', health })
        if (health.status === 'Starting') {
          timer = setTimeout(load, retryDelayMs)
        }
      } catch {
        if (controller.signal.aborted) return
        setState({ kind: 'error' })
        timer = setTimeout(load, retryDelayMs)
      }
    }

    void load()
    return () => {
      controller.abort()
      clearTimeout(timer)
    }
  }, [])

  if (state.kind === 'loading') {
    return (
      <main>
        <p>Comprobando el estado de CodeNetix…</p>
      </main>
    )
  }

  if (state.kind === 'error') {
    return (
      <main>
        <h1>No se puede contactar con CodeNetix</h1>
        <p>Reintentando…</p>
      </main>
    )
  }

  const { health } = state

  if (health.status === 'Starting') {
    return (
      <main>
        <h1>CodeNetix está arrancando…</h1>
      </main>
    )
  }

  if (health.status === 'Diagnostic') {
    return (
      <main>
        <h1>CodeNetix está en modo diagnóstico</h1>
        <p>La instancia está en marcha, pero no procesará trabajo hasta que completes la configuración:</p>
        <ul>
          {health.reasons.map((reason) => (
            <li key={reason}>{reason}</li>
          ))}
        </ul>
        <p>Define las variables que faltan y reinicia el contenedor.</p>
      </main>
    )
  }

  return (
    <main>
      <h1>CodeNetix está en marcha</h1>
      <p>Para lanzar trabajo sobre una issue de GitHub, ejecuta:</p>
      <pre>
        <code>{exampleCommand}</code>
      </pre>
    </main>
  )
}
