---
title: 'Historia 4.4: Cancelar una Ejecución'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: '9e1797bf0a0d58c6564f7ba939b28082226bbdb5'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-4-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Si una Ejecución se desvía o el desarrollador decide abortarla antes de abrir un PR, no existe ni el contrato/endpoint de cancelación ni el botón «Cancelar» en el Panel para detener de forma segura los agentes y el Sandbox sin crear un PR.

**Approach:** Definir `IRunControl.RequestCancelAsync` en `CodeNetix.Contracts`, implementarlo en `CodeNetix.Pipeline` coordinado con `RunOrchestrator` mediante un registro de cancelación por Ejecución (con exclusión atómica al entrar en `Deciding` para evitar carreras con la creación de PR), exponer `POST /api/runs/{id}/cancel` en `CodeNetix.Host` (202 Accepted si es cancelable, 409 Conflict en `Deciding` o estados terminales, 404 Not Found si no existe), actualizar el OpenAPI y `panel/src/api/generated.ts`, y añadir el botón «Cancelar» en el Panel (habilitado en `Received`/`Analyzing`/`Coding`/`Reviewing` y deshabilitado en `Deciding`/terminales).

## Boundaries & Constraints

**Always:**
- **Contrato y aislamiento modular (AD-1, AD-2):**
  - `CodeNetix.Contracts/Runs.cs` define `CancelRunOutcome` (`Accepted`, `NotFound`, `Conflict`), `CancelRunResult` e `IRunControl.RequestCancelAsync(string id, CancellationToken cancellationToken)`.
  - `CodeNetix.Pipeline` implementa `IRunControl` y `RunCancellationRegistry` (singleton interno). `PipelineRun.Cancel()` sigue siendo el único dueño de la transición a `Cancelled` (permitida solo desde `Received`, `Analyzing`, `Coding`, `Reviewing`).
- **Cancelación segura en `RunOrchestrator` (FR-23, AD-2, AD-16):**
  - `RunOrchestrator.RunAsync` enlaza un `CancellationTokenSource` por Ejecución con el `stoppingToken` del Host y lo pasa a todas las fases cancelables (`PrepareRepositoryAsync`, `CreateSandboxAsync`, `AnalyzeAsync`, `CodeAsync`, `ReviewAsync`).
  - Antes de invocar `run.StartDeciding()`, el orquestador verifica atómicamente en `RunCancellationRegistry` si existe cancelación solicitada: si ya se pidió, aborta antes de entrar en `Deciding` (sin crear PR); si no, marca la Ejecución como `InDeciding` para que cualquier cancelación posterior reciba `409 Conflict` y `DecideAndPublishAsync` no sea interrumpida por cancelación de usuario.
  - Al cancelarse una Ejecución activa por petición del usuario (sin ser apagado del Host): el orquestador aplica `run.Cancel()`, persiste y publica `StateChanged(→ Cancelled)` con `CancellationToken.None`, libera el Sandbox en el `finally` (en ≤ 10 s, emitiendo `SandboxReleased`), borra el workspace y llama a `NotifyTerminalAsync(run)` (`NotificationKind.RunCancelled`), retornando sin lanzar excepción al `Dispatcher`.
  - Si la Ejecución está en estado cancelable en BD (`Received`/`Analyzing`/`Coding`/`Reviewing`) pero no está activa en memoria en `RunOrchestrator`, `IRunControl.RequestCancelAsync` aplica `run.Cancel()` directamente en BD, publica `StateChanged(→ Cancelled)` y envía el aviso `RunCancelled`.
- **Endpoint `POST /api/runs/{id}/cancel` (`CodeNetix.Host`):**
  - Protegido por `AdminTokenMiddleware` (sin token → `401 Unauthorized`).
  - `id` admite GUID o `shortId` de 8 caracteres.
  - Devuelve `202 Accepted` con `CancelRunResponse(Guid RunId, string ShortId, RunState State, bool CancellationRequested)` cuando se acepta la cancelación; `409 Conflict` (`ProblemDetails`) en `Deciding`, `PullRequestCreated`, `Failed` o `Cancelled`; `404 Not Found` (`ProblemDetails`) si no existe.
  - Registrado en OpenAPI (`/openapi/v1.json`), actualizado `panel/openapi/v1.json` y regenerado `panel/src/api/generated.ts` (AD-14).
- **Botón «Cancelar» en el Panel (`panel/`):**
  - Añadir `cancelRun(token, runId)` en `panel/src/api/client.ts` y `isRunCancellable(state: RunState)` (true solo para `Received`, `Analyzing`, `Coding`, `Reviewing`).
  - Mostrar el botón «Cancelar» en la cabecera de la Ejecución en **En vivo** (`App.tsx`) y en el detalle de **Historial** (`RunHistoryView.tsx`), deshabilitado en `Deciding` y estados terminales (`PullRequestCreated`, `Failed`, `Cancelled`), con estado de envío (`Cancelando…`), manejo de `401` → `onLogout` y mensajes claros ante `409`/error.
  - Compilar `panel/` hacia `src/CodeNetix.Host/wwwroot/`.

