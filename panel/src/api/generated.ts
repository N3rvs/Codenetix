// Archivo generado automáticamente desde /openapi/v1.json (AD-14). No editar a mano.
// Regenerar con: npm run generate:api

export type ConfigSource = "file" | "defaults"

export type FailureReason = "InvalidRepoConfig" | "CloneFailed" | "NotAutomatable" | "SchemaInvalid" | "TestsFailed" | "ReviewRejected" | "ReviewRoundsExhausted" | "BudgetExceeded" | "Timeout" | "HostRestarted" | "StageError" | null

export type NotAutomatableCategory = "missing_information" | "too_large" | "out_of_scope"

export type NotificationKind = "NotAutomatable" | "PullRequestCreated" | "RunFailed" | "RunCancelled"

export interface ProblemDetails {
  type?: string | null
  title?: string | null
  status?: number | null
  detail?: string | null
  instance?: string | null
}

export interface PullRequestInfo {
  number: number
  url: string
  branchName: string
  commitSha: string
  title: string
  createdAt: string
}

export type ReviewerKind = "security" | "quality" | "tests"

export interface ReviewFinding {
  severity: string
  file: string
  line: number | null
  message: string
}

export type RiskClass = "Minor" | "NeedsAttention"

export type RunEvent =
  | RunEventRunCreated
  | RunEventStateChanged
  | RunEventRepositoryPrepared
  | RunEventRepoConfigResolved
  | RunEventAnalystOutputRejected
  | RunEventSpecGenerated
  | RunEventAnalysisRejected
  | RunEventIssueCommented
  | RunEventNotificationFailed
  | RunEventSandboxCreated
  | RunEventSandboxReleased
  | RunEventTokensConsumed
  | RunEventEgressBlocked
  | RunEventOutputAppended
  | RunEventOutputTruncated
  | RunEventCodingAttemptFailed
  | RunEventReviewerOutputRejected
  | RunEventVerdictRecorded
  | RunEventRiskClassified
  | RunEventPullRequestOpened

export interface RunEventAnalysisRejected {
  type: "AnalysisRejected"
  category: NotAutomatableCategory
  attempts: number
  model: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventAnalystOutputRejected {
  type: "AnalystOutputRejected"
  attempt: number
  error: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventCodingAttemptFailed {
  type: "CodingAttemptFailed"
  attempt: number
  reason: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventEgressBlocked {
  type: "EgressBlocked"
  target: string
  reason: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventIssueCommented {
  type: "IssueCommented"
  kind: NotificationKind
  commentUrl: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventNotificationFailed {
  type: "NotificationFailed"
  kind: NotificationKind
  error: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventOutputAppended {
  type: "OutputAppended"
  text: string
  stream?: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventOutputTruncated {
  type: "OutputTruncated"
  maxBytes?: number
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventPullRequestOpened {
  type: "PullRequestOpened"
  number: number
  url: string
  branch: string
  commitSha: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventRepoConfigResolved {
  type: "RepoConfigResolved"
  source: ConfigSource
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventRepositoryPrepared {
  type: "RepositoryPrepared"
  baseSha: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventReviewerOutputRejected {
  type: "ReviewerOutputRejected"
  round: number
  reviewer: ReviewerKind
  attempt: number
  error: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventRiskClassified {
  type: "RiskClassified"
  riskClass: RiskClass
  diffLines: number
  matchedProtectedPaths: string[]
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventRunCreated {
  type: "RunCreated"
  eventId: string
  objectKey: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventSandboxCreated {
  type: "SandboxCreated"
  image: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventSandboxReleased {
  type: "SandboxReleased"
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventSpecGenerated {
  type: "SpecGenerated"
  attempts: number
  model: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventStateChanged {
  type: "StateChanged"
  from: null | RunState
  to: RunState
  failureReason?: null | FailureReason
  detail?: string | null
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventTokensConsumed {
  type: "TokensConsumed"
  role: string
  provider: string
  model: string
  inputTokens: number
  outputTokens: number
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunEventVerdictRecorded {
  type: "VerdictRecorded"
  round: number
  reviewer: ReviewerKind
  decision: VerdictDecision
  findingCount: number
  model: string
  seq: number
  runId: string
  stageId?: string | null
  at: string
}

export interface RunResponse {
  runId: string
  shortId: string
  state: RunState
  failureReason: null | FailureReason
  failureDetail: string | null
  eventId: string
  objectKey: string
  createdAt: string
  updatedAt: string
  spec: null | Spec
  testReport: null | TestReport
  verdicts: RunVerdict[]
  pullRequest: null | PullRequestInfo
  events: RunEvent[]
}

export type RunState = "Received" | "Analyzing" | "Coding" | "Reviewing" | "Deciding" | "PullRequestCreated" | "Failed" | "Cancelled"

export interface RunSummary {
  runId: string
  shortId: string
  state: RunState
  failureReason: null | FailureReason
  failureDetail: string | null
  eventId: string
  objectKey: string
  createdAt: string
  updatedAt: string
}

export interface RunVerdict {
  stageId: string
  round: number
  reviewer: ReviewerKind
  decision: VerdictDecision
  summary: string
  model: string
  attempts: number
  createdAt: string
  findings: ReviewFinding[]
}

export interface Spec {
  problem: string
  probableCause: string
  proposedChange: string
  acceptanceCriteria: string[]
  affectedFiles: string[]
}

export interface TestCaseResult {
  name: string
  outcome: string
  errorMessage?: string | null
  stackTrace?: string | null
}

export interface TestReport {
  buildSucceeded: boolean
  totalTests: number
  passedTests: number
  failedTests: number
  skippedTests: number
  newTests: string[]
  failedTestCases: TestCaseResult[]
  duration: string
  buildOutput?: string | null
  passed?: boolean
}

export type VerdictDecision = "approved" | "changes_requested" | "rejected"

export type RunEventType = RunEvent['type']
