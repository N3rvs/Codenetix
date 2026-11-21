---
title: 'Historia 3.2: Rondas de revisión'
type: 'feature'
created: '2026-11-21'
status: 'done'
route: 'dispatch'
baseline_commit: '99323a25f8e7936b88061175c88a9bab8c852ffc'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-3-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Tras la Historia 3.1, la Ejecución siempre termina en un puente provisional después de la primera ronda de Revisores sin reaccionar a sus Veredictos (`Approved`, `ChangesRequested`, `Rejected`) ni iterar para corregir los hallazgos.

**Approach:** Implementar el bucle de Rondas de revisión en `RunOrchestrator` (FR-16, AD-16): evaluar los Veredictos de cada ronda tras `ReviewAsync`, fallar inmediatamente con `ReviewRejected` ante cualquier `Rejected`, volver a `Coding` (`run.StartReviewRound()`) pasando los hallazgos estructurados como `Feedback` al Programador y repitiendo tests y revisión mientras queden rondas (`EffectiveConfig.MaxReviewRounds`, 2 por defecto), fallar con `ReviewRoundsExhausted` al agotar rondas sin unanimidad, y avanzar al puente provisional hacia la Historia 3.3 (`"Creación de PR no disponible"`) ante aprobación unánime.

## Boundaries & Constraints

**Always:**
- **Precedencia de evaluación de Veredictos en una ronda:**
  1. Si al menos un Revisor emite `Rejected` → `Failed(FailureReason.ReviewRejected, <resumen de rechazos>)` inmediatamente (incluso si quedan rondas y aunque otro Revisor pidiera `ChangesRequested` o aprobara).
  2. Si todos los Revisores activos de la ronda emiten `Approved` (unanimidad) → listo para `Decidiendo`; puente provisional hasta Historia 3.3: `Failed(FailureReason.StageError, "Creación de PR no disponible")`.
  3. Si al menos un Revisor emite `ChangesRequested` y ningún Revisor emite `Rejected`:
     - Si `round < config.MaxReviewRounds` → transición `Reviewing → Coding` vía `run.StartReviewRound()`, construyendo `Feedback` estructurado con el resumen y los hallazgos (`archivo:línea`, severidad, mensaje) de los Revisores con `ChangesRequested`, y ejecutando la ronda `round + 1` (Programador → `ITestRunner.RunAsync` con el mismo `TestBaseline` → `ComputeDiffAsync` contra `BaseSha` → Revisores en paralelo con `round + 1`).
     - Si `round >= config.MaxReviewRounds` → `Failed(FailureReason.ReviewRoundsExhausted, <resumen>)`.
- **Intentos de programación por ronda:** Cada ronda de revisión concede hasta `config.MaxCodingAttempts` intentos del Programador + tests, mientras `PipelineRun.CodingAttempts` y `PipelineRun.FailedCodingAttempts` siguen acumulando el total histórico de la Ejecución.
- **Línea base única:** `TestBaseline` se captura una sola vez al iniciar la fase de programación (antes de la ronda 1) y se reutiliza en todas las rondas, de modo que los tests nuevos respecto a `BaseSha` siguen contando en las rondas de corrección.
- **Aislamiento intacto (FR-14, AD-5):** En la ronda 2+, los Revisores siguen recibiendo únicamente `Spec`, el nuevo `Diff` contra `BaseSha` y el último `TestReport` — nunca el historial ni la salida del Programador.
- **Veredictos visibles:** Todos los Veredictos de todas las rondas quedan persistidos en `run_verdicts` (con `round = 1, 2, ...`) y expuestos ordenados en `GET /api/runs/{id}` sea cual sea el desenlace (`ReviewRejected`, `ReviewRoundsExhausted` o unanimidad).
- **Redacción:** El feedback de revisión y los resúmenes de fallo provienen siempre de los Veredictos ya redactados por `ISecretRedactor`.

