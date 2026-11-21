---
title: 'Historia 3.3: PR con traza completa'
type: 'feature'
created: '2026-11-21'
status: 'done'
route: 'dispatch'
baseline_commit: '59300c6551f686e6b7b3155ff988ff68c271bdbb'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-3-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Tras lograr aprobación unánime de los Revisores, la Ejecución todavía se detiene en un puente provisional (`"Creación de PR no disponible"`) sin crear commit, rama ni Pull Request en GitHub.

**Approach:** Implementar el guardián Git (`GitSafetyGuard`), la creación de un único commit sobre `BaseSha` y push a `codenetix/<runId>` en `RepositoryWorkspace`, el publicador idempotente `PullRequestPublisher` con tabla `gh_pull_requests` (migración en `GitHubDbContext`) y etiqueta `codenetix`, y conectar `Deciding → PullRequestCreated` en `RunOrchestrator` con el cuerpo del PR incluyendo traza completa (FR-17, AD-8).

## Boundaries & Constraints

**Always:**
- **Guardián Git único (`GitSafetyGuard`, AD-8):** Toda operación de push o merge en el módulo GitHub pasa por `GitSafetyGuard`:
  - Rechaza y registra en el log (`Warning`/`Error`) cualquier intento de push a la rama por defecto del repositorio (o cualquier rama que no tenga prefijo `codenetix/`) lanzando `GitOperationBlockedException`.
  - Rechaza y registra cualquier intento de merge (git o API) lanzando `GitOperationBlockedException`.
- **Commit único sobre `BaseSha` y push de rama `codenetix/<runId>` (AD-8):**
  - El módulo GitHub crea la rama `codenetix/<runId>` partiendo exactamente de `BaseSha`, crea **un único commit** con los cambios sincronizados del Sandbox (verificando que no haya archivos `.git` ni secretos en el commit), y hace push usando `http.extraHeader` efímero (sin escribir nunca el token en `.git/config`).
- **PR con etiqueta `codenetix` y traza completa (FR-17):**
  - Abre el PR contra la rama por defecto vía `POST /repos/{owner}/{repo}/pulls` y añade la etiqueta `codenetix` vía `POST /repos/{owner}/{repo}/issues/{number}/labels`.
  - El cuerpo del PR incluye: enlace al Evento / issue de origen, resumen y criterios de aceptación de la `Spec`, resumen de todos los Veredictos (con sus hallazgos `archivo:línea`), resumen del `TestReport` (compilación, totales, fallidos, tests nuevos) y el coste cuando exista (redactado con `ISecretRedactor`).
- **Persistencia idempotente y estado final (`gh_pull_requests`, AD-11):**
  - Tabla `gh_pull_requests` en `GitHubDbContext` (migración `AddPullRequests`) con índice UNIQUE en `run_id`, almacenando `id`, `run_id`, `number`, `url`, `branch_name`, `commit_sha`, `title`, `created_at`.
  - Si ya existe registro para `runId`, devuelve el PR existente sin duplicar push ni llamada a GitHub.
  - En `RunOrchestrator`, tras unanimidad `Approved`: transiciona `Reviewing → Deciding` (`run.StartDeciding()`), publica el PR, emite `PullRequestOpened(Number, Url, Branch, CommitSha)` y transiciona `Deciding → PullRequestCreated` (`run.CompletePullRequest()`).
  - `GET /api/runs/{id}` incluye `pullRequest` (`PullRequestInfo?`) cuando existe.

