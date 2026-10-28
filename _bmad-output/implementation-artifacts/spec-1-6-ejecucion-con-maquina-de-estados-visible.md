---
title: 'Historia 1.6: Ejecución con máquina de estados visible'
type: 'feature'
created: '2026-10-27'
status: 'done'
route: 'dispatch'
baseline_commit: '1b214a0c82572428ea131cc8e063eec1c3553ac4'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-1-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Un Evento aceptado se queda en `ing_events` y nada lo convierte en trabajo. No existe la Ejecución, su Estado ni forma de seguirla (AD-2, AD-3, AD-4).

**Approach:** Pipeline añade el agregado `PipelineRun` con la tabla de transiciones de AD-2, sus tablas `run_runs`/`run_events` y el único sumidero `IRunEventSink`. Un Dispatcher convierte cada Evento aceptado en una Ejecución (como mucho una no terminal por objeto). `GET /api/runs/<runId>` y `codenetix fix --follow` muestran el Estado y los `RunEvent` por orden de `seq`. Hasta la 1.7, un puente provisional cierra cada Ejecución como `Failed(StageError)`.

## Boundaries & Constraints

**Always:**
- **Contracts:**
  - `RunState`: `Received`, `Analyzing`, `Coding`, `Reviewing`, `Deciding`, `PullRequestCreated`, `Failed` y `Cancelled`, serializados con esos nombres.
  - `FailureReason`: la lista de AD-2.
  - `RunEvent` como unión discriminada `[JsonPolymorphic]` por `type`, con `seq`, `runId` y `at` (UTC). Tipos por ahora: `RunCreated { eventId, objectKey }` y `StateChanged { from, to, failureReason?, detail? }`.
  - `IRunEventSink.AppendAsync(runId, stageId?, payload, ct)` para productores externos.
  - `IAcceptedEventSource`, implementado por Ingestion: leer los Eventos `Accepted` pendientes de despachar y marcarlos.
  - `IEventDispatchSignal.Notify()`, implementado por Pipeline y llamado por Ingestion tras aceptar un Evento.
- **Agregado `PipelineRun`:**
  - Métodos: `StartAnalysis`, `StartCoding`, `StartReview`, `StartReviewRound` (`Reviewing` → `Coding`), `StartDeciding`, `CompletePullRequest`, `Fail(reason, detail)` y `Cancel`.
  - Transición ilegal → `InvalidRunTransitionException`. No hay setter público de `State`.
  - Cada método devuelve el `StateChanged` resultante.
  - Tabla exacta de AD-2: `Fail` desde cualquier estado no terminal; `Cancel` desde todos salvo `Deciding` y los terminales.
- **Persistencia:**
  - `PipelineDbContext`: prefijo `run`, `Order` después de `ing`, historial `__ef_migrations_run`.
  - `run_runs`: `id` (`Guid.CreateVersion7`), `short_id` (los 8 primeros caracteres hexadecimales del id, único), `event_id`, `object_key`, `state`, `failure_reason`, `failure_detail` y `created_at`/`updated_at` como ticks UTC (igual que 1.4).
  - Índice UNIQUE parcial sobre `object_key` cuando `state` no es terminal.
  - `run_events`: PK (`run_id`, `seq`), `type`, `payload_json` y `at`.
  - La transición y su `RunEvent` se guardan en el mismo `SaveChanges`: el sumidero interno añade el evento al mismo contexto y asigna `seq` = máximo + 1.
  - El payload pasa por `ISecretRedactor` antes de persistirse.
- **Ingestion:**
  - Nueva migración `ing` con `dispatched_at` y `run_id` en `ing_events`.
  - Si ya hay una Ejecución no terminal para el objeto, el Evento pasa a `Ignored` con el motivo "ya hay una Ejecución en curso para este objeto (<shortId>)".
- **Dispatcher** (`BackgroundService` en Pipeline):
  - `await IInstanceHealth.WhenReadyAsync` antes de nada.
  - Despierta con `IEventDispatchSignal` o cada 1 s.
  - Crea la Ejecución en `Received` (con `RunCreated` y el `StateChanged` inicial con `from` nulo) y marca el Evento como despachado.
  - Un fallo con un Evento se registra en el log y no detiene el bucle.
- **Puente provisional** (orquestador mínimo en Pipeline): después de crearla, `Fail(StageError, "Análisis no disponible todavía")`. Lo sustituye la 1.7.
- **API** (con `admin-token`):
  - `GET /api/runs/{id}`, donde `id` es el GUID o el `shortId`: `{ runId, shortId, state, failureReason, failureDetail, eventId, objectKey, createdAt, updatedAt, events: [...] }`.
  - `GET /api/runs/{id}/events?afterSeq=N`: solo los eventos con `seq` > N, en orden.
  - `GET /api/events/{eventId}`: `{ eventId, status, reason, runId? }`.
  - Un id que no existe → 404.
