---
title: 'Historia 5.2: Corte automático por presupuesto'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: '730b6f659ff2f64ef1e65d859b9ec3ff09ce2f4b'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-5-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Aunque la Historia 5.1 registra cada consumo en `run_budget` y muestra el coste estimado, una Ejecución que supera `budget_usd` en su `EffectiveConfig` sigue consumiendo tokens sin detenerse, el Gateway no corta las peticiones con `402` y el CLI de Claude Code todavía no recibe `--max-budget-usd <restante>`.

**Approach:** Activar el resultado `BudgetChargeOutcome.Exhausted` en `BudgetLedger` cuando `EstimatedCostUsd >= BudgetLimitUsd`, disparando la cancelación cooperativa de la Ejecución mediante `RunCancellationRegistry`; hacer que el Gateway (`LlmReverseProxy`) rechace nuevas peticiones con `402 Payment Required` (`codenetix:budget_exhausted`) sin reenviarlas al Proveedor; traducir el agotamiento en `ClaudeCodeAgent` (añadiendo además `--max-budget-usd <restante>` como segunda barrera y cerrando el diferido de 2.3 en `deferred-work.md`), `Analyst` y `ReviewerAgent` a un resultado explícito de presupuesto agotado; y cerrar la Ejecución en `RunOrchestrator` como `Failed(FailureReason.BudgetExceeded)` liberando Sandbox, workspace y notificando en GitHub (`NotificationKind.RunFailed`).

## Boundaries & Constraints

**Always:**
- **Detección y corte en `BudgetLedger` (AD-15, FR-13):**
  - Cuando una Ejecución tiene `BudgetLimitUsd` configurado (`EffectiveConfig.BudgetUsd`) y el coste acumulado estimable cumple `EstimatedCostUsd >= BudgetLimitUsd`, `BudgetLedger.ChargeAsync` devuelve `BudgetChargeOutcome.Exhausted` (`RemainingUsd = 0m`) y solicita inmediatamente la cancelación cooperativa por presupuesto mediante `RunCancellationRegistry.TryRequestBudgetExhausted(runId, detail)`.
  - Si el coste no es estimable (`HasUnpricedModels == true`, p. ej. modelo local sin precio), **no** se dispara un falso `Exhausted` porque no existe coste en USD comparable.
- **Barrera 1 — Gateway `402` con `codenetix:budget_exhausted` (`LlmReverseProxy`):**
  - Antes de reenviar cada petición al Proveedor del Programador, si `_budgetLedger` está disponible y la Ejecución ya agotó su presupuesto (`GetRunCostAsync` con `IsExhausted`), el Gateway responde inmediatamente **`402 Payment Required`** con cuerpo JSON que incluye `codenetix:budget_exhausted` (`type` y `error.code`) sin contactar al Proveedor upstream.
  - Exponer la constante `BudgetExhaustedCode = "codenetix:budget_exhausted"` en `CodeNetix.Contracts/Budget.cs` para evitar duplicación de cadenas entre Gateway y adaptadores.
- **Barrera 2 — `--max-budget-usd <restante>` y traducción en adaptadores (`CodeNetix.Agents`):**
  - `ClaudeCodeAgent` calcula el presupuesto restante desde `IBudgetLedger` + `EffectiveConfig.BudgetUsd`: si ya está agotado antes de lanzar el proceso devuelve `CodingResult.BudgetExhausted`; si queda presupuesto (`> 0`), añade `--max-budget-usd <restante>` (`CultureInfo.InvariantCulture`) a `BuildCommand`. Marcar como resuelta la entrada diferida de 2.3 en `deferred-work.md`.
  - Si la salida del CLI (`stdout`/`stderr`) contiene `codenetix:budget_exhausted` o `IBudgetLedger` reporta agotamiento tras el intento, `ClaudeCodeAgent` devuelve `CodingResult.BudgetExhausted` (nunca `CodingResult.Failed` genérico que consuma reintentos).
  - `AnalysisResult.BudgetExhausted` y `ReviewResult` (`BudgetExhausted = true`) + `BudgetTrackingChatClient` propagan el agotamiento de presupuesto en Analista y Revisores sin consumir reintentos de esquema ni enmascararlo como `ProviderError`.
- **Cierre `Failed(BudgetExceeded)` en `RunOrchestrator` (FR-13, AD-2):**
  - Ante `CodingResult.BudgetExhausted`, `AnalysisResult.BudgetExhausted`, `ReviewResult.BudgetExhausted` o `OperationCanceledException` con `cancelHandle.IsBudgetExhausted == true`, `RunOrchestrator` cierra la Ejecución como **`Failed(FailureReason.BudgetExceeded, detail)`** (usando `CancellationToken.None` en un contexto limpio si vino por cancelación), libera Sandbox (`SandboxReleased`) y workspace en `finally`, publica el aviso terminal `NotificationKind.RunFailed` en GitHub y **nunca** crea un PR ni la marca como `Cancelled` o `TestsFailed`.
  - Prioridad de cancelación: si `IsBudgetExhausted` está activo, cierra como `Failed(BudgetExceeded)`; si solo `IsUserCancelRequested`, cierra como `Cancelled`; si solo `stoppingToken`, preserva el comportamiento de apagado del Host.