**Never:**
- No hacer push a la rama por defecto ni hacer merge automático bajo ninguna circunstancia.
- No crear más de un commit sobre `BaseSha` en la rama `codenetix/<runId>`.
- No filtrar el token de GitHub ni keys de Proveedores en mensajes de error, cuerpo del PR, `.git/config` ni base de datos.
- No violar AD-1 (Pipeline invoca `IPullRequestPublisher` vía `CodeNetix.Contracts`; solo `CodeNetix.GitHub` toca git y la API de GitHub).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Aprobación unánime → PR creado | Todos los Revisores aprueban | `Reviewing → Deciding → PullRequestCreated`; 1 único commit sobre `BaseSha`, push a `codenetix/<runId>`, PR abierto con etiqueta `codenetix` y traza completa, fila en `gh_pull_requests`, evento `PullRequestOpened` y `pullRequest` en `GET /api/runs/{id}` | N/A |
| Cuerpo del PR con traza completa | Spec + Veredictos con hallazgos + TestReport + opcional coste | Cuerpo Markdown contiene enlace al Evento, Spec, Veredictos y hallazgos, TestReport y coste (si existe), con secretos redactados | Si falta coste (`null`), omite o muestra "no disponible" sin fallar |
| Intento de push a rama por defecto | `GitSafetyGuard.EnsurePushAllowed("main", "main")` o rama sin prefijo `codenetix/` | Rechazado y registrado en log | Lanza `GitOperationBlockedException`; nunca ejecuta `git push` |
| Intento de merge | `GitSafetyGuard.EnsureMergeForbidden(...)` | Rechazado y registrado en log | Lanza `GitOperationBlockedException` |
| Idempotencia por `runId` | Segunda llamada a `PublishAsync` para el mismo `runId` | Devuelve el `PullRequestInfo` ya persistido en `gh_pull_requests` sin crear otro PR | N/A |
| Error de GitHub API al abrir PR | `POST /pulls` devuelve 422 / 500 | No guarda fila falsa en `gh_pull_requests` | Orquestador transiciona `Deciding → Failed(StageError, detalle redactado)` |
| Fallo al añadir etiqueta `codenetix` | `POST /pulls` devuelve 201 pero `POST /labels` falla con 403/500 | El PR ya creado se persiste igualmente y se registra warning en log | La Ejecución completa `PullRequestCreated` sin perder el PR |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/PullRequests.cs` (nuevo) y `Runs.cs` -- define `PullRequestRequest`, `PullRequestInfo`, `IPullRequestPublisher`, `GitOperationBlockedException`, `PullRequestPublishException` y evento `PullRequestOpened`.
- `src/CodeNetix.GitHub/GitSafetyGuard.cs` (nuevo) -- guardián único que rechaza y registra push a rama por defecto / fuera de `codenetix/*` y cualquier merge (AD-8).
- `src/CodeNetix.GitHub/RepositoryWorkspace.cs` -- añade `CommitAndPushBranchAsync` (detección de rama por defecto, validación con `GitSafetyGuard`, único commit sobre `BaseSha` en `codenetix/<runId>` y push con `http.extraHeader` efímero).
- `src/CodeNetix.GitHub/Data/GitHubDbContext.cs` y migración `AddPullRequests` -- tabla `gh_pull_requests` con `ux_gh_pull_requests_run_id`.
- `src/CodeNetix.GitHub/Features/PublishPullRequest/PullRequestPublisher.cs` (nuevo) y `GitHubServiceCollectionExtensions.cs` -- orquesta commit+push, `POST /pulls`, `POST /labels` (`codenetix`), idempotencia en `gh_pull_requests` y `FindByRunAsync`.
- `src/CodeNetix.Pipeline/Features/RunPipeline/PullRequestBodyBuilder.cs` (nuevo) y `RunOrchestrator.cs` -- construye el cuerpo trazable redactado (FR-17), transiciona `Reviewing → Deciding → PullRequestCreated` y elimina el puente `"Creación de PR no disponible"`.
- `src/CodeNetix.Host/Features/GetRun/GetRunEndpoint.cs` y `FixMode.cs` -- expone `pullRequest` en `GET /api/runs/{id}` y formatea `PullRequestOpened`.
- `tests/CodeNetix.Host.Tests/PullRequestTests.cs` (nuevo), `FakeGitHub.cs`, `LocalGitRepos.cs` y tests existentes actualizados (`PullRequestCreated` como estado final feliz).

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Contracts/PullRequests.cs` y `Runs.cs` -- definir contratos `IPullRequestPublisher`, `PullRequestRequest`, `PullRequestInfo`, excepciones y evento `PullRequestOpened` -- desacopla Pipeline y GitHub (AD-1, AD-8).
- [x] `src/CodeNetix.GitHub/GitSafetyGuard.cs`, `RepositoryWorkspace.cs`, `Data/GitHubDbContext.cs` (migración `AddPullRequests`) y `Features/PublishPullRequest/PullRequestPublisher.cs` -- implementar guardián git, commit único sobre `BaseSha`, push seguro a `codenetix/<runId>`, apertura de PR con etiqueta `codenetix` y persistencia idempotente en `gh_pull_requests` -- cumple AD-8, AD-11 y FR-17.
- [x] `src/CodeNetix.Pipeline/Features/RunPipeline/PullRequestBodyBuilder.cs`, `RunOrchestrator.cs`, `src/CodeNetix.Host/Features/GetRun/GetRunEndpoint.cs` y `FixMode.cs` -- construir cuerpo trazable redactado, transicionar `Reviewing → Deciding → PullRequestCreated`, emitir `PullRequestOpened` y exponer `pullRequest` en la API -- completa el flujo de extremo a extremo hasta PR real (AD-16).
- [x] `tests/CodeNetix.Host.Tests/PullRequestTests.cs`, `FakeGitHub.cs`, `LocalGitRepos.cs` y tests existentes -- cubrir las 7 filas de la matriz I/O (commit único sobre `BaseSha`, push real en repo local, cuerpo trazable, etiqueta `codenetix`, `gh_pull_requests`, guardián git contra push a rama por defecto y merge, idempotencia y fallo de API) y actualizar tests al nuevo estado terminal `PullRequestCreated`.

**Acceptance Criteria:**
- Given una aprobación unánime en revisión, when el orquestador entra en `Deciding`, then `CodeNetix.GitHub` crea un único commit sobre `BaseSha`, hace push de la rama `codenetix/<runId>`, abre un PR con la etiqueta `codenetix` cuyo cuerpo incluye enlace al Evento, Spec, resumen de Veredictos con hallazgos, `TestReport` y coste (cuando exista), registra el PR en `gh_pull_requests`, emite `PullRequestOpened` y la Ejecución llega a `PullRequestCreated`.
- Given cualquier intento de push a la rama por defecto o de merge, when pasa por `GitSafetyGuard`, then se rechaza con `GitOperationBlockedException` y queda registrado en el log sin ejecutar la operación.

## Implementation Notes

- **Contratos:** Creado [PullRequests.cs](../../src/CodeNetix.Contracts/PullRequests.cs) (`PullRequestBranch`, `PullRequestRequest`, `PullRequestInfo`, `IPullRequestPublisher`, `GitOperationBlockedException`, `PullRequestPublishException`) y evento `PullRequestOpened` en [Runs.cs](../../src/CodeNetix.Contracts/Runs.cs).
- **Módulo GitHub:** Creados [GitSafetyGuard.cs](../../src/CodeNetix.GitHub/GitSafetyGuard.cs) y [PullRequestPublisher.cs](../../src/CodeNetix.GitHub/Features/PublishPullRequest/PullRequestPublisher.cs), añadido `CommitAndPushBranchAsync` en [RepositoryWorkspace.cs](../../src/CodeNetix.GitHub/RepositoryWorkspace.cs) con verificación de 1 único commit sobre `BaseSha` y ausencia de credenciales, y generada la migración `AddPullRequests` (`gh_pull_requests`) en `GitHubDbContext`.
- **Pipeline y Host:** Creado [PullRequestBodyBuilder.cs](../../src/CodeNetix.Pipeline/Features/RunPipeline/PullRequestBodyBuilder.cs), conectado `Reviewing → Deciding → PullRequestCreated` en [RunOrchestrator.cs](../../src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs), expuesto `pullRequest` en [GetRunEndpoint.cs](../../src/CodeNetix.Host/Features/GetRun/GetRunEndpoint.cs) y formateado `PullRequestOpened` en [FixMode.cs](../../src/CodeNetix.Host/FixMode.cs).
- **Verificación:** Creado [PullRequestTests.cs](../../tests/CodeNetix.Host.Tests/PullRequestTests.cs) cubriendo las 7 filas de la matriz I/O; `CodeNetix.Host.Tests` pasó **827/827** y `CodeNetix.ArchitectureTests` **7/7** (0 errores, 0 omitidos).

## Spec Change Log

## Review Triage Log

| ID | Capa | Veredicto | Ruta | Evidencia / Justificación |
|---|---|---|---|---|
| BH-1 | blind-hunter | `high` | patch | `GitSafetyGuard.EnsureMergeForbidden` solo se llamaba en tests. Se refuerza en `RepositoryWorkspace.RunGitAsync` (bloqueando subcomando `merge`) y en `GitHubApiClient` (bloqueando rutas `/merge` y `/merges`). |
| BH-2 / EC-4 | blind-hunter, edge-case-hunter | `high` | patch | El escaneo de secretos staged omitía archivos binarios y podía dar falsos positivos al eliminar secretos en líneas `-`. Se cambia a `git diff --cached --no-ext-diff --text --unified=0` inspeccionando solo las líneas añadidas `+`. |
| BH-3 | blind-hunter | `medium` | patch | `CommitAndPushBranchAsync` redacta también errores de push mediante `ISecretRedactor` antes de propagar `PullRequestPublishException`. |
| BH-4 | blind-hunter | `high` | patch | Faltaba `--no-verify` en `git commit` y `git push`, permitiendo que hooks locales se ejecutaran en el host con el token en el entorno. Añadido `--no-verify` a ambos comandos. |
| BH-5 / EC-2 | blind-hunter, edge-case-hunter | `high` | patch | `DetectDefaultBranchAsync` hacía fallback silencioso a `"main"`. Ahora falla cerrado lanzando `PullRequestPublishException` si no puede determinar la rama por defecto real. |
| BH-6 / EC-1 | blind-hunter, edge-case-hunter | `medium` | patch | `CommitAndPushBranchAsync` no quitaba el prefijo `refs/heads/` tras `GitSafetyGuard`, y `IsSafeBranchName` no validaba `.lock`/`.` por segmento. Corregidos ambos. |
| BH-7 / EC-3 | blind-hunter, edge-case-hunter | `medium` | patch | En un reintento tras push exitoso y fallo en `POST /pulls`, el push de la rama exclusiva `codenetix/<runId>` fallaría por non-fast-forward. Se usa `--force-with-lease` sobre `codenetix/<runId>`. |
| BH-8 / EC-5 / EC-6 / EC-7 | blind-hunter, edge-case-hunter | `high` | patch | Una cancelación justo tras `POST /pulls` 201 o durante `TryAddLabelAsync` dejaba el PR sin registrar o la Ejecución atascada en `Deciding`. Se persiste `gh_pull_requests`, `PullRequestOpened` y `CompletePullRequest()` con `CancellationToken.None` y se captura `OperationCanceledException` en `TryAddLabelAsync` con timeout propio. |
| BH-9 | blind-hunter | `medium` | patch | `DescribeFailure` mapeaba HTTP 422 a `"GitHub no responde"`. Ahora extrae y redacta el mensaje de validación JSON de GitHub para 422. |
| BH-10 | blind-hunter | `medium` | patch | `RunOrchestrator` pasaba `spec.Problem` en vez de `issue.Title` a `BuildTitle`/`BuildCommitMessage`, y `PublishAsync` no rechazaba `RunId == Guid.Empty`. Corregidos ambos. |
| BH-11 | blind-hunter | `low` | patch | Escapados backticks en `PullRequestBodyBuilder` y usado `Uri.EscapeDataString` en el enlace de la issue. |
| BH-12 / VG-1 / VG-2 | blind-hunter, verification-gap | `medium` | patch | Añadidos tests en `PullRequestTests.cs` para rama por defecto no-`main` (`master`), `git push` sobre HTTP (`Authorization: Basic`, redacción de token y `.git/config` limpio), borrado de secreto sin falso positivo vs secreto en binario bloqueado, y bloqueo de `merge` en la frontera git/API. |
