---
title: 'Historia 3.1: Revisores independientes emiten Veredictos'
type: 'feature'
created: '2026-11-21'
status: 'done'
route: 'dispatch'
baseline_commit: 'ded2cd47760d148ad7f6d276f96941100eb357cf'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-3-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Tras superar la compilación y los tests obligatorios en el Sandbox, CodeNetix todavía termina en un puente provisional (`"Revisión no disponible"`) sin que ningún Revisor independiente audite el diff generado frente a la Spec.

**Approach:** Exportar el árbol modificado desde el Sandbox al repositorio git del host para calcular el `Diff` contra `BaseSha` (AD-8), transicionar la Ejecución a `Reviewing` creando una Etapa `Review` por Revisor y Ronda en `run_stages` (AD-11), ejecutar en paralelo los Revisores activos (`IReviewerAgent`) usando `IChatClient` + `SchemaRetryDecorator` con el esquema `schemas/verdict.v1.json` (recibiendo solo `Spec`, `Diff` y `TestReport`), persistir los resultados en `run_verdicts` y exponerlos en `GET /api/runs/{id}`.

## Boundaries & Constraints

**Always:**
- **Diff contra `BaseSha` fuera del Sandbox (AD-8):** `ISandboxSession.ExportWorkspaceAsync(Stream, CancellationToken)` extrae `/workspace` desde Docker (`GET /containers/{id}/archive`), y `IRepositoryWorkspace.ComputeDiffAsync(runId, workspaceTar, baseSha, CancellationToken)` sincroniza el árbol en el clon del host (excluyendo `.git`, `bin/`, `obj/`) y ejecuta `git diff <baseSha>` incluyendo archivos nuevos (`git add -N` en un índice temporal o equivalente), nunca contra `HEAD` ni dentro del Sandbox.
- **Aislamiento de entrada del Revisor (FR-14, AD-5):** `ReviewInput` contiene exclusivamente `RunId`, `StageId`, `Round`, `ReviewerKind`, `Spec`, `Diff` (truncado de forma segura si excede límite), `TestReport`, `Config` — nunca historial, prompts ni salida del Programador.
- **Ejecución paralela y Etapas propias (AD-11):** Al entrar en `Reviewing`, el orquestador crea una fila `RunStageRecord` (`kind = Review`, `round = 1`, `reviewer = <kind>`) en `run_stages` por cada Revisor activo de `EffectiveConfig.Reviewers` y los lanza en paralelo (`Task.WhenAll`).
- **Proveedores heterogéneos:** Cada Revisor puede usar un Proveedor/modelo distinto compatible con OpenAI (`CODENETIX__Agents__Reviewers__*` y overrides por especialidad `Security`/`Quality`/`Tests`, o `EffectiveConfig.Models.Reviewers`), reutilizando `IAgentChatClientFactory` y `SchemaRetryDecorator` (con fallback de esquema en el prompt cuando `StructuredOutput == false`).
- **Esquema `Verdict` (`schemas/verdict.v1.json`, FR-15):** Recurso embebido en `CodeNetix.Contracts`. Valida `decision` (`approved` | `changes_requested` | `rejected`), `summary` (string no vacío) y `findings` (lista de `{ severity, file, line, message }`).
- **Regla de fallo de esquema/proveedor:** Si un Revisor no produce un `Verdict` válido tras agotar `SchemaRetries` (o sufre error de proveedor), se registra el intento rechazado y el Veredicto persistido para ese Revisor cuenta como `ChangesRequested` con el detalle del error en `summary` (sin abortar a los demás Revisores en paralelo).
- **Persistencia y observabilidad:** `run_stages` y `run_verdicts` en `PipelineDbContext` (con migración EF Core `AddReviewStagesAndVerdicts`); se emite `VerdictRecorded(Round, Reviewer, Decision, FindingCount, Model)` en `RunEvent` (con `stageId` de su Etapa); `IRunReader.GetVerdictsAsync(runId)` y `GET /api/runs/{id}` devuelven `verdicts`.
- **Puente provisional hacia la Historia 3.2:** Tras persistir todos los Veredictos de la Ronda 1, la Ejecución finaliza provisionalmente en `Failed(StageError, "Rondas de revisión no disponibles")` con los Veredictos consultables por API hasta que la 3.2 implemente el bucle de Rondas y decisiones.

