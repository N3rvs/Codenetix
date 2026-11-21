---
title: 'Historia 2.4: Tests obligatorios con reintentos'
type: 'feature'
created: '2026-10-29'
status: 'done'
route: 'dispatch'
baseline_commit: '4460a446e5892db2029b2674ada1106f4d15745d'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-2-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Los cambios producidos por el Programador pueden introducir regresiones, no compilar o carecer de tests que verifiquen la Spec, arriesgando código defectuoso en el repositorio sin validación objetiva.

**Approach:** Integrar `ITestRunner` (`DotnetTestRunner`) en `CodeNetix.Sandbox` ejecutando `dotnet test` con logger TRX dentro del Sandbox para obtener una ejecución base (`TestBaseline`) al crear el Sandbox y un informe de pruebas (`TestReport`) estructurado tras cada intento del Programador. El orquestador evalúa el `TestReport` y exige compilación limpia, 0 fallos y al menos un test nuevo, reintentando con feedback detallado hasta `EffectiveConfig.MaxCodingAttempts` antes de fallar con `TestsFailed`, y dejando el `TestReport` visible por la API y listo para revisión.

## Boundaries & Constraints

**Always:**
- `CodeNetix.Sandbox` implementa `ITestRunner` (`DotnetTestRunner`), ejecutando `dotnet test --logger "trx;LogFileName=..."` dentro de `/workspace` de `ISandboxSession` con el timeout y límites del Sandbox.
- La línea base (`TestBaseline`) se captura al crear el Sandbox o antes de iniciar el bucle de programación, registrando los tests existentes.
- `TestReport` debe reportar con exactitud: si la compilación tuvo éxito (`BuildSucceeded`), total de tests ejecutados, fallidos, omitidos, lista de tests nuevos (`NewTests`), duración y si cumple los criterios de aprobación (`Passed = BuildSucceeded && FailedTests == 0 && NewTests.Count > 0`).
- La decisión de aprobación la toma el orquestador en Pipeline basándose en el `TestReport`, nunca en afirmaciones del agente o de Claude Code.
- Si un intento de programación termina con `TestReport` no aprobado (`!report.Passed`), se contabiliza como intento fallido en `PipelineRun`, se emite `CodingAttemptFailed` con el feedback estructurado (errores de compilación, tests fallidos o ausencia de tests nuevos) y se reintenta mientras `CodingAttempts < EffectiveConfig.MaxCodingAttempts`.
- Al agotar los intentos, la Ejecución pasa a `Failed(TestsFailed, <motivo>)`.
- Cuando el `TestReport` es válido (`report.Passed == true`), se persiste en `run_test_reports` y, como puente provisional hacia la Épica 3, la Ejecución pasa a `Failed(StageError, "Revisión no disponible")` manteniendo el `TestReport` consultable a través de `IRunReader` y `GET /api/runs/{id}`.
- Respetar la regla de arquitectura AD-1: `CodeNetix.Contracts` define los modelos e interfaces (`ITestRunner`, `TestReport`, `TestBaseline`), `CodeNetix.Sandbox` implementa la ejecución en el contenedor, `CodeNetix.Pipeline` orquesta y almacena, y `CodeNetix.Host` registra las dependencias y expone la API.