**Never:**
- No reenviar peticiones al Proveedor upstream desde `LlmReverseProxy` cuando el presupuesto de la Ejecución ya está agotado.
- No reintentar `Coding` ni reintentos de esquema cuando el motivo es `BudgetExhausted`.
- No disparar `BudgetExceeded` para modelos sin precio conocido (`HasUnpricedModels == true`).
- No romper `OpenApiContractTests` ni `panel` (`check:api`, `test`, `build`).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Cargo alcanza o supera `budget_usd` | `EffectiveConfig.BudgetUsd = 0.01m`, cargo con modelo valorado que acumula `>= 0.01m` | `IBudgetLedger.ChargeAsync` devuelve `BudgetChargeOutcome.Exhausted`, `RemainingUsd == 0m` y activa cancelación cooperativa por presupuesto | Si `BudgetUsd == null` o `HasUnpricedModels == true`, devuelve `Recorded` |
| Petición al Gateway con presupuesto agotado | Petición válida a `/llm/v1/messages` para una Ejecución ya en `Exhausted` | Gateway responde `402 Payment Required` con `codenetix:budget_exhausted` y **0 llamadas** al Proveedor upstream | No persiste cargo adicional |
| Bandera `--max-budget-usd` en Claude Code | `BudgetUsd = 2.50m`, gastado `0.75m` → restante `1.75m` | `ClaudeCodeAgent.BuildCommand` incluye `--max-budget-usd 1.75` (`InvariantCulture`); si `BudgetUsd == null`, no añade la bandera | Si restante `<= 0`, devuelve `CodingResult.BudgetExhausted` sin lanzar el proceso |
| Corte en `Coding` por `402` / `Exhausted` | `ClaudeCodeAgent` recibe `codenetix:budget_exhausted` o el ledger agota presupuesto durante `Coding` | Traduce a `CodingResult.BudgetExhausted`; `RunOrchestrator` pasa a `Failed(BudgetExceeded)`, libera Sandbox, 0 PRs, notifica `RunFailed` | Sin reintentos adicionales de `Coding` |
| Corte en `Analyzing` o `Reviewing` | `BudgetTrackingChatClient` recibe `Exhausted` en Analista o Revisor | Ejecución pasa a `Failed(BudgetExceeded)`, libera Sandbox, 0 PRs y notifica `RunFailed` | No se enmascara como `ProviderError` ni `Cancelled` |
| Precedencia apagado Host vs presupuesto vs usuario | `stoppingToken` sin `BudgetExhausted` ni `UserCancel` | Sigue lanzando `OperationCanceledException` sin marcar `BudgetExceeded` ni `Cancelled` | Comportamiento de reinicio intacto |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/Budget.cs`, `Analysis.cs`, `Coding.cs`, `Review.cs` -- añadir `BudgetCodes.BudgetExhausted = "codenetix:budget_exhausted"`, propiedades `[JsonIgnore]` `IsExhausted`/`RemainingUsd` en `RunCostSummary`, `AnalysisResult.BudgetExhausted`, `CodingResult.BudgetExhausted` y `BudgetExhausted` en `ReviewResult`.
- `src/CodeNetix.Pipeline/Features/CancelRun/RunCancellationRegistry.cs`, `Features/Budget/BudgetLedger.cs`, `Features/RunPipeline/RunOrchestrator.cs` -- añadir `TryRequestBudgetExhausted` / `IsBudgetExhausted`, devolver `Exhausted` + disparar cancelación cooperativa en `BudgetLedger`, y cerrar como `Failed(FailureReason.BudgetExceeded)` + limpieza + `NotifyTerminalAsync` en `RunOrchestrator`.
- `src/CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs` -- comprobación previa de presupuesto agotado respondiendo `402 Payment Required` con `codenetix:budget_exhausted` sin reenviar al upstream.
- `src/CodeNetix.Agents/ChatClients/BudgetTrackingChatClient.cs`, `Features/ImplementSpec/ClaudeCodeAgent.cs`, `Features/AnalyzeIssue/Analyst.cs`, `Features/ReviewDiff/ReviewerAgent.cs` -- añadir `--max-budget-usd <restante>` y traducción de `codenetix:budget_exhausted` / `Exhausted` a `BudgetExhausted` en los tres agentes.
- `_bmad-output/implementation-artifacts/deferred-work.md` -- marcar como resuelta la entrada de 2.3 sobre `--max-budget-usd`.
- `tests/CodeNetix.Host.Tests/BudgetCutoffTests.cs` (nuevo) -- pruebas cubriendo las 6 filas de la matriz I/O.

## Tasks & Acceptance

**Execution:**
- [x] `CodeNetix.Contracts/` (`Budget.cs`, `Analysis.cs`, `Coding.cs`, `Review.cs`) y `CodeNetix.Pipeline/` (`RunCancellationRegistry.cs`, `BudgetLedger.cs`, `RunOrchestrator.cs`) -- activar `BudgetChargeOutcome.Exhausted`, cancelación cooperativa por presupuesto y cierre `Failed(BudgetExceeded)` -- cumple FR-13, AD-2 y AD-15.
- [x] `CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs` y `CodeNetix.Agents/` (`BudgetTrackingChatClient.cs`, `ClaudeCodeAgent.cs`, `Analyst.cs`, `ReviewerAgent.cs`) -- responder `402 codenetix:budget_exhausted` en Gateway, pasar `--max-budget-usd <restante>` a Claude Code (cerrando el diferido de 2.3 en `deferred-work.md`) y traducir agotamiento a `BudgetExhausted` en todos los adaptadores -- cumple FR-13 y AD-15.
- [x] `tests/CodeNetix.Host.Tests/BudgetCutoffTests.cs` -- cubrir las 6 filas de la matriz I/O.

**Acceptance Criteria:**
- Given `budget_usd` en la `EffectiveConfig`, when un cargo devuelve `Exhausted`, then el Gateway responde `402` con `codenetix:budget_exhausted`, el adaptador lo traduce a `BudgetExhausted`, el ledger solicita la cancelación cooperativa y la Ejecución pasa a `Failed(BudgetExceeded)` liberando Sandbox y sin crear PR (FR-13).
- Given `budget_usd` configurado al lanzar el Programador, when `ClaudeCodeAgent` construye el comando del CLI de Claude Code, then incluye `--max-budget-usd <restante>` como segunda barrera.

## Implementation Notes
- Activado `BudgetChargeOutcome.Exhausted` en `BudgetLedger` disparando `RunCancellationRegistry.TryRequestBudgetExhausted`; `LlmReverseProxy` responde `402 Payment Required` con `codenetix:budget_exhausted` sin reenviar al Proveedor upstream; `ClaudeCodeAgent` pasa `--max-budget-usd <restante>` (cerrando el diferido de 2.3 en `deferred-work.md`) y traduce `codenetix:budget_exhausted` / `Exhausted` a `CodingResult.BudgetExhausted`; `BudgetTrackingChatClient` + `SchemaRetryDecorator` + `Analyst` + `ReviewerAgent` propagan `BudgetExhausted` sin consumir reintentos; `RunOrchestrator` cierra como `Failed(FailureReason.BudgetExceeded)` liberando Sandbox y notificando `RunFailed`.
- Verificación: `npm --prefix panel run check:api` OK, `npm --prefix panel test` (75/75 Vitest), `npm --prefix panel run build` (0 errores), `dotnet test CodeNetix.slnx` (970/970, 0 errores, 0 omitidos, 0 warnings).

## Spec Change Log

## Review Triage Log
- **Capa 1 (`blind-hunter`), Capa 2 (`edge-case-hunter`), Capa 3 (`verification-gap`) — Iteración 1:**
  - `[PATCH][HIGH]` (`blind-hunter` #1, #4, #5, `edge-case-hunter` #1, #2, #4): En `ClaudeCodeAgent.cs`, evaluar `postCost.IsExhausted` y `ParseOutput` primero; solo traducir `codenetix:budget_exhausted` (o `"subtype": "error_max_budget_usd"`) a `CodingResult.BudgetExhausted` cuando el CLI haya fallado (`ExitCode != 0 || isError || TimedOut`) y `budgetLedger is null || BudgetUsd.HasValue` (evitando falsos positivos en salidas exitosas); formatear `--max-budget-usd` con `"0.########"` (`InvariantCulture`); usar `CancellationToken.None` en `PublishAsync` tras `ExecAsync`.
  - `[PATCH][HIGH]` (`blind-hunter` #2): Añadir bucle de reintento (`MaxAttempts = 5`, contexto limpio) ante `RunEventLog.IsUniqueViolation` en `FailBudgetExceededActiveRunAsync` y `CancelActiveRunAsync` en `RunOrchestrator.cs`.
  - `[PATCH][MEDIUM]` (`edge-case-hunter` #3, #5, `blind-hunter` #3 parcial): Proteger `_budgetLedger.GetRunCostAsync` en `LlmReverseProxy.cs` con `try/catch` fail-closed (`503 ProblemDetails` sin reenviar al upstream); usar `client.GetService<BudgetTrackingChatClient>()` en `SchemaRetryDecorator.cs`; devolver `false` en `RunCancellationRegistry.TryRequestCancel` si `slot.BudgetExhausted` ya es `true` (manteniendo la precedencia `BudgetExceeded > Cancelled` definida en la spec).
  - `[PATCH][HIGH]` (`verification-gap` #1–#7): Ampliar `BudgetCutoffTests.cs` con test de 0 reintentos de esquema en `SchemaRetryDecorator` ante JSON inválido + presupuesto agotado, aserciones de transición exacta en `Analyzing`/`Reviewing`, verificación de `BudgetExceeded` en `notification.Body` y borrado de workspace, cultura `es-ES` + falso positivo `ExitCode == 0` + agotamiento post-ejecución en `ClaudeCodeAgent`, igualdad exacta/multi-cargo/mixto en `BudgetLedger`, y `TryEnterDeciding == false` tras `TryRequestBudgetExhausted`.
