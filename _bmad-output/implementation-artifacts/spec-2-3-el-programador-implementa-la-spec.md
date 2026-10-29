---
title: 'Historia 2.3: El Programador implementa la Spec'
type: 'feature'
created: '2026-10-29'
status: 'done'
route: 'dispatch'
baseline_commit: '7d6f6c634d7a9ad0735cc838e3649eeef092551f'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-2-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Tras generar la `Spec` en la historia 1.8, el orquestador detiene la Ejecución en un puente provisional (`"Programación no disponible"`). Falta el agente Programador (`ClaudeCodeAgent` / `ICodingAgent`) que ejecute Claude Code dentro del Sandbox aislado para implementar la `Spec`, emita su salida en vivo como `RunEvent` y contabilice los intentos fallidos en `PipelineRun` (FR-11, AD-2, AD-3, AD-5, AD-6, AD-16).

**Approach:**
- Implementar `ClaudeCodeAgent` (`ICodingAgent`) en `CodeNetix.Agents`, que construye el prompt con la `Spec` y la issue delimitadas como `<untrusted-data>` (`UntrustedText`), asegura la provisión inicial de `@anthropic-ai/claude-code@2.1.311` en el volumen `codenetix-tools` a través del Egress Gateway, ejecuta `claude --bare -p <prompt> --output-format json --permission-mode acceptEdits --allowedTools "Bash,Read,Edit,Write" --permission-prompts none` dentro del Sandbox y emite la salida mediante `IRunEventSink` como `OutputAppended` (con límite de 5 MiB por Etapa y `OutputTruncated`).
- Eliminar el puente provisional de la historia 1.8 en `RunOrchestrator`: tras persistir la `Spec`, la Ejecución pasa a `Coding` (`run.StartCoding()`), ejecuta el Programador y, si el CLI termina con error o sin cambios en `/workspace`, registra un intento fallido en `PipelineRun` (reintentando hasta `EffectiveConfig.MaxCodingAttempts` con feedback antes de fallar con `TestsFailed`).

## Boundaries & Constraints

**Always:**
- **Contracts (`src/CodeNetix.Contracts/`):**
  - Nuevos `RunEvent` en la unión discriminada (`Runs.cs`):
    - `OutputAppended(string Text, string Stream = "stdout")`
    - `OutputTruncated(int MaxBytes = 5 * 1024 * 1024)`
    - `CodingAttemptFailed(int Attempt, string Reason)`
  - `ICodingAgent.RunAsync(CodingInput, CancellationToken)` → `CodingResult` (`Completed(string Summary, string Model)` o `Failed(string Reason)`).
  - `ISandboxSession.HasChangesAsync(CancellationToken)` comprueba si `/workspace` presenta cambios respecto al árbol `BaseSha` subido al crear el Sandbox (sin depender de `.git`, que no existe en el Sandbox).
  - `ISandboxToolsProvisioner.EnsureClaudeCliAsync(ISandboxSession, CancellationToken)` garantiza que `@anthropic-ai/claude-code` (`2.1.311`) está instalado en el volumen `codenetix-tools` antes de invocar `claude`.
- **Provisión de `@anthropic-ai/claude-code` (`CodeNetix.Sandbox`, AD-6):**
  - Si `claude` ya está disponible en el Sandbox (en `/tools/node_modules/.bin/claude`, `/tools/bin/claude` o en `PATH`), no hace nada.
  - Si no está instalado en `codenetix-tools`, habilita temporalmente `registry.npmjs.org:443` en `NuGetConnectProxy` solo durante la instalación, ejecuta un contenedor efímero con `codenetix-tools` montado en lectura/escritura en `/tools` (`npm install --prefix /tools --no-audit --no-fund @anthropic-ai/claude-code@2.1.311`) y revoca el permiso de npm y elimina el contenedor en el `finally`.
  - Los contenedores del Sandbox incluyen `/tools/node_modules/.bin:/tools/bin` en su `PATH`.