**Never:**
- No pasar salida de Claude Code, logs de programación ni `Feedback` anterior en `ReviewInput`.
- No ejecutar `git` dentro del contenedor Sandbox ni copiar `.git/` desde el tar del Sandbox al clon del host.
- No abortar los demás Revisores paralelos cuando uno falla por esquema inválido o error del Proveedor.
- No romper los tests existentes ni violar AD-1 (módulos de negocio solo referencian `CodeNetix.Contracts`).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Revisores activos en paralelo | `TestReport.Passed == true`, 3 Revisores (`security`, `quality`, `tests`) | Calcula `Diff` contra `BaseSha`, pasa a `Reviewing`, crea 3 filas `run_stages` (`Review`, ronda 1), ejecuta los 3 en paralelo y persiste 3 filas `run_verdicts` + 3 `VerdictRecorded` | Puente provisional: `Failed(StageError, "Rondas de revisión no disponibles")` con `verdicts` en `GET /api/runs/{id}` |
| Hallazgos estructurados válidos | Revisor devuelve JSON válido con `changes_requested` y hallazgos `{ file, line, severity, message }` | Validado contra `schemas/verdict.v1.json`; persistido en `run_verdicts` y expuesto en API | N/A |
| Respuesta inválida tras reintentos | Revisor devuelve JSON inválido en todos los intentos de `SchemaRetryDecorator` | Se reintenta `SchemaRetries` veces; al agotarse, se persiste `Verdict(ChangesRequested)` con motivo de esquema inválido | No detiene a los otros Revisores paralelos |
| Error de proveedor en un Revisor | Uno de los 3 Revisores falla con error HTTP/timeout | Ese Revisor produce `Verdict(ChangesRequested)` con motivo de proveedor; los otros 2 completan normalmente | No lanza excepción no controlada |
| Proveedores distintos por Revisor | `Security` apunta a proveedor local OpenAI-compatible (`StructuredOutput=false`) y `Quality` a Anthropic | Cada Revisor usa su propio `AgentEndpoint`/modelo y envía el esquema en el prompt si `StructuredOutput=false` | Si falta endpoint para un Revisor, usa el general de `Reviewers` o `Analyst` |
| Diff con archivo nuevo y modificado | Sandbox añade un archivo nuevo y modifica uno existente respecto a `BaseSha` | `ComputeDiffAsync` devuelve diff unificado con ambos archivos y lista de rutas cambiadas sin incluir `.git` | Error de exportación o git -> `Failed(StageError, motivo)` |
| Consulta por API | `GET /api/runs/{id}` tras revisión o antes de revisión | Devuelve `verdicts` con los veredictos persistidos ordenados por ronda/revisor (lista vacía si aún no hubo revisión) | N/A |

</frozen-after-approval>

## Code Map

