---
title: 'Historia 3.5: Aviso en la issue al terminar'
type: 'feature'
created: '2026-11-21'
status: 'done'
route: 'dispatch'
baseline_commit: '923444e42b6871edaf049c05200f917053dd7308'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-3-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Actualmente CodeNetix solo publica un comentario en la issue de GitHub cuando el Analista rechaza tempranamente una issue (`NotAutomatable`, Historia 1.9); cuando una Ejecución termina en `PullRequestCreated`, en otro `Failed` (`TestsFailed`, `ReviewRejected`, `ReviewRoundsExhausted`, `CloneFailed`, `InvalidRepoConfig`, `StageError`, etc.) o en `Cancelled`, el desarrollador no recibe ningún aviso en la issue de origen.

**Approach:** Extender `NotificationKind` con los tipos de cierre (`PullRequestCreated`, `RunFailed`, `RunCancelled`), crear un generador de comentarios terminales (`TerminalNotificationComment`), e integrar el paso de notificación final (AD-16 paso 7) en `RunOrchestrator` reutilizando `IIssueNotifier` (idempotente por `(runId, kind)` en `gh_notifications`, sin nuevas migraciones) para toda Ejecución originada en GitHub que alcance un estado terminal.

## Boundaries & Constraints

**Always:**
- **Cobertura de todos los estados terminales con origen en GitHub (FR-19, AD-8, AD-16):**
  - Solo se notifica cuando `GitHubObjectKey.TryParse(run.ObjectKey, out owner, out repo, out number)` tiene éxito; si el origen no es una issue de GitHub, se omite limpiamente.
  - `PullRequestCreated` → publica un comentario con `NotificationKind.PullRequestCreated` incluyendo el enlace al PR (`pr.Url` y `#number`), el `ShortId` de la Ejecución y la clasificación de riesgo si aplica.
  - `Failed` con `FailureReason.NotAutomatable` → mantiene exactamente el flujo y `NotificationKind.NotAutomatable` de la Historia 1.9 (sin publicar un segundo comentario `RunFailed`).
  - `Failed` con cualquier otro `FailureReason` (`InvalidRepoConfig`, `CloneFailed`, `SchemaInvalid`, `TestsFailed`, `ReviewRejected`, `ReviewRoundsExhausted`, `BudgetExceeded`, `Timeout`, `HostRestarted`, `StageError`) → publica un comentario con `NotificationKind.RunFailed` incluyendo el `FailureReason`, el detalle redactado (si existe) y el `ShortId`.
  - `Cancelled` → publica un comentario con `NotificationKind.RunCancelled` indicando que la Ejecución fue cancelada y su `ShortId` (usando un `CancellationTokenSource` acotado independiente de 15 s para poder enviar el aviso incluso cuando el token de la Ejecución fue cancelado).
- **Idempotencia y efectos seguros (AD-8):**
  - Todo aviso pasa por `IIssueNotifier.NotifyAsync(run.Id, owner, repo, number, kind, body, ct)` respaldado por `gh_notifications` (`ux_gh_notifications_run_id_kind`).
  - Se ejecuta **siempre después** de confirmar en BD la transición al Estado terminal.
  - Emite `IssueCommented(kind, commentUrl)` o `NotificationFailed(kind, error)` mediante `IRunEventSink`; un error al notificar **nunca** altera el Estado terminal ni relanza excepción.
  - Todo contenido redactado con `ISecretRedactor` (y saneado por `IssueCommentSanitizer` en `IssueNotifier`).
- **Sin nuevas migraciones EF Core:** `gh_notifications.kind` ya se persiste como texto (`HasConversion<string>()`).