- **Agente Programador (`ClaudeCodeAgent` en `CodeNetix.Agents`, AD-5):**
  - Invoca en `ISandboxSession.ExecAsync` exactamente:
    `claude --bare -p <prompt> --output-format json --permission-mode acceptEdits --allowedTools "Bash,Read,Edit,Write" --permission-prompts none`
  - Pasa el modelo efectivo (`EffectiveConfig.Models?.Coder` o `AgentsOptions.Coder?.Model`) en la variable de entorno `ANTHROPIC_MODEL` del comando.
  - El prompt incluye instrucciones fijas en inglés y envuelve siempre en bloques `<untrusted-data>` (`UntrustedText.RenderAsDataBlock`) el título de la issue (`issue-title`), el cuerpo (`issue-body`), la `Spec` (`spec`), las Rutas protegidas (`protected-paths`) y, en reintentos, el motivo del fallo previo (`feedback`).
  - Emite la salida del proceso (`StdOut` y `StdErr` no vacíos) como `OutputAppended` a través de `IRunEventSink`.
  - Si el comando vence por timeout, devuelve `ExitCode != 0`, devuelve `"is_error": true` en el JSON o no deja ningún archivo modificado/añadido/eliminado en `/workspace` (`HasChangesAsync == false`), devuelve `CodingResult.Failed(reason)`.
- **Sumidero de eventos (`RunEventSink`, AD-3, AD-13):**
  - Redacta todos los secretos con `ISecretRedactor` antes de persistir `OutputAppended`.
  - Limita la salida acumulada de `OutputAppended` a 5 MiB (`5 * 1024 * 1024` bytes UTF-8) por Etapa (`(runId, stageId)`), recortando en frontera de carácter y emitiendo `OutputTruncated` una sola vez al superarlo.
- **Orquestador y agregado (`PipelineRun` y `RunOrchestrator`, AD-2, AD-16):**
  - `PipelineRun` mantiene los contadores `CodingAttempts` y `FailedCodingAttempts` (persistidos en `run_runs` mediante migración EF Core).
  - Tras `SpecGenerated`, desaparece el puente de la 1.8: la Ejecución transita de `Analyzing` a `Coding` (`run.StartCoding()`).
  - Si un intento del Programador devuelve `CodingResult.Failed`, incrementa `CodingAttempts` y `FailedCodingAttempts` en `PipelineRun`, emite `CodingAttemptFailed` y reintenta pasando el motivo como `Feedback` mientras `CodingAttempts < EffectiveConfig.MaxCodingAttempts`; al agotar los intentos, la Ejecución pasa a `Failed(TestsFailed, <motivo>)`.
  - Si el intento devuelve `CodingResult.Completed`, incrementa `CodingAttempts` en `PipelineRun` y, hasta que la historia 2.4 conecte `ITestRunner`, cierra provisionalmente con `Failed(StageError, "Pruebas no disponibles")` (`RunOrchestrator.Detail`).