- `schemas/verdict.v1.json` (nuevo) -- esquema JSON público de `Verdict` (embebido en `CodeNetix.Contracts`).
- `src/CodeNetix.Contracts/Review.cs` (nuevo), `Runs.cs`, `Sandbox.cs`, `RepositoryConfig.cs` -- define `VerdictDecision`, `ReviewFinding`, `Verdict`, `ReviewInput`, `ReviewResult`, `IReviewerAgent`, `DiffResult`, `StageKind`, `VerdictRecorded`, `ISandboxSession.ExportWorkspaceAsync`, `IRepositoryWorkspace.ComputeDiffAsync` y `IRunReader.GetVerdictsAsync`.
- `src/CodeNetix.Sandbox/Docker/DockerClient.cs` y `SandboxSession.cs` -- añade `GetArchiveAsync` (`GET /containers/{id}/archive?path=/workspace`) y `ExportWorkspaceAsync`.
- `src/CodeNetix.GitHub/Features/PrepareRepository/RepositoryWorkspace.cs` -- implementa `ComputeDiffAsync(runId, workspaceTar, baseSha, ct)` sincronizando el tar de forma segura (sin `.git`, sin escapes `..`) y ejecutando `git diff <baseSha>` con archivos nuevos incluidos.
- `src/CodeNetix.Agents/AgentsOptions.cs`, `Features/ReviewDiff/ReviewerAgent.cs` (nuevo), `ReviewerPrompt.cs` (nuevo), `VerdictValidator.cs` (nuevo), `AgentsServiceCollectionExtensions.cs` -- implementa `IReviewerAgent` con `IChatClient` + `SchemaRetryDecorator` y endpoints por especialidad.
- `src/CodeNetix.Host/Configuration/InstanceOptions.cs`, `InstanceOptionsValidator.cs`, `ServeMode.cs`, `Features/GetRun/GetRunEndpoint.cs` -- enlaza configuración de Revisores (`CODENETIX__Agents__Reviewers__*`) y expone `verdicts` en `GET /api/runs/{id}`.
- `src/CodeNetix.Pipeline/Domain/RunStageRecord.cs` (nuevo), `RunVerdictRecord.cs` (nuevo), `Data/PipelineDbContext.cs`, migración `AddReviewStagesAndVerdicts`, `Features/GetRun/RunReader.cs`, `Features/RunPipeline/RunOrchestrator.cs` -- persiste etapas y veredictos y sustituye el puente `"Revisión no disponible"` por la fase `Reviewing`.
- `tests/CodeNetix.Host.Tests/ReviewerTests.cs` (nuevo), `FakeSandbox.cs`, `CodeNetixFactory.cs` y tests existentes ajustados al nuevo puente `"Rondas de revisión no disponibles"`.

## Tasks & Acceptance

**Execution:**
- [x] `schemas/verdict.v1.json` y `src/CodeNetix.Contracts/` -- definir esquema JSON embebido y contratos `Verdict`, `ReviewInput`, `IReviewerAgent`, `DiffResult`, `VerdictRecorded`, `ExportWorkspaceAsync`, `ComputeDiffAsync` y `GetVerdictsAsync` -- establece los contratos cerrados AD-1, AD-5 y AD-8.
- [x] `src/CodeNetix.Sandbox/` y `src/CodeNetix.GitHub/` -- implementar `DockerClient.GetArchiveAsync`, `SandboxSession.ExportWorkspaceAsync` y `RepositoryWorkspace.ComputeDiffAsync` contra `BaseSha` -- permite calcular el diff fuera del Sandbox sin exponer `.git`.
- [x] `src/CodeNetix.Agents/` y `src/CodeNetix.Host/Configuration/` -- implementar `ReviewerAgent` (`IReviewerAgent`), `ReviewerPrompt`, `VerdictValidator` con `SchemaRetryDecorator` y soporte de proveedor/modelo distinto por Revisor -- cumple FR-14, FR-15 y AD-5.
- [x] `src/CodeNetix.Pipeline/` y `src/CodeNetix.Host/Features/GetRun/` -- añadir `run_stages` y `run_verdicts` con migración `AddReviewStagesAndVerdicts`, orquestar `StartReview()` + Revisores en paralelo en `RunOrchestrator`, exponer `verdicts` en `GET /api/runs/{id}` y actualizar el puente provisional a `"Rondas de revisión no disponibles"` -- cumple AD-11 y AD-16.
- [x] `tests/CodeNetix.Host.Tests/` -- crear `ReviewerTests.cs` cubriendo las 7 filas de la matriz I/O (incluyendo paralelismo, proveedores heterogéneos, fallback `ChangesRequested` tras reintentos de esquema, `ComputeDiffAsync` con archivos nuevos/modificados y API) y ajustar tests existentes.

