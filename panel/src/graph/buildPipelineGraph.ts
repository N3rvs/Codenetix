import type { Edge, Node } from '@xyflow/react'
import type {
  FailureReason,
  ReviewerKind,
  RunEvent,
  RunEventVerdictRecorded,
  RunState,
  RunSummary,
  VerdictDecision,
} from '../api/generated'

export type StageVisualStatus = 'pending' | 'active' | 'completed' | 'failed' | 'cancelled'

export interface PipelineStageNodeData extends Record<string, unknown> {
  label: string
  stageKey: string
  status: StageVisualStatus
  subtitle?: string
  badge?: string
  selected?: boolean
}

export type PipelineFlowNode = Node<PipelineStageNodeData, 'stage'>

const DEFAULT_REVIEWERS: readonly ReviewerKind[] = ['security', 'quality', 'tests']

const REVIEWER_LABELS: Record<ReviewerKind, string> = {
  security: 'Revisor · Seguridad',
  quality: 'Revisor · Calidad',
  tests: 'Revisor · Pruebas',
}

const VERDICT_LABELS: Record<VerdictDecision, string> = {
  approved: 'Aprobado',
  changes_requested: 'Cambios pedidos',
  rejected: 'Rechazado',
}

export const STATE_LABELS: Record<RunState, string> = {
  Received: 'Recibida',
  Analyzing: 'Analizando',
  Coding: 'Programando',
  Reviewing: 'Revisando',
  Deciding: 'Decidiendo',
  PullRequestCreated: 'PR abierto',
  Failed: 'Fallida',
  Cancelled: 'Cancelada',
}

export function deriveCurrentState(run: RunSummary, events: readonly RunEvent[]): {
  state: RunState
  failureReason: FailureReason
  failureDetail: string | null
} {
  let state: RunState = run.state
  let failureReason: FailureReason = run.failureReason
  let failureDetail: string | null = run.failureDetail

  for (const ev of events) {
    if (ev.type === 'StateChanged') {
      state = ev.to
      if (ev.failureReason !== undefined) {
        failureReason = ev.failureReason
      }
      if (ev.detail !== undefined) {
        failureDetail = ev.detail
      }
    }
  }

  return { state, failureReason, failureDetail }
}

function stageOrderIndex(state: RunState): number {
  switch (state) {
    case 'Received':
      return 0
    case 'Analyzing':
      return 1
    case 'Coding':
      return 2
    case 'Reviewing':
      return 4
    case 'Deciding':
      return 5
    case 'PullRequestCreated':
      return 6
    case 'Failed':
    case 'Cancelled':
      return -1
  }
}