**Never:**
- Pasar el texto de la issue o de la `Spec` fuera de bloques `<untrusted-data>` o ejecutar `claude` sin `--bare`.
- Ejecutar `git` dentro del Sandbox o montar `.git` en `/workspace`.
- Dejar `registry.npmjs.org:443` abierto en el proxy `CONNECT` fuera del paso de provisión de `codenetix-tools`.
- Ejecutar `dotnet build` / `dotnet test` desde el orquestador (`ITestRunner` pertenece a la historia 2.4).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Implementación con cambios | Spec válida, Sandbox activo, `claude` sale con 0 y modifica `/workspace` | Transición `Analyzing → Coding`, invocación de `claude --bare -p ...`, emisión de `OutputAppended`, `CodingAttempts = 1`, `FailedCodingAttempts = 0` | Puente provisional a 2.4: `Failed(StageError, "Pruebas no disponibles")` |
| Salida con error del CLI | `claude` devuelve `ExitCode != 0`, `TimedOut` o `"is_error": true` | Cuenta como intento fallido en `PipelineRun` (`FailedCodingAttempts++`), emite `CodingAttemptFailed` y reintenta con feedback | Al agotar `MaxCodingAttempts`, `Failed(TestsFailed, motivo)` |
| Sin cambios en `/workspace` | `claude` sale con 0 pero `/workspace` es idéntico al `BaseSha` | Cuenta como intento fallido en `PipelineRun` (`"el Programador terminó sin cambios en el árbol de trabajo"`) y reintenta | Al agotar `MaxCodingAttempts`, `Failed(TestsFailed, motivo)` |
| Fallo en 1.er intento y éxito en 2.º | 1.er intento falla (sin cambios o error), 2.º modifica `/workspace` | 2.º intento recibe el fallo en `<untrusted-data label="feedback">`; `CodingAttempts = 2`, `FailedCodingAttempts = 1` | Continúa tras el 2.º intento |
| Inyección en issue o Spec | Issue/Spec con `</untrusted-data>` e instrucciones maliciosas | Todo el contenido viaja neutralizado dentro de `<untrusted-data>` con nonce aleatorio | — |
| Salida > 5 MiB o con secretos | `claude` imprime la API key o supera 5 MiB en la Etapa | Secretos enmascarados por `ISecretRedactor`; al superar 5 MiB se recorta y se emite un único `OutputTruncated` | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/Runs.cs` -- añadir `OutputAppended`, `OutputTruncated` y `CodingAttemptFailed` a `[JsonPolymorphic]` de `RunEvent`.
- `src/CodeNetix.Contracts/Sandbox.cs` -- añadir `HasChangesAsync` a `ISandboxSession` e `ISandboxToolsProvisioner`.
- `src/CodeNetix.Contracts/Coding.cs` (nuevo) -- `CodingInput`, `CodingResult` e `ICodingAgent`.
- `src/CodeNetix.Sandbox/WorkspaceArchiveContent.cs`, `SandboxManager.cs`, `SandboxSession.cs` -- calcular el manifiesto SHA-256 de los archivos del `BaseSha` al subir el tar, implementar `HasChangesAsync` comparando `/workspace` contra dicho manifiesto e incluir `/tools/node_modules/.bin:/tools/bin` en `PATH`.
- `src/CodeNetix.Sandbox/Gateway/NuGetConnectProxy.cs` y `src/CodeNetix.Sandbox/SandboxToolsProvisioner.cs` (nuevo) -- permitir temporalmente `registry.npmjs.org:443` durante la instalación inicial de `@anthropic-ai/claude-code@2.1.311` en `codenetix-tools`.
- `src/CodeNetix.Agents/Features/ImplementSpec/ClaudeCodeAgent.cs` y `CoderPrompt.cs` (nuevos) -- construcción del prompt con `<untrusted-data>`, ejecución de `claude --bare -p ...`, emisión de `OutputAppended` y verificación de salida y cambios.
- `src/CodeNetix.Pipeline/Domain/PipelineRun.cs`, `Data/PipelineDbContext.cs`, `Data/Migrations/` -- añadir `CodingAttempts` y `FailedCodingAttempts` a `PipelineRun` y su migración EF Core.
- `src/CodeNetix.Pipeline/Events/RunEventSink.cs` -- aplicar el límite de 5 MiB por Etapa y emitir `OutputTruncated`.
- `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` -- sustituir el puente de la 1.8 por `StartCoding()` y el bucle de intentos de `ICodingAgent`.
- `tests/CodeNetix.Host.Tests/` -- actualizar `FakeSandboxSession` y secuencias de eventos de 1.6–1.9; añadir `CoderTests.cs` cubriendo toda la matriz I/O.

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Contracts/Runs.cs`, `Sandbox.cs` y `Coding.cs` -- definir `OutputAppended`, `OutputTruncated`, `CodingAttemptFailed`, `ICodingAgent`, `ISandboxSession.HasChangesAsync` e `ISandboxToolsProvisioner` -- contratos entre Pipeline, Agents y Sandbox (AD-1, AD-3, AD-5, AD-6).
- [x] `src/CodeNetix.Sandbox/` y `docker/sandbox.Dockerfile` -- implementar `HasChangesAsync` sobre el manifiesto SHA-256 del `BaseSha`, añadir `/tools/node_modules/.bin:/tools/bin` al `PATH` e implementar `SandboxToolsProvisioner` con apertura temporal de `registry.npmjs.org:443` en `NuGetConnectProxy` -- permite detectar cambios sin `.git` y provisionar `@anthropic-ai/claude-code@2.1.311` en `codenetix-tools` (AD-6).
- [x] `src/CodeNetix.Agents/Features/ImplementSpec/` y `AgentsServiceCollectionExtensions.cs` -- implementar `CoderPrompt` y `ClaudeCodeAgent` (`ICodingAgent`) -- ejecuta el CLI con `--bare` y bloques `<untrusted-data>` y publica `OutputAppended` (AD-5).
- [x] `src/CodeNetix.Pipeline/` -- añadir `CodingAttempts` y `FailedCodingAttempts` a `PipelineRun` con migración en `run_runs`, aplicar el tope de 5 MiB por Etapa en `RunEventSink` y conectar `StartCoding()` + bucle de intentos en `RunOrchestrator` -- cumple AD-2, AD-3 y AD-16.
- [x] `tests/CodeNetix.Host.Tests/` -- actualizar `FakeSandboxSession` y los tests existentes al paso por `Coding`, y crear `CoderTests.cs` verificando flags del CLI, bloques `<untrusted-data>`, `OutputAppended`/`OutputTruncated`, detección de árbol sin cambios, reintentos con feedback, contadores en `PipelineRun` y provisión de `codenetix-tools`.