**Acceptance Criteria:**
- Given una Ejecución con `TestReport.Passed == true`, when el orquestador exporta `/workspace` y calcula el `Diff` contra `BaseSha`, then entra en `Reviewing`, crea una Etapa `Review` en `run_stages` por cada Revisor activo y ejecuta los Revisores en paralelo entregándoles únicamente `Spec`, `Diff` y `TestReport`.
- Given la respuesta de cada Revisor validada contra `schemas/verdict.v1.json`, when se aplica, then se persiste en `run_verdicts` con `Approved` / `ChangesRequested` / `Rejected` y hallazgos con archivo y línea, se emite `VerdictRecorded`, y una respuesta inválida tras agotar reintentos cuenta como `ChangesRequested`.
- Given que todos los Revisores de la Ronda 1 han emitido su Veredicto, when el orquestador concluye la fase en la Historia 3.1, then los Veredictos son consultables en `GET /api/runs/{id}` y la Ejecución termina provisionalmente en `Failed(StageError, "Rondas de revisión no disponibles")`.

## Implementation Notes

- **Esquema y Contratos:** Creados [verdict.v1.json](../../schemas/verdict.v1.json) (embebido en `CodeNetix.Contracts`) y [Review.cs](../../src/CodeNetix.Contracts/Review.cs); ampliados `Runs.cs`, `Sandbox.cs` y `RepositoryConfig.cs`.
- **Sandbox y GitHub:** Implementados `DockerClient.GetArchiveAsync`, `SandboxSession.ExportWorkspaceAsync` y `RepositoryWorkspace.ComputeDiffAsync` contra `BaseSha` usando índice temporal (`GIT_INDEX_FILE`) y protección contra path traversal y `.git`/`bin`/`obj`.
- **Agentes y Configuración:** Implementados [ReviewerAgent.cs](../../src/CodeNetix.Agents/Features/ReviewDiff/ReviewerAgent.cs), [ReviewerPrompt.cs](../../src/CodeNetix.Agents/Features/ReviewDiff/ReviewerPrompt.cs) y [VerdictValidator.cs](../../src/CodeNetix.Agents/Features/ReviewDiff/VerdictValidator.cs) sobre `SchemaRetryDecorator`, con soporte de proveedores/modelos heterogéneos en `AgentsOptions`, `InstanceOptions`, `AgentEndpointResolver`, `InstanceOptionsValidator` y `ServeMode`.
- **Pipeline y API:** Añadidos `RunStageRecord`, `RunVerdictRecord`, migración EF Core `AddReviewStagesAndVerdicts` (`run_stages`, `run_verdicts`, `run_events.stage_id`), orquestación paralela con redacción en [RunOrchestrator.cs](../../src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs) y exposición de `verdicts` en [GetRunEndpoint.cs](../../src/CodeNetix.Host/Features/GetRun/GetRunEndpoint.cs).
- **Verificación:** Creado [ReviewerTests.cs](../../tests/CodeNetix.Host.Tests/ReviewerTests.cs) cubriendo las 7 filas de la matriz I/O; `dotnet test` pasó con **796/796 tests correctos, 0 errores, 0 omitidos**.

## Spec Change Log

## Review Triage Log