**Never:**
- No cancelar una Ejecución que ya entró en `Deciding` ni abrir un PR tras aceptar la cancelación.
- No confundir apagado del Host (`stoppingToken`) con cancelación de usuario (un apagado del Host NO debe marcar `Cancelled` una Ejecución que se puede reanudar).
- No dejar el Sandbox o el directorio de trabajo sin liberar al cancelar.
- No desincronizar `panel/openapi/v1.json` ni `panel/src/api/generated.ts` respecto a `/openapi/v1.json`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Cancelar Ejecución activa en `Coding` con Sandbox en marcha | `POST /api/runs/{id}/cancel` mientras `ICodingAgent` está ejecutándose | `202 Accepted`; agente interrumpido, Sandbox liberado en ≤ 10 s (`SandboxReleased`), estado pasa a `Cancelled` en BD y `/hubs/runs`, ningún PR creado y aviso `RunCancelled` publicado | No lanza error al `Dispatcher` ni marca `Failed(StageError)` |
| Cancelar Ejecución en `Received` (pendiente en BD) | `POST /api/runs/{shortId}/cancel` antes de orquestar | `202 Accepted`; transición inmediata a `Cancelled` + aviso `RunCancelled`; si el orquestador la toma luego la omite por terminal | Idempotente frente a `Dispatcher` |
| Cancelar en `Deciding` | `POST /api/runs/{id}/cancel` con Ejecución en `Deciding` | `409 Conflict` (`ProblemDetails`); la publicación del PR continúa sin interrupción | Botón «Cancelar» deshabilitado en el Panel |
| Cancelar en estados terminales (`PullRequestCreated`, `Failed`, `Cancelled`) | `POST /api/runs/{id}/cancel` | `409 Conflict` (`ProblemDetails`) | Sin cambios en BD ni eventos extra |
| Id inexistente o sin token | `POST /api/runs/no-existe/cancel` o sin `Authorization: Bearer` | `404 Not Found` (`ProblemDetails`) o `401 Unauthorized` | Sin fugas de información |
| Botón «Cancelar» en el Panel | Ejecución en `Coding` vs `Deciding` vs `PullRequestCreated` | Habilitado en `Received`/`Analyzing`/`Coding`/`Reviewing`; deshabilitado en `Deciding`/terminales; `401` cierra sesión | Muestra feedback de error si la API responde `409` o falla la red |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/Runs.cs` -- añadir `CancelRunOutcome`, `CancelRunResult` e `IRunControl`.
- `src/CodeNetix.Pipeline/Features/CancelRun/RunCancellationRegistry.cs` (nuevo) y `RunControl.cs` (nuevo) -- registro concurrente de cancelaciones activas con barrera atómica para `Deciding`, e implementación de `IRunControl`.
- `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` y `PipelineServiceCollectionExtensions.cs` -- enlazar token de cancelación por Ejecución, barrera `TryEnterDeciding`, captura de cancelación de usuario → `run.Cancel()` + `ReleaseSandboxAsync` + `NotifyTerminalAsync(RunCancelled)`, y registrar `RunCancellationRegistry` + `IRunControl`.
- `src/CodeNetix.Host/Features/CancelRun/CancelRunEndpoint.cs` (nuevo) y `ServeMode.cs` -- endpoint `POST /api/runs/{id}/cancel` y mapeo en el Host.
- `panel/openapi/v1.json`, `panel/src/api/generated.ts`, `panel/src/api/client.ts`, `panel/src/App.tsx`, `panel/src/components/RunHistoryView.tsx`, `panel/src/styles.css` -- regenerar OpenAPI/TS, añadir `cancelRun` + `isRunCancellable`, botón «Cancelar» en En vivo e Historial y rebuild a `src/CodeNetix.Host/wwwroot/`.
- `tests/CodeNetix.Host.Tests/CancelRunTests.cs` (nuevo) y `panel/src/__tests__/cancel.test.ts` (nuevo) -- pruebas de integración (cancelación en `Coding` con Sandbox ≤ 10 s sin PR + aviso `RunCancelled`, `Received`, `Deciding` → 409, terminales → 409, 404, 401) y pruebas Vitest del cliente y reglas de habilitación del botón.

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Contracts/Runs.cs`, `src/CodeNetix.Pipeline/Features/CancelRun/` y `RunOrchestrator.cs` -- definir `IRunControl`, implementar `RunCancellationRegistry` + `RunControl` y cablear cancelación cooperativa + barrera `Deciding` + limpieza de Sandbox y aviso `RunCancelled` en `RunOrchestrator` -- cumple FR-23, AD-1 y AD-2.
- [x] `src/CodeNetix.Host/Features/CancelRun/CancelRunEndpoint.cs`, `ServeMode.cs`, `panel/openapi/v1.json` y `panel/src/api/generated.ts` -- exponer `POST /api/runs/{id}/cancel` (202/404/409) y regenerar el contrato OpenAPI/TypeScript -- cumple AD-14 y AD-17.
- [x] `panel/src/api/client.ts`, `panel/src/App.tsx`, `panel/src/components/RunHistoryView.tsx`, `panel/src/styles.css` y `src/CodeNetix.Host/wwwroot/` -- añadir botón «Cancelar» habilitado en estados cancelables y deshabilitado en `Deciding`/terminales, con feedback claro y compilación a `wwwroot/` -- cumple FR-23.
- [x] `tests/CodeNetix.Host.Tests/CancelRunTests.cs` y `panel/src/__tests__/cancel.test.ts` -- cubrir las 6 filas de la matriz I/O.