**Never:**
- No confiar en la salida en texto plano o afirmaciones de Claude Code sobre si los tests pasaron.
- No omitir la verificación de que se haya añadido al menos un test nuevo (`NewTests.Count >= 1`).
- No bloquear el contenedor o el host si `dotnet test` se cuelga (aplicar timeout de comando).
- No perder los detalles de los tests fallidos en el feedback proporcionado al Programador.
- No requerir acceso a internet ni dependencias NuGet externas para generar o parsear el TRX.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Compilación fallida | El código modificado tiene errores de compilación de C# | `TestReport(BuildSucceeded: false, Passed: false)`. Feedback incluye diagnóstico del compilador | Reintenta con feedback hasta `MaxCodingAttempts`; al agotarse `Failed(TestsFailed, motivo)` |
| Tests existentes fallan | Compila pero 1+ tests preexistentes o modificados fallan | `TestReport(FailedTests: >0, Passed: false)`. Feedback enumera los tests fallidos y mensajes de error | Reintenta con feedback hasta `MaxCodingAttempts`; al agotarse `Failed(TestsFailed, motivo)` |
| Sin tests nuevos | Compila y todos los tests pasan, pero `NewTests.Count == 0` | `TestReport(Passed: false)`. Feedback indica que no se añadió ningún test nuevo | Reintenta con feedback hasta `MaxCodingAttempts`; al agotarse `Failed(TestsFailed, motivo)` |
| Intento exitoso | Compila, todos los tests pasan y `NewTests.Count >= 1` | `TestReport(Passed: true)`. Se guarda en `run_test_reports` | Pasa al puente provisional de la Épica 3: `Failed(StageError, "Revisión no disponible")` |
| Reintentos agotados | 3 intentos consecutivos con fallos de tests | 3 registros de `CodingAttemptFailed`. `PipelineRun.FailedCodingAttempts == 3` | Ejecución pasa a `Failed(TestsFailed, último motivo)` |
| Error del motor Sandbox en dotnet test | El comando en el sandbox sufre timeout o caída de contenedor | Lanza `SandboxUnavailableException` | Manejado por el orquestador fallando la ejecución con `StageError` |
| Consulta por API | `GET /api/runs/{id}` tras un intento de test | Devuelve `RunResponse` con el objeto `testReport` poblado | Si aún no se han ejecutado tests, `testReport` es `null` |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/Testing.cs` (nuevo) -- define `ITestRunner`, `TestReport`, `TestCaseResult` y `TestBaseline` (AD-1, AD-6).
- `src/CodeNetix.Contracts/Runs.cs` -- añade `GetTestReportAsync(runId)` a `IRunReader`.
- `src/CodeNetix.Sandbox/Testing/DotnetTestRunner.cs` (nuevo) y `TrxParser.cs` -- ejecución de `dotnet test --logger "trx;LogFileName=..."`, extracción de resultados con `System.Xml.Linq` y cálculo diferencial contra la línea base.
- `src/CodeNetix.Sandbox/SandboxServiceCollectionExtensions.cs` -- registra `ITestRunner` en DI.
- `src/CodeNetix.Pipeline/Domain/RunTestReportRecord.cs` (nuevo) y `Data/PipelineDbContext.cs` -- mapeo y persistencia de `run_test_reports` con migración EF Core.
- `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` -- captura de `TestBaseline` al inicializar sandbox, llamada a `ITestRunner.RunAsync` tras `CodingResult.Completed`, reintentos con feedback y puente provisional a la Épica 3 ("Revisión no disponible").
- `src/CodeNetix.Host/Features/GetRun/GetRunEndpoint.cs` -- exposición de `testReport` en `GET /api/runs/{id}`.
- `tests/CodeNetix.Host.Tests/TestRunnerTests.cs` (nuevo) y `FakeSandbox.cs` -- pruebas unitarias del parser TRX, comparación diferencial de tests y pruebas de integración del orquestador.

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Contracts/Testing.cs` y `Runs.cs` -- definir contratos `ITestRunner`, `TestReport`, `TestCaseResult`, `TestBaseline` y consulta en `IRunReader` -- desacopla módulos bajo AD-1 y AD-6.
- [x] `src/CodeNetix.Sandbox/Testing/` y `SandboxServiceCollectionExtensions.cs` -- implementar `DotnetTestRunner` y parseo de TRX con `System.Xml.Linq` -- ejecuta `dotnet test` y produce `TestReport` determinista sin dependencias externas.
- [x] `src/CodeNetix.Pipeline/Domain/`, `Data/PipelineDbContext.cs` y `Data/Migrations/` -- entidad `RunTestReportRecord` y migración `AddTestReports` en `run_test_reports` -- persiste el informe final de pruebas.
- [x] `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` y `PipelineRun.cs` -- capturar línea base, conectar `ITestRunner.RunAsync`, validar compilación, tests y nuevo test obligatorio, reintentar con feedback hasta `MaxCodingAttempts` y puentear a "Revisión no disponible" -- cumple AD-6, AD-16 y FR-12.
- [x] `src/CodeNetix.Host/Features/GetRun/GetRunEndpoint.cs` -- exponer `testReport` en `RunResponse` -- permite la observabilidad del reporte a través de la API (AD-3).
- [x] `tests/CodeNetix.Host.Tests/` -- tests exhaustivos en `TestRunnerTests.cs` y actualización de dobles de prueba -- verifica toda la matriz I/O y casos límite.