**Acceptance Criteria:**
- Given una Ejecución con `Spec` válida y un Sandbox activo, when el orquestador entra en `Coding`, then `ClaudeCodeAgent` ejecuta dentro del Sandbox `claude --bare -p <prompt> --output-format json --permission-mode acceptEdits --allowedTools "Bash,Read,Edit,Write" --permission-prompts none`, con la `Spec` y la issue delimitadas en bloques `<untrusted-data>`, y emite la salida como `OutputAppended` mediante `IRunEventSink`.
- Given que el CLI termina con error, timeout o sin cambios en `/workspace`, when el orquestador aplica el resultado, then se contabiliza como un intento fallido en `PipelineRun` (`FailedCodingAttempts`), se reintenta con feedback hasta `MaxCodingAttempts` y, si se agotan los intentos, la Ejecución termina en `Failed(TestsFailed)`.

## Implementation Notes

- **Contratos:** Incorporados `OutputAppended`, `OutputTruncated` y `CodingAttemptFailed` a la unión polimórfica `RunEvent` en [Runs.cs](../../src/CodeNetix.Contracts/Runs.cs). Creado [Coding.cs](../../src/CodeNetix.Contracts/Coding.cs) con `CodingInput`, `CodingResult` (`Completed`, `Failed`) e `ICodingAgent`. Añadidos `ISandboxSession.HasChangesAsync` e `ISandboxToolsProvisioner` en [Sandbox.cs](../../src/CodeNetix.Contracts/Sandbox.cs).
- **Sandbox y Detección de Cambios:** En [WorkspaceArchiveContent.cs](../../src/CodeNetix.Sandbox/WorkspaceArchiveContent.cs) se calcula la huella SHA-256 [WorkspaceManifest.cs](../../src/CodeNetix.Sandbox/WorkspaceManifest.cs) mientras se emite el tar a Docker. `SandboxSession.HasChangesAsync` ejecuta el script de `WorkspaceManifest` en `/workspace` y compara contra el digest base sin requerir `.git`.
- **Provisión de Claude Code:** Implementado [SandboxToolsProvisioner.cs](../../src/CodeNetix.Sandbox/SandboxToolsProvisioner.cs) con apertura temporal controlada de `registry.npmjs.org:443` en `NuGetConnectProxy` y montaje RW temporal de `codenetix-tools` en `/tools`. Modificado `docker/sandbox.Dockerfile` y `SandboxManager` para priorizar `/tools/node_modules/.bin:/tools/bin` en el `PATH`.
- **Agente Programador:** Implementados [ClaudeCodeAgent.cs](../../src/CodeNetix.Agents/Features/ImplementSpec/ClaudeCodeAgent.cs) y [CoderPrompt.cs](../../src/CodeNetix.Agents/Features/ImplementSpec/CoderPrompt.cs) delimitando con `UntrustedText` el título, cuerpo, spec, rutas protegidas y feedback.
- **Pipeline y Orquestador:** Añadidos `CodingAttempts` y `FailedCodingAttempts` a `PipelineRun` con migración `AddCodingAttempts`. Actualizado `RunEventSink` en [RunEventLog.cs](../../src/CodeNetix.Pipeline/Events/RunEventLog.cs) para limitar a 5 MiB por etapa emitiendo `OutputTruncated` y redacción de secretos. `RunOrchestrator` reemplaza el puente anterior con `run.StartCoding()` y `CodeAsync`.
- **Pruebas:** Creado [CoderTests.cs](../../tests/CodeNetix.Host.Tests/CoderTests.cs) con 9 pruebas cubriendo todas las filas de la matriz I/O y casos límite. Adaptados los tests existentes de 1.6-1.9. Suite completa en verde con 769 tests superados.

## Spec Change Log

## Review Triage Log