**Never:**
- No reiniciar ni perder `TestBaseline` entre rondas.
- No agotar el bucle de la ronda 2 por intentos gastados en la ronda 1.
- No ignorar un `Rejected` aunque queden rondas disponibles.
- No saltar `ITestRunner.RunAsync` tras una corrección de ronda de revisión (FR-16 exige repetir compilación y tests antes de volver a revisar).
- No requerir nuevas migraciones EF Core (la tabla `run_verdicts` ya soporta múltiples rondas por `(run_id, round, reviewer)`).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Unanimidad en Ronda 1 | Todos los Revisores emiten `Approved` en Ronda 1 | Persiste 3 veredictos de Ronda 1; no inicia Ronda 2 | Puente provisional Historia 3.3: `Failed(StageError, "Creación de PR no disponible")` |
| `ChangesRequested` en Ronda 1 → `Approved` en Ronda 2 | Ronda 1 tiene 1+ `ChangesRequested`, `MaxReviewRounds = 2`; Ronda 2 todos `Approved` | Transición `Reviewing → Coding`, Programador recibe hallazgos como `Feedback`, se re-ejecutan tests y Revisores en Ronda 2 (6 veredictos en API) | Puente provisional: `Failed(StageError, "Creación de PR no disponible")` |
| `Rejected` en Ronda 1 (con rondas disponibles) | 1 Revisor emite `Rejected`, otros `Approved` o `ChangesRequested`, `MaxReviewRounds = 2` | Termina inmediatamente sin iniciar Ronda 2; veredictos visibles en API | `Failed(ReviewRejected, detalle)` |
| `Rejected` en Ronda 2 | Ronda 1 `ChangesRequested`, Ronda 2 1 Revisor emite `Rejected` | Termina con veredictos de Ronda 1 y Ronda 2 visibles en API | `Failed(ReviewRejected, detalle)` |
| Rondas agotadas sin unanimidad | Ronda 1 y Ronda 2 tienen `ChangesRequested` (`MaxReviewRounds = 2`) | Ejecuta 2 rondas completas (6 veredictos en API), no inicia Ronda 3 | `Failed(ReviewRoundsExhausted, detalle)` |
| `MaxReviewRounds = 1` en `codenetix.yml` | Ronda 1 tiene `ChangesRequested`, `MaxReviewRounds = 1` | No inicia Ronda 2 | `Failed(ReviewRoundsExhausted, detalle)` |
| Tests fallan en Ronda 2 tras corrección | Ronda 1 necesitó 2 intentos de coding; Ronda 2 falla tests en intento 1 y pasa en intento 2 | La Ronda 2 dispone de su propio cupo `MaxCodingAttempts`, reintenta tests y llega a Revisión Ronda 2 | Si agota `MaxCodingAttempts` en Ronda 2 → `Failed(TestsFailed, motivo)` |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` -- actualizar `Detail` a `"Creación de PR no disponible"`, envolver programación+tests+revisión en el bucle de rondas (`round = 1..config.MaxReviewRounds`) con intentos por ronda (`roundAttempts`), evaluar veredictos (`Rejected` / `Approved` unánime / `ChangesRequested` + `StartReviewRound()`), construir `BuildReviewFeedback`, `BuildRejectionDetail` y `BuildRoundsExhaustedDetail`.
- `tests/CodeNetix.Host.Tests/ReviewRoundsTests.cs` (nuevo) -- pruebas de integración y unidad cubriendo las 7 filas de la matriz I/O (unanimidad, corrección en Ronda 2 con verificación de feedback + re-ejecución de tests + 6 veredictos en API, `ReviewRejected` en Ronda 1 y 2, `ReviewRoundsExhausted` con `MaxReviewRounds = 2` y `1`, cupo independiente de `MaxCodingAttempts` en Ronda 2).
- `tests/CodeNetix.Host.Tests/` -- actualizar aserciones del puente provisional `"Rondas de revisión no disponibles"` → `"Creación de PR no disponible"` en los tests existentes (`AnalystTests.cs`, `FixFollowTests.cs`, `TestRunnerTests.cs`, `ReviewerTests.cs`, etc.).

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` -- implementar el bucle de Rondas de revisión, evaluación determinista de veredictos (`Rejected`, unanimidad `Approved`, `ChangesRequested`), formateo de feedback con hallazgos, transición `StartReviewRound()`, cupo de intentos por ronda y puente provisional `"Creación de PR no disponible"` -- cumple FR-16 y AD-16.
- [x] `tests/CodeNetix.Host.Tests/ReviewRoundsTests.cs` y tests existentes -- crear pruebas exhaustivas para las 7 filas de la matriz I/O y actualizar el texto del puente provisional en tests existentes -- verifica regresiones y toda la máquina de estados de revisión.