**Acceptance Criteria:**
- Given que el Programador ha completado un intento con cambios en el Sandbox, when el orquestador invoca a `ITestRunner.RunAsync(session, baseline, cancellationToken)`, then se ejecutan `dotnet build` y `dotnet test` produciendo un `TestReport` que detalla el resultado de compilación, los tests totales y fallidos, y la lista de tests nuevos comparados con la línea base tomada al crear el Sandbox.
- Given un `TestReport` con fallos de compilación, tests fallidos o sin ningún test nuevo (`NewTests.Count == 0`), when quedan intentos (`run.CodingAttempts < config.MaxCodingAttempts`), then se contabiliza el intento fallido en `PipelineRun`, se emite `CodingAttemptFailed` y se reintenta pasando el detalle del fallo como feedback al Programador; al agotar los intentos, la Ejecución pasa a `Failed(TestsFailed, motivo)`.
- Given un `TestReport` correcto (`BuildSucceeded == true`, `FailedTests == 0` y `NewTests.Count >= 1`), when el orquestador lo procesa, then el `TestReport` se persiste en `run_test_reports`, queda consultable en `GET /api/runs/{id}` y la Ejecución finaliza provisionalmente en `Failed(StageError, "Revisión no disponible")`.

## Implementation Notes

- **Contratos:** Creado [Testing.cs](../../src/CodeNetix.Contracts/Testing.cs) con `TestBaseline`, `TestCaseResult`, `TestReport` (`Passed => BuildSucceeded && FailedTests == 0 && NewTests.Count > 0`) e `ITestRunner`. Añadido `GetTestReportAsync` a `IRunReader` en [Runs.cs](../../src/CodeNetix.Contracts/Runs.cs).
- **Sandbox (`DotnetTestRunner` y `TrxParser`):** Implementados [DotnetTestRunner.cs](../../src/CodeNetix.Sandbox/Testing/DotnetTestRunner.cs) y [TrxParser.cs](../../src/CodeNetix.Sandbox/Testing/TrxParser.cs) ejecutando `dotnet build` + `dotnet test --no-build --logger "trx;LogFileName=results.trx"` en `/workspace`, parseo XML seguro (`DtdProcessing.Prohibit`) sin dependencias NuGet y limpieza en `/tmp`. Registrado en [SandboxServiceCollectionExtensions.cs](../../src/CodeNetix.Sandbox/SandboxServiceCollectionExtensions.cs).
- **Pipeline y Persistencia:** Creado [RunTestReportRecord.cs](../../src/CodeNetix.Pipeline/Domain/RunTestReportRecord.cs), actualizada configuración en [PipelineDbContext.cs](../../src/CodeNetix.Pipeline/Data/PipelineDbContext.cs) con migración `AddTestReports` (`run_test_reports`) e implementado `GetTestReportAsync` en [RunReader.cs](../../src/CodeNetix.Pipeline/Features/GetRun/RunReader.cs). Conectado en [RunOrchestrator.cs](../../src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs) con captura de línea base, bucle de reintentos con feedback estructurado y puente provisional `"Revisión no disponible"`.
- **Host API:** Expuesto `testReport` en `RunResponse` en [GetRunEndpoint.cs](../../src/CodeNetix.Host/Features/GetRun/GetRunEndpoint.cs).
- **Pruebas:** Creado [TestRunnerTests.cs](../../tests/CodeNetix.Host.Tests/TestRunnerTests.cs) cubriendo las 7 filas de la matriz I/O y actualizados `FakeSandbox.cs`, `CodeNetixFactory.cs`, `AnalystTests.cs`, `FixFollowTests.cs` y `SubmitManualEventTests.cs`. Verificado con `dotnet test`: 783 tests pasados, 0 fallidos.