| ID | Capa | Veredicto | Ruta | Evidencia / Justificación |
|---|---|---|---|---|
| BH-1 | blind-hunter | `high` | patch | En Windows, una entrada tar como `workspace/.git./config` burlaba `IsExcludedWorkspacePath` porque no normalizaba puntos/espacios finales. Se normalizan segmentos con `TrimEnd('.', ' ')` en la exclusión y se rechazan segmentos con punto/espacio final en `IsPathTraversalOrUnsafe`. |
| BH-2 | blind-hunter | `high` | patch | `GetArchiveAsync` y `SyncWorkspaceTarAsync` no imponían límites de tamaño ni número de entradas (riesgo de tar bomb). Se fijan límites máximos de 256 MiB y 50.000 entradas lanzando `RepositoryPreparationException`/`SandboxUnavailableException`. |
| BH-3 | blind-hunter | `low` | defer | El retorno temprano con tar vacío solo acomoda el doble de prueba `FakeSandboxSession` sin handler; en Docker real `/workspace` siempre incluye la carpeta raíz. |
| BH-4 / EC-3 | blind-hunter, edge-case-hunter | `medium` | patch | Archivos legítimamente versionados bajo `bin/` o `obj/` se borraban y excluían al extraer, produciendo eliminaciones fantasma en el diff. Se conservan las rutas versionadas en `BaseSha` al filtrar exclusiones `bin`/`obj`. |
| BH-5 | blind-hunter | `medium` | patch | `AgentEndpointResolver.ResolveReviewers` dejaba que el modelo por defecto de Anthropic/OpenAI sombrease `Analyst.Model` cuando `Reviewers` no definía proveedor ni modelo. Si `Reviewers` no define ninguno, hereda directamente el `AgentEndpoint` de `Analyst`. |
| BH-6 | blind-hunter | `low` | patch | Se captura `CompletedAt` individual por cada tarea paralela de Revisor para que la duración de cada `RunStageRecord` sea exacta. |
| BH-7 | blind-hunter | `medium` | patch | Se deduplica `activeReviewers` antes de insertar `run_stages`/`run_verdicts`. La reanudación tras caída del host en `Reviewing` queda diferida a la Historia 5.3. |
| BH-8 / EC-4 | blind-hunter, edge-case-hunter | `medium` | patch | `ReviewerAgent` hardcodeaba `Attempts: 1` en `ProviderFailed` aunque hubiera reintentos previos de esquema, y omitía logs `Warning` en fallbacks. Se contabiliza `rejectedAttempts + 1` y se registra warning. |
| BH-9 | blind-hunter | `medium` | patch | `RedactVerdict` no redactaba `Finding.File` y `tarPath` era predecible con `FileMode.Create`. Se redacta `Finding.File` y se usa `Guid.NewGuid():N` + `FileMode.CreateNew`. |
| BH-10 | blind-hunter | `low` | defer | Nombre de índice auto-generado `IX_run_verdicts_stage_id` es cosmético en SQLite y la migración ya está aplicada y verificada. |
| BH-11 | blind-hunter | `medium` | patch | En `ReviewerTests.cs` `cmd.CommandText` se sobrescribía omitiendo el filtro `WHERE run_id = $id`. Corregida la consulta SQL en el test. |
| BH-12 / VG-1..5 | blind-hunter, verification-gap | `medium` | patch | Se añaden tests en `ReviewerTests.cs` para: validación `Diagnostic` en `/api/health` de `Agents:Reviewers:*` inválidos y override de solo modelo; fallo de exportación/diff en `RunOrchestrator` -> `Failed(StageError, DiffFailedPrefix...)`; validación semántica `VerdictRules` (`../escape.cs`); redacción de secretos en `run_verdicts`/API; y `codenetix.yml` con subconjunto de `reviewers` y `models.reviewers`. |
| BH-13 | blind-hunter | `low` | patch | Actualizado comentario XML obsoleto en `RunOrchestrator.cs` (`"Revisión no disponible"` -> `"Rondas de revisión no disponibles"`). |
| EC-1 | edge-case-hunter | `high` | patch | Una entrada tar `GlobalExtendedAttributes` antes de `workspace/` fijaba `stripWorkspacePrefix = false` corrompiendo la extracción y el diff. Se omiten entradas que no sean `Directory`/`RegularFile`/`V7RegularFile` antes de evaluar `stripWorkspacePrefix`. |
| EC-2 | edge-case-hunter | `medium` | patch | Excepciones `UnauthorizedAccessException`, `ArgumentException` y `NotSupportedException` al extraer el tar se envuelven ahora en `RepositoryPreparationException`. |