**Never:**
- No publicar dos comentarios para una misma Ejecución `Failed(NotAutomatable)` (solo `NotAutomatable`, nunca también `RunFailed`).
- No alterar el Estado terminal de la Ejecución si GitHub rechaza el comentario (403, 500, timeout).
- No filtrar secretos ni tokens de GitHub en el comentario ni en `NotificationFailed`.
- No violar AD-1 (Pipeline invoca `IIssueNotifier` vía `CodeNetix.Contracts`).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Cierre en `PullRequestCreated` | Ejecución completa que abre PR `#12` | Publica 1 comentario en la issue (`NotificationKind.PullRequestCreated`) con el enlace al PR, registra `gh_notifications(Posted)` y emite `IssueCommented` | Si falla GitHub → `NotificationFailed`; estado sigue `PullRequestCreated` |
| Cierre en `Failed` (no `NotAutomatable`) | Ejecución falla con `TestsFailed`, `ReviewRejected`, `ReviewRoundsExhausted` o `InvalidRepoConfig` | Publica 1 comentario (`NotificationKind.RunFailed`) con el `FailureReason` y detalle redactado, registra `gh_notifications(Posted)` y emite `IssueCommented` | Si falla GitHub → `NotificationFailed`; estado sigue `Failed` |
| Cierre en `Failed(NotAutomatable)` | Rechazo temprano del Analista (Historia 1.9) | Publica únicamente 1 comentario (`NotificationKind.NotAutomatable`), nunca un segundo `RunFailed` | Igual que 1.9 |
| Cierre en `Cancelled` | Ejecución cancelada (`RunState.Cancelled`) | Publica 1 comentario (`NotificationKind.RunCancelled`) usando token acotado independiente y emite `IssueCommented` | Si falla → `NotificationFailed`; estado sigue `Cancelled` |
| Idempotencia por `(runId, kind)` | Llamada repetida a `IIssueNotifier.NotifyAsync` para `(runId, PullRequestCreated)` o `(runId, RunFailed)` | Devuelve el comentario ya registrado en `gh_notifications` sin duplicar en GitHub | N/A |
| Origen no GitHub | `run.ObjectKey` no tiene formato `github:<owner>/<repo>#<n>` | No invoca `IIssueNotifier` ni emite `IssueCommented`/`NotificationFailed` | N/A |
| Error de GitHub al comentar | `POST /issues/{n}/comments` devuelve 500/403 | Persiste `gh_notifications(Failed)` y emite `NotificationFailed(kind, error)` redactado | Estado terminal de la Ejecución intacto |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/Notifications.cs` -- añadir `PullRequestCreated`, `RunFailed` y `RunCancelled` al enum `NotificationKind`.
- `src/CodeNetix.Pipeline/Features/RunPipeline/TerminalNotificationComment.cs` (nuevo) -- construye el Markdown redactado para `PullRequestCreated` (enlace al PR + riesgo), `RunFailed` (`FailureReason` + detalle) y `RunCancelled`.
- `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` -- invocar la notificación terminal tras confirmar `PullRequestCreated`, cualquier `Failed` distinto de `NotAutomatable`, o `Cancelled` (paso 7 de AD-16), reutilizando `IIssueNotifier` y emitiendo `IssueCommented`/`NotificationFailed` sin alterar el estado terminal ni duplicar en `NotAutomatable`.
- `tests/CodeNetix.Host.Tests/TerminalNotificationTests.cs` (nuevo) y actualización de secuencias de eventos en tests existentes (`AnalystTests.cs`, `FixFollowTests.cs`, `RepositoryPreparationTests.cs`, `RunExecutionTests.cs`, `PullRequestTests.cs`, etc. donde las ejecuciones de GitHub ahora emiten `IssueCommented` al terminar).

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Contracts/Notifications.cs` -- añadir `PullRequestCreated`, `RunFailed` y `RunCancelled` a `NotificationKind` -- soporta la clave de idempotencia `(runId, kind)` para todos los estados terminales (AD-8, FR-19).
- [x] `src/CodeNetix.Pipeline/Features/RunPipeline/TerminalNotificationComment.cs` y `RunOrchestrator.cs` -- construir comentarios terminales redactados y ejecutar el paso de notificación (AD-16 paso 7) tras confirmar `PullRequestCreated`, `Failed` (no `NotAutomatable`) o `Cancelled`, emitiendo `IssueCommented`/`NotificationFailed` sin alterar el estado terminal -- cumple FR-19 y AD-8.
- [x] `tests/CodeNetix.Host.Tests/TerminalNotificationTests.cs` y ajustes de secuencias en tests existentes -- cubrir las 7 filas de la matriz I/O (PR creado, fallos `TestsFailed`/`ReviewRejected`/`ReviewRoundsExhausted`, no duplicado en `NotAutomatable`, cancelación, idempotencia en `gh_notifications`, origen no GitHub y error 500 de GitHub).

**Acceptance Criteria:**
- Given una Ejecución con origen en GitHub que llega a `PullRequestCreated`, `Failed` o `Cancelled`, when el orquestador ejecuta el paso de notificación (AD-16), then publica exactamente un comentario en la issue con el enlace al PR o el motivo (`FailureReason`) redactado, registra el resultado en `gh_notifications` de forma idempotente por `(runId, kind)` y emite `IssueCommented` o `NotificationFailed` sin alterar el Estado terminal.
- Given una Ejecución `Failed(NotAutomatable)`, when concluye, then mantiene exactamente su único comentario `NotAutomatable` sin publicar un segundo comentario `RunFailed`.

## Implementation Notes