- **`codenetix fix <url> --follow`:**
  - Tras registrar el Evento, sondea `GET /api/events/{id}` hasta tener `runId` (o `Ignored`) y después `…/events?afterSeq=N` cada 500 ms.
  - Imprime una línea por evento (`[seq] StateChanged Received → Failed (StageError: …)`).
  - Termina en estado terminal: `PullRequestCreated` → código 0; `Failed` o `Cancelled` → código 1.
  - `--follow` puede ir antes o después de la URL.

**Never:** Etapas, `run_stages`, hub o SignalR (4.1), lotes de salida o truncado (Épica 2/4), reconciliación al arrancar (5.3), cancelar desde la API (4.4), concurrencia mayor que 1, efectos disparados por un `RunEvent`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Evento aceptado | `fix` de una issue | Ejecución creada en ≤ 2 s; eventos `RunCreated`, `StateChanged(null→Received)` y `StateChanged(Received→Failed, StageError)` con `seq` 1, 2, 3 | — |
| Objeto ocupado | Ejecución no terminal y nuevo Evento del mismo objeto | El nuevo Evento queda `Ignored` con el motivo y el shortId | — |
| Objeto libre | Ejecución anterior terminal | Se crea una Ejecución nueva | — |
| Transiciones | Todas las combinaciones (desde, método) | Válidas según AD-2; el resto lanza la excepción | Test de tabla exhaustivo |
| Atomicidad | Fallo al guardar el `RunEvent` | El Estado tampoco cambia | Rollback |
| `afterSeq` | `afterSeq=2` con 3 eventos | Solo el `seq` 3 | — |
| shortId | `GET /api/runs/<8 hex>` | Igual que con el GUID | Desconocido → 404 |
| Diagnóstico | Instancia en `Diagnostic` | El Dispatcher no crea nada | — |
| `--follow` | `fix --follow <url>` | Imprime los 3 eventos y sale con 1 (`Failed`) | — |
| `--follow` ignorado | Evento `Ignored` | Imprime el motivo y sale con 1 | — |
| Redacción | Payload con un secreto | Persistido enmascarado | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/` -- `EventEnvelope`, `EventStatus`, `IModuleDatabase` e `IInstanceHealth` (con `WhenReadyAsync`) ya existen.
- `src/CodeNetix.Ingestion/Data/IngestionDbContext.cs` y su migración -- patrón a copiar: ticks UTC con conversor, migración generada con `dotnet-ef`, `IModuleDatabase` con prefijo y orden.
- `src/CodeNetix.Ingestion/Features/SubmitManualEvent/ManualEventIngestion.cs` -- llamar a `IEventDispatchSignal.Notify()` tras persistir un `Accepted`.
- `src/CodeNetix.Persistence/DatabaseMigrator.cs` -- ordena por `Order` y después por `Prefix`.
- `src/CodeNetix.Pipeline/` -- módulo vacío; solo referencia Contracts.
- `src/CodeNetix.Host/ServeMode.cs` -- composición (`AddCodeNetixPipeline`) y mapeo de los endpoints en slices `Features/GetRun/`, `Features/GetRunEvents/` y `Features/GetEvent/`.
- `src/CodeNetix.Host/FixMode.cs` -- reintentos y configuración ya resueltos; añadir `--follow`.
- `tests/CodeNetix.Host.Tests/` -- fábrica con GitHub falso (1.5) y cliente autorizado.

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Contracts/` -- `RunState`, `FailureReason`, la unión `RunEvent`, `IRunEventSink`, `IAcceptedEventSource` e `IEventDispatchSignal`.
- [x] `src/CodeNetix.Pipeline/` -- `PipelineRun`, `PipelineDbContext` con su migración, el sumidero, `Dispatcher`, el orquestador puente, las consultas de lectura y `AddCodeNetixPipeline`.
- [x] `src/CodeNetix.Ingestion/` -- la migración del despacho, `IAcceptedEventSource` y la llamada a `Notify`.
- [x] `src/CodeNetix.Host/` -- los tres endpoints, `--follow` y la composición.
- [x] `tests/` -- test de tabla de transiciones (8 estados × 8 métodos); un test por fila de la matriz; tests de `--follow` contra un servidor falso.

**Acceptance Criteria:**
- Given un Evento aceptado, when pasan 2 s, then existe su Ejecución y `GET /api/runs/<shortId>` devuelve el Estado y los eventos en orden.
- Given cualquier cambio de Estado, when se inspecciona la BD, then cada fila de `run_runs` tiene en `run_events` su `StateChanged` con el `seq` correlativo y sin huecos.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde.

## Implementation Notes