**Acceptance Criteria:**
- Given una Ejecución en `Received`, `Analyzing`, `Coding` o `Reviewing`, when el usuario pulsa «Cancelar» o invoca `POST /api/runs/{id}/cancel`, then `IRunControl.RequestCancelAsync` registra la cancelación, el orquestador aplica `Cancelled` con aviso `RunCancelled` en la issue, el Sandbox se detiene en ≤ 10 s y no se crea ningún PR (FR-23, AD-2).
- Given una Ejecución en `Deciding` o en un estado terminal (`PullRequestCreated`, `Failed`, `Cancelled`), when se solicita la cancelación, then la API responde `409 Conflict` y el botón «Cancelar» aparece deshabilitado en el Panel.

## Implementation Notes
- Implementados `IRunControl`, `RunCancellationRegistry` (con barrera atómica `TryEnterDeciding`) y `RunControl`, integrados con `RunOrchestrator` para cancelar etapas activas, liberar el Sandbox, borrar el workspace y publicar `NotificationKind.RunCancelled` sin afectar apagados del Host (`stoppingToken`).
- Añadido `POST /api/runs/{id}/cancel` (`CancelRunEndpoint.cs`), regenerados `panel/openapi/v1.json` y `panel/src/api/generated.ts` (39 esquemas), y añadido el botón «Cancelar» en **En vivo** y **Historial** con reglas de habilitación/deshabilitación.
- Verificación: `npm --prefix panel run check:api` OK, `npm --prefix panel test` (46/46 Vitest), `npm --prefix panel run build` (0 errores, `wwwroot/` actualizado), `dotnet test CodeNetix.slnx` (934/934, 0 errores, 0 omitidos, 0 warnings).

## Spec Change Log

## Review Triage Log
- **Capa 1 (`blind-hunter`), Capa 2 (`edge-case-hunter`), Capa 3 (`verification-gap`) — Iteración 1:**
  - `[PATCH][CRITICAL]` (`blind-hunter` #1, #2, `edge-case-hunter` #1): Eliminar la carrera TOCTOU entre `RunControl` (rama inactiva en BD) y `RunOrchestrator.BeginRun` así como doble `POST /cancel` concurrente: serializar por `runId` en `RunCancellationRegistry` (reserva de cancelación en BD que `BeginRun` espera o detecta, y serializa peticiones concurrentes) + capturar `DbUpdateConcurrencyException`/`DbUpdateException` devolviendo `409 Conflict`.
  - `[PATCH][HIGH]` (`blind-hunter` #3, #4, `edge-case-hunter` #6): Desregistrar el slot cancelable en `RunCancellationRegistry` al salir del flujo cancelable (antes de `finally`/`NotifyTerminalAsync`), devolver `false` en `catch (ObjectDisposedException)` de `TryRequestCancel` para reevaluar BD, y proteger `ctsToCancel.Cancel(false)` frente a `AggregateException`.
  - `[PATCH][HIGH]` (`edge-case-hunter` #2, #3, #5, `blind-hunter` #5, #7): Honrar `IsUserCancelRequested(runId)` aunque `stoppingToken` se dispare simultáneamente; proteger `sandbox.DisposeAsync()` y `workspace.DeleteAsync()` con `try/catch` en `finally` para no omitir `NotifyTerminalAsync(RunCancelled)`; añadir `ThrowIfCancellationRequested()` en `RunReviewerSafeAsync`/`ReviewAsync`/`CodeAsync` tras `testRunner.RunAsync`; usar `CancellationToken.None` en `SaveAndPublishAsync` de `RunControl`.
  - `[PATCH][MEDIUM]` (`blind-hunter` #6, `edge-case-hunter` #4): Sustituir `cancellingRunId` escalar por `ReadonlySet<string>` en `App.tsx` y `RunHistoryView.tsx`, comprobar `selectedRunIdRef.current === runId` antes de actualizar error/`catchUpEvents`/`detailReloadTick`, y evitar revertir un estado ya terminal en `runs` con el `result.state` del `202 Accepted`.
  - `[PATCH][HIGH]` (`verification-gap` #1–#6): Añadir tests unitarios de `RunCancellationRegistry` (exclusión mutua `TryEnterDeciding` vs `TryRequestCancel`), tests en `CancelRunTests.cs` (cancelación en `Reviewing` abortando antes de `Deciding`, `stoppingToken` sin `RequestCancelAsync` no marca `Cancelled`, idempotencia de `orchestrator.RunAsync` tras cancelar `Received`, sin mutaciones tras `409` en terminales), y ampliar `panel/src/__tests__/cancel.test.ts` (`Received`/`Analyzing`/`Reviewing`, `cancelling`, `cancelError`).