**Acceptance Criteria:**
- Given al menos un Veredicto `ChangesRequested` (sin `Rejected`) y `round < config.MaxReviewRounds`, when el orquestador aplica los Veredictos, then la Ejecución vuelve a `Coding` (`Reviewing → Coding`) entregando los hallazgos como `Feedback` al Programador, vuelve a ejecutar `ITestRunner.RunAsync` con la misma línea base y repite la revisión en `round + 1`.
- Given al menos un Veredicto `Rejected`, when el orquestador aplica los Veredictos, then la Ejecución pasa inmediatamente a `Failed(ReviewRejected)` con todos los Veredictos visibles en `GET /api/runs/{id}`.
- Given que se agotan las Rondas (`round >= config.MaxReviewRounds`) sin unanimidad, when el orquestador aplica la última revisión, then la Ejecución pasa a `Failed(ReviewRoundsExhausted)` con todos los Veredictos de todas las rondas visibles en `GET /api/runs/{id}`.
- Given aprobación unánime en cualquier ronda válida, when el orquestador aplica los Veredictos, then la Ejecución finaliza provisionalmente en `Failed(StageError, "Creación de PR no disponible")` con todos los Veredictos visibles.

## Implementation Notes

- **Orquestador (`RunOrchestrator.cs`):** Implementado el bucle de Rondas (`round = 1..MaxReviewRounds`) capturando `TestBaseline` una sola vez, otorgando cupo independiente `MaxCodingAttempts` por ronda (`roundAttempts`), evaluando veredictos con precedencia `Rejected` > unanimidad `Approved` > `ChangesRequested` + `run.StartReviewRound()` con feedback estructurado redactado (`BuildReviewFeedback`, `BuildRejectionDetail`, `BuildRoundsExhaustedDetail`) y actualizando el puente provisional a `"Creación de PR no disponible"`.
- **Pruebas:** Creado [ReviewRoundsTests.cs](../../tests/CodeNetix.Host.Tests/ReviewRoundsTests.cs) (10 pruebas cubriendo las 7 filas de la matriz I/O y los formateadores) y ajustados `AnalystTests.cs`, `FixFollowTests.cs`, `ReviewerTests.cs`, `TestRunnerTests.cs` y `FakeOpenAIServer.cs`. Verificado con `dotnet test`: **814/814 pruebas pasadas, 0 fallos, 0 omitidas**.

## Spec Change Log

## Review Triage Log

| ID | Capa | Veredicto | Ruta | Evidencia / Justificación |
|---|---|---|---|---|
| BH-1 | blind-hunter | `high` | patch | Si el intento 1 de la Ronda 2 falla tests o coding, `feedback` sobrescribía los hallazgos de la revisión de Ronda 1. Se separa `reviewFeedback` de `attemptFeedback` y se combinan ambos dentro de `MaxFeedbackLength`. |
| BH-2 | blind-hunter | `medium` | patch | `CodingResult.Failed.Reason` pasaba a `CodingInput.Feedback` sin redactar ni truncar. Se aplican `log.Redactor.Redact` y `TextLimits.Truncate(..., MaxFeedbackLength)`. |
| BH-3 / EC-1 | blind-hunter, edge-case-hunter | `medium` | patch | Si `roundVerdicts` llegase vacío o sin `ChangesRequested`, no se detenía con `StageError`. Se añade guarda `if (changesRequested.Count == 0)` → `Failed(StageError, ...)`. |
| BH-4 / EC-2 | blind-hunter, edge-case-hunter | `low` | patch | Eliminado bloque muerto `if (run.State == RunState.Coding)` tras el bucle `for` de rondas. |
| BH-5 | blind-hunter | `low` | defer | El presupuesto de 8.000 caracteres cubre sobradamente 3 revisores en uso normal; la asignación por revisor queda diferida si surge truncado real. |
| BH-6 | blind-hunter | `low` | patch | Normalizados saltos de línea a `\n` en `BuildReviewFeedback` y colapsados saltos de línea en `BuildRejectionDetail`/`BuildRoundsExhaustedDetail`. |
| BH-7 | blind-hunter | `false` | - | La especificación (FR-15, FR-16) exige explícitamente que una respuesta inválida tras reintentos cuente como `ChangesRequested`. |
| BH-8 | blind-hunter | `false` | - | `MaxCodingAttempts` está validado `>= 1` en `InstanceOptionsValidator` y `EffectiveConfigCalculator`. |
| BH-9 / BH-10 / VG-1 | blind-hunter, verification-gap | `medium` | patch | Añadidas aserciones negativas (`Assert.DoesNotContain` para revisores aprobados/no rechazados), verificación de `run_stages` en Ronda 2 y verificación de preservación de `reviewFeedback` en reintentos de Ronda 2 en `ReviewRoundsTests.cs`. |
| BH-11 | blind-hunter | `false` | - | `FakeOpenAIServer` es un doble de prueba interno diseñado para distinguir peticiones de Analista vs Revisor en tests HTTP. |
| BH-12 | blind-hunter | `false` | - | Los checkboxes e `Implementation Notes` ya se habían actualizado antes de la revisión. |