- **Contratos:** Ampliado `NotificationKind` en [Notifications.cs](../../src/CodeNetix.Contracts/Notifications.cs) con `PullRequestCreated`, `RunFailed` y `RunCancelled` (sin migraciones nuevas).
- **Saneado seguro de enlaces a PR:** Añadido `IssueCommentSanitizer.SanitizeAllowingPullRequestLinks` en [IssueCommentSanitizer.cs](../../src/CodeNetix.GitHub/Features/NotifyIssue/IssueCommentSanitizer.cs) y conectado en [IssueNotifier.cs](../../src/CodeNetix.GitHub/Features/NotifyIssue/IssueNotifier.cs) solo para `NotificationKind.PullRequestCreated`.
- **Comentarios terminales y Orquestador:** Creado [TerminalNotificationComment.cs](../../src/CodeNetix.Pipeline/Features/RunPipeline/TerminalNotificationComment.cs) e integrado `NotifyTerminalAsync` en [RunOrchestrator.cs](../../src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs) tras `PullRequestCreated`, cualquier `Failed` distinto de `NotAutomatable` y `Cancelled` (con timeout independiente de 15 s).
- **Verificación:** Creado [TerminalNotificationTests.cs](../../tests/CodeNetix.Host.Tests/TerminalNotificationTests.cs); `CodeNetix.Host.Tests` pasó **902/902** y `CodeNetix.ArchitectureTests` **7/7** (0 errores, 0 omitidas).

## Spec Change Log

## Review Triage Log

| ID | Capa | Veredicto | Ruta | Evidencia / Justificación |
|---|---|---|---|---|
| BH-1 / EC-5 / VG-1 | blind-hunter, edge-case-hunter, verification-gap | `high` | patch | `StuckRunRecovery.FailIfActiveAsync` cerraba ejecuciones en `Failed(StageError)` sin publicar el aviso `RunFailed`. Se conecta `IIssueNotifier` + `IRunEventSink` en `StuckRunRecovery` tras confirmar el fallo. |
| BH-2 / EC-6 | blind-hunter, edge-case-hunter | `low` | defer | El disparador HTTP/CLI de cancelación pertenece a la Historia 4.4 (`4-4-cancelar-una-ejecucion`), que invocará `NotifyTerminalAsync` tras confirmar `run.Cancel()`. |
| BH-3 / EC-2 / VG-O-1 | blind-hunter, edge-case-hunter, verification-gap | `high` | patch | `SanitizeAllowingPullRequestLinks` aceptaba cualquier host (`https?://[^/\s<>()]+/...`). Se restringe el patrón exclusivamente a `https://github.com/{owner}/{repo}/pull/{n}` (sin `http://` ni userinfo `@`). |
| BH-4 / EC-1 | blind-hunter, edge-case-hunter | `high` | patch | Truncar en `Sanitize(substituted, maxLength)` antes de restaurar placeholders podía cortar `CODENETIXPRLINK...` y filtrarlo en el comentario. Ahora se sanea con `int.MaxValue`, se restauran los enlaces y se trunca al final con `TextLimits.Truncate(..., maxLength)`. |
| BH-5 / EC-3 | blind-hunter, edge-case-hunter | `high` | patch | `SendNotificationAsync` usaba token enlazado en `RunFailed`/`NotAutomatable`, pudiendo relanzar `OperationCanceledException` tras confirmar el estado terminal. Ahora usa siempre timeout independiente de 15 s y `CancellationToken.None` en `events.AppendAsync`. |
| BH-6 | blind-hunter | `low` | defer | La reconciliación de filas `gh_notifications` en `Pending` tras timeout es alcance de la Historia 5.3 (ya registrada en `deferred-work.md`). |
| BH-7 | blind-hunter | `medium` | patch | Se redacta `result.Error` en `SendNotificationAsync` y el cuerpo de `NotAutomatableComment.Build` con `ISecretRedactor`. |
| BH-8 / EC-4 | blind-hunter, edge-case-hunter | `low` | patch | `BuildPullRequestCreated` añade `(+N más)` si hay más de 10 rutas protegidas, y `NotifyTerminalAsync` valida `ArgumentNullException` si `PullRequestCreated` recibe `pullRequest == null`. |
| BH-9 / VG-2 | blind-hunter, verification-gap | `medium` | patch | Añadidos tests en `TerminalNotificationTests.cs` y `RunExecutionTests.cs` para `StuckRunRecovery` publicando `RunFailed`, `OperationCanceledException` / excepción con secreto en `SendNotificationAsync`, host suplantado `https://evil.example/acme/shop/pull/12` y truncado cerca de `maxLength` en `SanitizeAllowingPullRequestLinks`. |
| BH-10 | blind-hunter | `false` | - | Los checkboxes e `Implementation Notes` ya se habían actualizado antes de la revisión. |