| ID | Capa | Veredicto | Ruta | Evidencia / Justificación |
|---|---|---|---|---|
| BH-1 | blind-hunter | `defer` | defer | El soporte de recuperación de ejecuciones tras reinicio o caída del host es alcance de la historia 5.3 (`5-3-comportamiento-tras-un-reinicio`); en épicas 1 y 2 las ejecuciones comienzan siempre en `Analyzing`. |
| BH-2 | blind-hunter | `false` | - | La ejecución de Claude Code CLI con `--bare --output-format json -p` emite un único objeto JSON al finalizar; no genera NDJSON ni tramas múltiples. |
| BH-3 | blind-hunter | `low` | patch | `ClaudeCodeAgent.ParseOutput` sólo extraía la propiedad `result`; se añadieron comprobaciones de `error` y `message` para preservar la explicación en fallos con `is_error: true`. |
| BH-4 | blind-hunter | `low` | patch | `_stageTrackers` en `RunEventSink` acumulaba entradas en memoria sin desalojo; se implementó desalojo atómico al transicionar la ejecución a un estado terminal. |
| BH-5 | blind-hunter | `false` | - | Un fallo en `AppendCoreAsync` eleva una excepción que aborta la ejecución completa; el rastreador en memoria no vuelve a ser consultado. |
| BH-6 | blind-hunter | `false` | - | La especificación exige explícitamente descartar la salida sobrante al superar 5 MiB y continuar sin fallar; los emisores de log no dependen de la persistencia del evento descartado. |
| BH-7 | blind-hunter | `false` | - | La especificación define expresamente el registro temporal de `registry.npmjs.org:443` en el proxy CONNECT durante la instalación y su revocación al concluir; no exige filtrado por IP individual. |
| BH-8 | blind-hunter | `false` | - | El contenedor de instalación finaliza y es esperado con `WaitContainerAsync` antes de liberar el lease de `registry.npmjs.org:443`, cerrando todas las conexiones TCP activas. |
| BH-9 | blind-hunter | `false` | - | `DockerClient.RemoveContainerAsync` invoca la API de Docker con `?force=true`, forzando la detención y borrado de contenedores en ejecución. |
| BH-10 | blind-hunter | `false` | - | La arquitectura de red del sandbox de CodeNetix define un único extra host (`codenetix.local:<ip>`) para el enlace con la pasarela del host. |
| BH-11 | blind-hunter | `false` | - | Una salida superior a 1 MiB con código 0 y modificaciones en el árbol de trabajo constituye una ejecución satisfactoria; el truncado del búfer de captura no invalida el trabajo realizado. |
| BH-12 | blind-hunter | `false` | - | `MaxCodingAttempts` es validado de forma estricta `>= 1` tanto en la configuración de la instancia como en la configuración efectiva del repositorio. |
| BH-13 | blind-hunter | `false` | - | Cuando `ExecAsync` lanza `SandboxUnavailableException`, la llamada a Docker falló a nivel de transporte/sistema y no existe resultado de salida que publicar. |
| BH-14 | blind-hunter | `false` | - | Git no realiza seguimiento de directorios vacíos; el manifiesto refleja fielmente la estructura del árbol git en `BaseSha`. |
| VG-1 | verification-gap | `medium` | patch | `ToolsProvisioner_skips_when_claude_already_available` verificaba un doble de prueba; se añadió prueba en `SandboxModuleTests` ejecutando la clase de producción `SandboxToolsProvisioner` contra `FakeDockerEngine`. |
| VG-2 | verification-gap | `medium` | patch | No existía prueba de regresión para la apertura temporal y revocación del lease en el proxy CONNECT; se añadió test en `EgressGatewayTests` verificando túnel 200 y posterior 403 Forbidden con `EgressBlocked`. |
| VG-3 | verification-gap | `low` | patch | `SandboxSession.HasChangesAsync` no se ejecutaba contra el motor de Docker en los tests; se añadieron casos en `SandboxModuleTests` comprobando digest idéntico, modificado y excepciones en error. |
| VG-O-1 | verification-gap | `low` | patch | `FakeDockerEngine` no gestionaba `POST /containers/{id}/wait`; se incorporó la ruta simulada devolviendo `StatusCode: 0`. |
| VG-O-2 | verification-gap | `low` | patch | En `FakeSandboxToolsProvisioner` la propiedad `OnSessionCreated` no tenía uso; se eliminó el código muerto. |

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todos los tests unitarios, de arquitectura y de integración en verde.