## Spec Change Log

## Review Triage Log

| ID | Capa | Veredicto | Ruta | Evidencia / Justificación |
|---|---|---|---|---|
| BH-1 / EC-1 | blind-hunter, edge-case-hunter | `high` | patch | `CaptureBaselineAsync` ejecuta `dotnet build` dentro de `/workspace`, creando `bin/` y `obj/` que alteran `WorkspaceManifest` y hacen que `ISandboxSession.HasChangesAsync()` devuelva siempre `true`. Se redirigen los artefactos de compilación fuera de `/workspace` (en `/tmp/codenetix-artifacts`). |
| BH-2 / EC-2 / VG-O-1 | blind-hunter, edge-case-hunter, verification-gap | `high` | patch | `--logger "trx;LogFileName=results.trx"` sobrescribe el archivo TRX en soluciones con múltiples proyectos de test. Se cambia a `--logger trx` y se leen y fusionan todos los `*.trx` de `trxDir`. |
| BH-3 / EC-3 / VG-O-1 | blind-hunter, edge-case-hunter, verification-gap | `high` | patch | `DotnetTestRunner.RunAsync` ignoraba `testResult.ExitCode != 0` si había TRX parcial sin fallos. Se añade un fallo sintético cuando `ExitCode != 0` y `FailedTests == 0`. |
| BH-4 / EC-4 / EC-5 / VG-O-2 | blind-hunter, edge-case-hunter, verification-gap | `high` | patch | Un TRX truncado (> 1 MiB en `cat`) o malformado lanzaba `FormatException` sin capturar. Se comprueba `result.Truncated` y se envuelve `FormatException` en `SandboxUnavailableException`. |
| BH-5 | blind-hunter | `high` | patch | `RunTestReportRecord.ReportJson` se persistía y exponía en `GET /api/runs/{id}` sin pasar por `ISecretRedactor`. Se redactan `BuildOutput`, `ErrorMessage` y `StackTrace` (y el feedback) con `ISecretRedactor`. |
| BH-6 | blind-hunter | `medium` | patch | `CombineOutput` truncaba por el inicio descartando errores `error CS...` del final de `dotnet build`. Se trunca conservando el final (tail). |
| BH-7 | blind-hunter | `low` | defer | Si `CaptureBaselineAsync` falla el build base o no genera TRX, devuelve `TestBaseline.Empty`; un repositorio sin tests previos es válido y el caso de repo base roto se puede mejorar con logging en una iteración posterior. |
| BH-8 / EC-7 | blind-hunter, edge-case-hunter | `medium` | patch | `ResolveFullName` comprobaba `.` en todo `rawName` (fallando en argumentos como `Method(1.5)`) y `Warning` se clasificaba como skipped. Se inspecciona solo el prefijo antes de `(` y se retira `Warning` de `IsSkippedOutcome`. |
| BH-9 | blind-hunter | `low` | defer | Soporte para repositorios con múltiples `.sln`/`.csproj` en raíz o solución en subdirectorio requiere configuración adicional en `codenetix.yml`, fuera del alcance de la 2.4. |
| BH-10 / VG-1 / VG-2 / VG-3 | blind-hunter, verification-gap | `medium` | patch | Faltaban pruebas para el reenvío de `TestBaseline` en `RunOrchestrator`, `FailBaselineWith` (`SandboxUnavailableException` en baseline), ramas de TRX ausente y fallo de build en `CaptureBaselineAsync`, y verificación de columnas SQLite de `run_test_reports`. |
| BH-11 | blind-hunter | `low` | patch | Comentarios XML en `RunOrchestrator.cs` (líneas 37, 336–339) todavía citaban el puente antiguo `"Pruebas no disponibles"`. Se actualizan a `"Revisión no disponible"`. |
| EC-6 | edge-case-hunter | `medium` | patch | En tests parametrizados MSTest (`[DataRow]`), los resultados y errores están dentro de `UnitTestResult/InnerResults`. Se expanden `InnerResults/UnitTestResult` en `TrxParser.Parse`. |