- **shortId = 8 últimos hex, no los 8 primeros.** En un GUID v7 los 8 primeros caracteres son la marca de tiempo (cambian cada ~65 s), así que dos Ejecuciones creadas en el mismo minuto colisionarían en el índice UNIQUE. Se usan los 8 últimos (aleatorios); una colisión residual se resuelve reintentando la creación. Ratificado (ver Spec Change Log).
- **`event_id` UNIQUE en `run_runs`** (no pedido): si el proceso cae entre crear la Ejecución y marcar el Evento, el Dispatcher la reencuentra y solo marca el Evento, sin duplicar.
- **`stageId`** se acepta en `IRunEventSink.AppendAsync` pero no se persiste: `run_events` no tiene columna de Etapa hasta que exista `run_stages`.
- **`IRunEventSink` externo rechaza `RunCreated` y `StateChanged`**: solo el agregado emite transiciones. El sumidero interno es `RunEventLog` (añade al contexto del orquestador; `seq` = máximo persistido o pendiente + 1). `failure_detail` en `run_runs` también se redacta.
- **Lecturas en Contracts**: `IRunReader` (Pipeline) e `IEventReader` (Ingestion) para que los endpoints de Host no dependan de tipos internos de los módulos.
- **Dispatcher**: el orquestador se ejecuta en línea (concurrencia 1). Ajustes en `DispatcherOptions` (sondeo 1 s, espera de reintento 30 s, 5 intentos); las pruebas los acortan.
- **Revisión 1, parche 1 (fallo del orquestador)**: si `IRunOrchestrator.RunAsync` lanza, `StuckRunRecovery` carga la Ejecución en un contexto nuevo y, si sigue no terminal, aplica `Fail(StageError, "Error interno de orquestación: <motivo>")` (una línea, máx. 200 caracteres, redactado al persistir) con su `StateChanged`. Al arrancar (tras `WhenReadyAsync`), el Dispatcher vuelve a orquestar las Ejecuciones que quedaron en `Received`, antes del bucle de despacho.
- **Revisión 1, parche 2 (`--follow` sin fin)**: cada fase (esperar `runId`/`Ignored` y esperar eventos nuevos) abandona tras 10 min sin progreso (inyectable en `FixMode.RunAsync`), indicando en stderr el `GET /api/events/{id}` o `GET /api/runs/{id}` a consultar, y sale con 1.
- **Revisión 1, parche 3 (Eventos envenenados)**: `IAcceptedEventSource.GetPendingAsync` recibe los ids a excluir (los que esperan reintento), así los nuevos siempre son alcanzables. Tras 5 fallos el Evento pasa a `Ignored` con "no se pudo despachar: <error redactado>" y sale del mapa de reintentos.
- **Revisión 1, parches 4/5 (tests)**: orquestador que lanza (Ejecución `Failed(StageError)` y el siguiente Evento se despacha), Ejecución huérfana reanudada al rearrancar, Ejecución ya creada para un Evento sin despachar (se enlaza sin crear otra), Evento que siempre falla (`Ignored` tras 5 intentos y uno más nuevo se despacha) y corte por falta de progreso de `--follow` en ambas fases. Los tests que crean una Ejecución en `Received` esperan antes a que el Dispatcher termine su reanudación de arranque (si no, la cerraría).
- **`--follow`**: tolera hasta 20 fallos transitorios seguidos (red/5xx) al sondear; un 4xx termina con código 1. `GET /api/runs/{id}/events` devuelve un array; `afterSeq` negativo → 400.

## Spec Change Log

- 2026-10-27: `short_id` = 8 últimos hex del id (no los 8 primeros). Motivo: en GUID v7 los primeros son marca de tiempo y colisionarían en el UNIQUE. Ratificado por Claude bajo autonomía delegada.

## Review Triage Log

Revisión 1 (revisor combinado, 12 hallazgos):

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | high | Fallo del orquestador deja la Ejecución en `Received` para siempre (objeto bloqueado) | patch |
| 2 | medium | `--follow` sin límite si la Ejecución no avanza | patch |
| 3 | medium | 50+ Eventos fallidos bloquean el despacho de los nuevos | patch |
| 4 | low | Redacción sobre el JSON crudo puede corromperlo | defer |
| 5 | low | Eventos aceptados antes de 1.6 se despachan al actualizar | reject (intencional: siguen pendientes) |
| 6 | medium | Sin tests de resiliencia y recuperación del Dispatcher | patch |
| 7 | medium | Sin test de fallo del orquestador | patch |
| 8 | low | `Notify()` no verificado (el sondeo de 1 s lo enmascara) | reject |
| 9 | low | Camino de reintento por carrera del índice no ejecutado | reject |
| 10 | low | Éxito de `IRunEventSink` externo sin test | defer (1.7 trae el primer productor) |
| 11 | low | Salidas de error de `--follow` sin test | reject |
| 12 | low | "Objeto libre" solo probado desde `Failed` | reject |