export function buildPipelineGraph(
  run: RunSummary,
  events: readonly RunEvent[],
  selectedNodeId: string | null = null,
): { nodes: PipelineFlowNode[]; edges: Edge[] } {
  const { state: currentState, failureReason } = deriveCurrentState(run, events)

  // Detectar hasta qué etapa llegó antes de fallar o cancelarse y el número de ronda real.
  let lastNonTerminalState: RunState = 'Received'
  let reviewEntryCount = 0
  let latestVerdictRound = 0
  let specModel: string | undefined
  let codingAttemptsFailed = 0
  let prNumber: number | undefined
  let riskClass: string | undefined

  const extraReviewers: ReviewerKind[] = []
  const latestVerdictByReviewer = new Map<ReviewerKind, RunEventVerdictRecorded>()

  for (const ev of events) {
    if (ev.type === 'StateChanged') {
      if (ev.to === 'Reviewing') {
        reviewEntryCount += 1
      }
      if (ev.to !== 'Failed' && ev.to !== 'Cancelled' && ev.to !== 'PullRequestCreated') {
        lastNonTerminalState = ev.to
      }
    } else if (ev.type === 'SpecGenerated') {
      specModel = ev.model
    } else if (ev.type === 'CodingAttemptFailed') {
      codingAttemptsFailed += 1
    } else if (ev.type === 'ReviewerOutputRejected') {
      latestVerdictRound = Math.max(latestVerdictRound, ev.round)
      if (!DEFAULT_REVIEWERS.includes(ev.reviewer) && !extraReviewers.includes(ev.reviewer)) {
        extraReviewers.push(ev.reviewer)
      }
    } else if (ev.type === 'VerdictRecorded') {
      latestVerdictRound = Math.max(latestVerdictRound, ev.round)
      if (!DEFAULT_REVIEWERS.includes(ev.reviewer) && !extraReviewers.includes(ev.reviewer)) {
        extraReviewers.push(ev.reviewer)
      }
      const prev = latestVerdictByReviewer.get(ev.reviewer)
      if (!prev || ev.round >= prev.round) {
        latestVerdictByReviewer.set(ev.reviewer, ev)
      }
    } else if (ev.type === 'RiskClassified') {
      riskClass = ev.riskClass === 'Minor' ? 'Riesgo menor' : 'Requiere atención'
    } else if (ev.type === 'PullRequestOpened') {
      prNumber = ev.number
    }
  }

  const currentRound = Math.max(reviewEntryCount, latestVerdictRound, 1)

  // Mantener siempre DEFAULT_REVIEWERS en orden canónico + cualquier revisor adicional al final.
  const activeReviewers: ReviewerKind[] = [...DEFAULT_REVIEWERS, ...extraReviewers]

  const isFailed = currentState === 'Failed'
  const isCancelled = currentState === 'Cancelled'
  const isSucceeded = currentState === 'PullRequestCreated'

  const effectiveIdx =
    isFailed || isCancelled ? stageOrderIndex(lastNonTerminalState) : stageOrderIndex(currentState)

  function statusForLinearStage(idx: number, stageState: RunState): StageVisualStatus {
    if (isSucceeded) {
      return 'completed'
    }
    if (isFailed) {
      if (lastNonTerminalState === stageState) return 'failed'
      return idx < effectiveIdx ? 'completed' : 'pending'
    }
    if (isCancelled) {
      if (lastNonTerminalState === stageState) return 'cancelled'
      return idx < effectiveIdx ? 'completed' : 'pending'
    }
    if (currentState === stageState) {
      return 'active'
    }
    return idx < effectiveIdx ? 'completed' : 'pending'
  }

  // Testing se ejecuta al final de cada intento de Coding antes de pasar a Reviewing.
  let testingStatus: StageVisualStatus = 'pending'
  if (isSucceeded || effectiveIdx >= 4) {
    testingStatus = 'completed'
  } else if (isFailed && failureReason === 'TestsFailed') {
    testingStatus = 'failed'
  } else if (isFailed && lastNonTerminalState === 'Coding') {
    testingStatus = codingAttemptsFailed > 0 ? 'failed' : 'pending'
  } else if (currentState === 'Coding' && codingAttemptsFailed > 0) {
    testingStatus = 'active'
  }

  const receivedStatus = statusForLinearStage(0, 'Received')
  const analyzingStatus = statusForLinearStage(1, 'Analyzing')
  const codingStatus: StageVisualStatus =
    isFailed && failureReason === 'TestsFailed'
      ? 'completed'
      : statusForLinearStage(2, 'Coding')
  const decidingStatus = statusForLinearStage(5, 'Deciding')

  let terminalLabel = 'PR abierto'
  let terminalStatus: StageVisualStatus = 'pending'
  let terminalSubtitle: string | undefined = prNumber ? `PR #${prNumber}` : 'Esperando decisión'

  if (isSucceeded) {
    terminalLabel = 'PR abierto'
    terminalStatus = 'completed'
    terminalSubtitle = prNumber ? `PR #${prNumber}` : 'Pull Request creado'
  } else if (isFailed) {
    terminalLabel = 'Fallida'
    terminalStatus = 'failed'
    terminalSubtitle = failureReason ?? 'Error de ejecución'
  } else if (isCancelled) {
    terminalLabel = 'Cancelada'
    terminalStatus = 'cancelled'
    terminalSubtitle = 'Detenida por el operador'
  }

  const baseY = 140
  const nodes: PipelineFlowNode[] = [
    {
      id: 'received',
      type: 'stage',
      position: { x: 20, y: baseY },
      data: {
        label: 'Received',
        stageKey: 'received',
        status: receivedStatus,
        subtitle: 'Clonado y config',
      },
    },
    {
      id: 'analyzing',
      type: 'stage',
      position: { x: 230, y: baseY },
      data: {
        label: 'Analyzing',
        stageKey: 'analyzing',
        status: analyzingStatus,
        subtitle: specModel ? `Spec · ${specModel}` : 'Generar Spec',
      },
    },
    {
      id: 'coding',
      type: 'stage',
      position: { x: 440, y: baseY },
      data: {
        label: 'Coding',
        stageKey: 'coding',
        status: codingStatus,
        subtitle:
          codingAttemptsFailed > 0
            ? `Reintentos: ${codingAttemptsFailed}`
            : currentRound > 1 && currentState === 'Coding'
              ? `Correcciones (R${currentRound})`
              : 'Implementar Spec',
      },
    },
    {
      id: 'testing',
      type: 'stage',
      position: { x: 650, y: baseY },
      data: {
        label: 'Testing',
        stageKey: 'testing',
        status: testingStatus,
        subtitle: 'Validar pruebas',
      },
    },
  ]

  // Nodos Review: 1 nodo por cada Revisor activo, apilados verticalmente.
  const reviewX = 880
  const reviewSpacingY = 104
  const reviewStartY = baseY - ((activeReviewers.length - 1) * reviewSpacingY) / 2

  const reviewerNodeIds: string[] = []
  activeReviewers.forEach((reviewer, index) => {
    const id = `review-${reviewer}`
    reviewerNodeIds.push(id)

    const verdict = latestVerdictByReviewer.get(reviewer)
    let status: StageVisualStatus = 'pending'
    let subtitle = 'Pendiente'
    let badge: string | undefined

    if (currentState === 'Reviewing' && (!verdict || verdict.round < currentRound)) {
      // En una ronda en curso sin veredicto todavía para esa ronda, el revisor está activo.
      status = 'active'
      badge = `R${currentRound}`
      subtitle = `Revisando (R${currentRound})…`
    } else if (verdict) {
      badge = `R${verdict.round}`
      subtitle = `${VERDICT_LABELS[verdict.decision]}${verdict.findingCount > 0 ? ` (${verdict.findingCount})` : ''}`
      if (verdict.decision === 'approved') {
        status = 'completed'
      } else if (verdict.decision === 'rejected') {
        status = 'failed'
      } else {
        // changes_requested: nunca se marca completed; si ha vuelto a Coding, queda pendiente de nueva ronda.
        if (isFailed) {
          status = 'failed'
        } else if (isCancelled) {
          status = 'cancelled'
        } else if (currentState === 'Reviewing') {
          status = 'active'
        } else {
          status = 'pending'
          subtitle = `Cambios pedidos (R${verdict.round})`
        }
      }
    } else if (isSucceeded || effectiveIdx > 4) {
      status = 'completed'
      subtitle = 'Aprobado'
    } else if (isCancelled && lastNonTerminalState === 'Reviewing') {
      status = 'cancelled'
      subtitle = 'Cancelado'
    } else if (isFailed && lastNonTerminalState === 'Reviewing') {
      status = 'failed'
      subtitle = 'Interrumpido'
    }

    nodes.push({
      id,
      type: 'stage',
      position: { x: reviewX, y: reviewStartY + index * reviewSpacingY },
      data: {
        label: REVIEWER_LABELS[reviewer] ?? `Revisor · ${reviewer}`,
        stageKey: id,
        status,
        subtitle,
        badge,
      },
    })
  })

  nodes.push(
    {
      id: 'deciding',
      type: 'stage',
      position: { x: 1130, y: baseY },
      data: {
        label: 'Deciding',
        stageKey: 'deciding',
        status: decidingStatus,
        subtitle: riskClass ?? 'Política y PR',
      },
    },
    {
      id: 'terminal',
      type: 'stage',
      position: { x: 1340, y: baseY },
      data: {
        label: terminalLabel,
        stageKey: 'terminal',
        status: terminalStatus,
        subtitle: terminalSubtitle,
      },
    },
  )

  const edges: Edge[] = [
    {
      id: 'e-received-analyzing',
      source: 'received',
      target: 'analyzing',
      animated: analyzingStatus === 'active',
    },
    {
      id: 'e-analyzing-coding',
      source: 'analyzing',
      target: 'coding',
      animated: codingStatus === 'active',
    },
    {
      id: 'e-coding-testing',
      source: 'coding',
      target: 'testing',
      animated: testingStatus === 'active',
    },
  ]

  for (const reviewerId of reviewerNodeIds) {
    const reviewerNode = nodes.find((n) => n.id === reviewerId)
    const isRevActive = reviewerNode?.data.status === 'active'
    edges.push(
      {
        id: `e-testing-${reviewerId}`,
        source: 'testing',
        target: reviewerId,
        animated: isRevActive,
      },
      {
        id: `e-${reviewerId}-deciding`,
        source: reviewerId,
        target: 'deciding',
        animated: decidingStatus === 'active',
      },
    )
  }

  edges.push({
    id: 'e-deciding-terminal',
    source: 'deciding',
    target: 'terminal',
    animated: decidingStatus === 'active',
  })

  const nodesWithSelection: PipelineFlowNode[] = nodes.map((node) => {
    const isSelected = selectedNodeId !== null && node.id === selectedNodeId
    return {
      ...node,
      selected: isSelected,
      data: {
        ...node.data,
        selected: isSelected,
      },
    }
  })

  return { nodes: nodesWithSelection, edges }
}
