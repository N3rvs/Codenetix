---
title: 'Historia 5.3: Comportamiento tras un reinicio'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: 'e69c549c8bcaf08b25d1f61779c3eeee936088e0'
route: 'dispatch'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-5-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Actualmente `StartupReconciler` solo ejecuta migraciones, `admin.token` y la evaluación de salud antes de marcar `Ready` y despertar al `Dispatcher`. Tras un reinicio inesperado del contenedor/proceso, las Ejecuciones que quedaron en `Analyzing`, `Coding`, `Reviewing`, `Deciding` o `Received` no se cierran como `Failed(HostRestarted)`, y los contenedores `codenetix.run`, volúmenes `codenetix-ws-*` y directorios huérfanos en `workspaces/` quedan abandonados.

**Approach:** Completar `StartupReconciler` aplicando estrictamente el orden canónico de AD-18: (1) migraciones de todos los módulos + `admin.token`, (2) evaluación de salud (`InstanceOptionsValidator` + `ISandboxHealthCheck`), (3) reconciliación de todas las Ejecuciones no terminales a `Failed(FailureReason.HostRestarted)` con aviso `RunFailed` en GitHub (`IRunStartupReconciler`), (4) limpieza de contenedores huérfanos con etiqueta `codenetix.run`, volúmenes `codenetix-ws-*` (`ISandboxReconciler`, sin tocar nunca `codenetix-tools` ni volúmenes ajenos) y directorios huérfanos bajo `workspaces/` (cerrando el diferido de 1.7 en `deferred-work.md`), y (5) publicación de `Ready` / `Diagnostic`, de modo que el `Dispatcher` arranque únicamente al final y procese los Eventos `Accepted` que aún no tenían Ejecución.

## Boundaries & Constraints

**Always:**
- **Orden estricto AD-18 en `StartupReconciler` (FR-24):**
  1. `database.InitializeAsync` → `migrator.MigrateAllAsync` → `tokenStore.EnsureTokenFile`.
  2. Evaluación de salud (`InstanceOptionsValidator.Validate` + `sandboxHealth.CheckAsync`).
  3. Reconciliación de Ejecuciones no terminales (`IRunStartupReconciler.ReconcileNonTerminalRunsAsync`): toda Ejecución en `Received`, `Analyzing`, `Coding`, `Reviewing` o `Deciding` pasa a `Failed(FailureReason.HostRestarted, ...)` mediante `PipelineRun.Fail` + `RunEventLog.SaveAndPublishAsync`, liberando el índice único activo del objeto (`ux_run_runs_active_object_key`) y publicando el aviso `NotificationKind.RunFailed` en GitHub (best-effort con timeout acotado, sin relanzar si GitHub falla).
  4. Limpieza de recursos huérfanos (`ISandboxReconciler.CleanupOrphansAsync` + limpieza de `workspaces/`):
     - Detiene (con gracia acotada) y elimina todo contenedor Docker con etiqueta `codenetix.run`.
     - Elimina todo volumen Docker cuyo nombre comience por `codenetix-ws-` (buscando por etiqueta `codenetix.run` y/o listado filtrado por prefijo `codenetix-ws-`), **nunca** `codenetix-tools` ni volúmenes ajenos.
     - Elimina directorios huérfanos bajo `<DataDirectory>/workspaces/` (marcando como resuelta la entrada diferida de 1.7 en `deferred-work.md`).
     - Best-effort: un fallo en un contenedor/volumen/directorio concreto se registra como warning y continúa con el resto sin impedir el arranque del Host.
  5. Publicación de salud (`health.MarkReady()` o `health.MarkDiagnostic(reasons)`): el `Dispatcher` (que espera `health.WhenReadyAsync()`) **nunca** arranca antes de completarse los pasos 1–4; una vez `Ready`, procesa normalmente todos los Eventos `Accepted` en `ing_events` que aún no tenían Ejecución.
- **Aislamiento modular (AD-1):**
  - `IRunStartupReconciler` en `CodeNetix.Contracts/Runs.cs` (implementado en `CodeNetix.Pipeline`).
  - `ISandboxReconciler` y `SandboxCleanupSummary` en `CodeNetix.Contracts/Sandbox.cs` (implementado en `CodeNetix.Sandbox` usando `DockerClient`).
  - `CleanupOrphansAsync` en `IRepositoryWorkspace` (`CodeNetix.Contracts`) implementado en `CodeNetix.GitHub`.
  - Mantener compatibilidad de constructores en tests de host mínimos (`AccessTests`, `CommandLineTests`, `InstanceConfigurationTests`, `SubmitManualEventTests`).
- **Verificación real:**
  - `tests/CodeNetix.Host.Tests/StartupReconciliationTests.cs` verificando:
    1. Orden estricto de los 5 pasos de AD-18 (registrando timestamps/secuencia de llamadas: migraciones → salud → `ReconcileNonTerminalRunsAsync` → `CleanupOrphansAsync` → `MarkReady` / `Dispatcher`).
    2. Reinicio real del Host sobre `/data` persistente con Ejecuciones no terminales (`Received`, `Analyzing`, `Coding`, `Reviewing`, `Deciding`) y una Ejecución terminal (`PullRequestCreated`): todas las no terminales pasan a `Failed(HostRestarted)` con evento `StateChanged` y aviso `RunFailed`, la terminal queda intacta, y los Eventos `Accepted` sin Ejecución se despachan después del arranque.
    3. Limpieza de Docker (`SandboxReconciler` contra HTTP fake de Docker): detiene y borra contenedores con etiqueta `codenetix.run`, borra volúmenes `codenetix-ws-*`, **preserva** `codenetix-tools` y volúmenes ajenos, y tolera errores parciales sin lanzar.
    4. Limpieza de directorios huérfanos bajo `<DataDirectory>/workspaces/`.
    5. Arranque en modo `Diagnostic`: reconcilia Ejecuciones no terminales en SQLite sin despertar al `Dispatcher` ni crashear si Docker no responde.

**Never:**
- No marcar `health.MarkReady()` antes de terminar la reconciliación de Ejecuciones y la limpieza de huérfanos.
- No eliminar jamás el volumen `codenetix-tools` ni contenedores/volúmenes que no pertenezcan a CodeNetix.
- No modificar Ejecuciones que ya estaban en estado terminal (`PullRequestCreated`, `Failed`, `Cancelled`).
- No descartar ni marcar como fallidos los Eventos `Accepted` en `ing_events` que todavía no tenían Ejecución.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Orden estricto de arranque AD-18 | Arranque de `StartupReconciler` con spies/fakes de orden | Ejecuta exactamente: `MigrateAll` → `CheckHealth` → `ReconcileNonTerminalRuns` → `CleanupOrphans` → `MarkReady` → `Dispatcher` | Si falla DB/migraciones, aborta arranque antes de salud/reconciliación |
| Reinicio con Ejecuciones no terminales + terminales + Eventos `Accepted` pendientes | `/data` compartido con runs en `Received`/`Analyzing`/`Coding`/`Reviewing`/`Deciding`, 1 run `PullRequestCreated`, y 1 Evento `Accepted` sin Run | Todas las no terminales pasan a `Failed(HostRestarted)` con `StateChanged` y `RunFailed`; `PullRequestCreated` queda intacta; el Evento `Accepted` pendiente se despacha tras el arranque | Si el objeto del Evento pendiente coincidía con un run reconciliado, el índice activo ya está libre y el nuevo Run se crea sin `ObjectBusy` |
| Limpieza de contenedores `codenetix.run` y volúmenes `codenetix-ws-*` | Docker fake con 2 contenedores `codenetix.run`, 2 volúmenes `codenetix-ws-*`, `codenetix-tools` y 1 volumen ajeno `postgres-data` | Detiene/elimina los 2 contenedores y los 2 volúmenes `codenetix-ws-*`; **no** toca `codenetix-tools` ni `postgres-data` | Si falla el borrado de 1 contenedor/volumen, registra warning y continúa con el resto sin lanzar |
| Directorios huérfanos en `workspaces/` | `<DataDirectory>/workspaces/<shortId>` residual tras caída | Eliminado durante la reconciliación de arranque | Error de IO en un directorio se registra sin abortar arranque |
| Reinicio en modo `Diagnostic` (p. ej. Docker caído) | Falta configuración o Docker no responde + run en `Coding` en BD | El run en `Coding` pasa a `Failed(HostRestarted)` en SQLite, `CleanupOrphansAsync` tolera Docker caído sin lanzar, instancia queda en `Diagnostic` y `Dispatcher` no arranca | 0 excepciones no controladas |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/Runs.cs`, `Sandbox.cs`, `RepositoryWorkspace.cs` (o archivo donde viva `IRepositoryWorkspace`) -- añadir `IRunStartupReconciler`, `ISandboxReconciler`, `SandboxCleanupSummary` y `CleanupOrphansAsync` en `IRepositoryWorkspace`.
- `src/CodeNetix.Pipeline/Features/RunPipeline/RunStartupReconciler.cs` (nuevo) y `PipelineServiceCollectionExtensions.cs` -- reconciliar todas las Ejecuciones no terminales a `Failed(HostRestarted)` + aviso `RunFailed`.
- `src/CodeNetix.Sandbox/SandboxReconciler.cs` (nuevo), `Docker/DockerClient.cs` (si necesita listado de volúmenes sin filtro de etiqueta o por prefijo) y `SandboxServiceCollectionExtensions.cs` -- detener/borrar contenedores `codenetix.run` y volúmenes `codenetix-ws-*` preservando `codenetix-tools`.
- `src/CodeNetix.GitHub/Features/PrepareRepository/RepositoryWorkspace.cs` -- implementar `CleanupOrphansAsync` sobre `<DataDirectory>/workspaces/`.
- `src/CodeNetix.Host/StartupReconciler.cs` -- aplicar estrictamente el orden AD-18 (migraciones → salud → reconciliación de Ejecuciones → limpieza de contenedores/volúmenes/workspaces → `MarkReady`/`MarkDiagnostic`).
- `_bmad-output/implementation-artifacts/deferred-work.md` -- marcar como resuelta la entrada de 1.7 sobre limpieza de `workspaces/<shortId>` huérfanos.
- `tests/CodeNetix.Host.Tests/StartupReconciliationTests.cs` (nuevo) -- cubrir las 5 filas de la matriz I/O.

## Tasks & Acceptance

**Execution:**
- [x] `CodeNetix.Contracts/` (`IRunStartupReconciler`, `ISandboxReconciler`, `SandboxCleanupSummary`, `IRepositoryWorkspace.CleanupOrphansAsync`), `CodeNetix.Pipeline/Features/RunPipeline/RunStartupReconciler.cs`, `CodeNetix.Sandbox/SandboxReconciler.cs` y `CodeNetix.GitHub/.../RepositoryWorkspace.cs` -- implementar reconciliación de Ejecuciones no terminales a `Failed(HostRestarted)` y limpieza de contenedores `codenetix.run`, volúmenes `codenetix-ws-*` y `workspaces/` huérfanos (cerrando diferido de 1.7 en `deferred-work.md`) -- cumple AD-1, AD-18 y FR-24.
- [x] `src/CodeNetix.Host/StartupReconciler.cs` -- ejecutar estrictamente el orden de AD-18: migraciones → evaluación de salud → reconciliación de Ejecuciones → limpieza de huérfanos → publicación de salud (`Ready`/`Diagnostic`) para arranque del `Dispatcher` -- cumple AD-18 y FR-24.
- [x] `tests/CodeNetix.Host.Tests/StartupReconciliationTests.cs` -- cubrir las 5 filas de la matriz I/O (orden estricto AD-18, reinicio real sobre `/data` con Ejecuciones no terminales/terminales + Eventos `Accepted` pendientes, limpieza Docker preservando `codenetix-tools`, limpieza `workspaces/`, y modo `Diagnostic`).

**Acceptance Criteria:**
- Given Ejecuciones no terminales y Sandboxes activos tras un reinicio del Host, when `StartupReconciler` ejecuta el arranque, then aplica el orden estricto de AD-18: migraciones → salud → Ejecuciones no terminales a `Failed(HostRestarted)` → limpieza de contenedores `codenetix.run` y volúmenes `codenetix-ws-*` (preservando `codenetix-tools`) → arranque del `Dispatcher` (FR-24).
- Given Eventos `Accepted` en `ing_events` que aún no tenían Ejecución antes del reinicio, when `StartupReconciler` finaliza y marca `Ready`, then el `Dispatcher` los procesa después del arranque.

## Implementation Notes

- **Retoma de trabajo parcial.** Se partió de cambios sin commitear de otro agente sobre `e69c549`. Se conservaron los contratos (`IRunStartupReconciler`, `ISandboxReconciler`, `SandboxCleanupSummary`), la estructura de `SandboxReconciler`; se corrigió lo siguiente:
  - **Causa raíz de los fallos intermitentes de la suite:** `CodeNetixFactory` sustituía `ISandboxManager`/`ISandboxHealthCheck` pero no el nuevo `ISandboxReconciler`, así que **cada arranque de un Host de pruebas ejecutaba la limpieza real contra Docker Desktop** y borraba los contenedores `codenetix.run` y volúmenes `codenetix-ws-*` que `SandboxIntegrationTests`/`SandboxSecurityFireTests` estaban usando en paralelo (`container is not running`, `409 marked for removal`…). Qué tests fallaban dependía del entrelazado, de ahí la variación. Arreglo: `FakeSandboxReconciler` registrado por defecto en la fábrica; solo el test de `Diagnostic` usa el `SandboxReconciler` real, contra un endpoint TCP cerrado. Los `UNIQUE constraint failed: gh_notifications…`/`run_events…` del log son violaciones esperadas y capturadas por las rutas de idempotencia existentes (`IssueNotifier` por `(runId, kind)`, reintento de `seq`, `dedup_key`), que EF registra igualmente; no se observó ningún doble `RunFailed` publicado (el test de reinicio comprueba exactamente una fila `Posted` por Ejecución).
  - **Reanudación de `Received` (1.6) eliminada:** el `Dispatcher` ya no orquesta al arrancar las Ejecuciones en `Received` (se quitó `ResumeReceivedRunsAsync` y `StuckRunRecovery.GetReceivedRunIdsAsync`); ahora las cierra `RunStartupReconciler` como `Failed(HostRestarted)`. `RunExecutionTests.Run_left_in_Received_by_a_crash_is_orchestrated_on_the_next_start` se sustituyó por `…_is_failed_as_HostRestarted_on_the_next_start_and_frees_the_object`.
  - `StartupReconciler`: dependencias ahora obligatorias (antes opcionales con `null`, lo que podía saltarse la reconciliación en silencio); los pasos 3 y 4 van protegidos para que un fallo inesperado se registre y nunca impida el arranque.
  - `RunStartupReconciler`: cierra cada Ejecución bajo el cerrojo por `runId` de `RunCancellationRegistry` (no se pisa con un `POST /cancel` concurrente; un `DbUpdateConcurrencyException`/UNIQUE se trata como "otra escritura la cerró" y se respeta); un fallo en una Ejecución se registra y no aborta el resto. Los avisos `RunFailed` se publican **después** de confirmar todos los cierres en SQLite, con 10 s por aviso y 30 s en total, para que GitHub caído no retrase el arranque de forma ilimitada.
  - `SandboxReconciler`: plazo total de 60 s (Docker colgado no bloquea el arranque); cada contenedor se detiene (gracia 5 s) antes de borrarlo; los volúmenes se listan todos y se filtran en cliente por prefijo exacto `codenetix-ws-` (sensible a mayúsculas), excluyendo siempre `codenetix-tools`.
  - `IRepositoryWorkspace.CleanupOrphansAsync`: se quitó la implementación por defecto de la interfaz (devolvía 0 y ocultaba un olvido). `RepositoryWorkspace` solo borra directorios con forma de `shortId` (8 hex en minúscula) que no sean enlaces/junctions: si `WorkspacesDirectory` apuntase por error a otro sitio, no se borra nada ajeno.
- **Sin test de reconciliación contra Docker real:** `CleanupOrphansAsync` borra los recursos `codenetix.run` de las Ejecuciones de la instancia, por lo que ejecutarlo contra Docker Desktop mataría los contenedores de las pruebas de Sandbox en paralelo (y los de una instancia real del usuario). La fila 3 se cubre con `FakeDockerEngine` (ampliado con `GET /containers/json`, `GET /volumes`, borrado de recursos listados y `FailingRemovals`).
- **Orden AD-18 (fila 1):** se verifica con espías en DI: un `IModuleDatabase` de orden máximo (fin de migraciones), `ISandboxHealthCheck`, decoradores de `IRunStartupReconciler`, `IRepositoryWorkspace` e `IAcceptedEventSource` (primera petición del Dispatcher), registrando además el estado de `IInstanceHealth` en cada paso (`Starting` hasta la limpieza, `Ready` en el Dispatcher). También se prueba que un fallo de migración aborta antes de salud y reconciliación.
- **Etapas abiertas:** las filas de `run_stages` sin `CompletedAt` de una Ejecución interrumpida no se cierran (igual que en los cierres por `StageError`/`Cancelled` existentes); fuera de alcance.
- **Parches de revisión y de la imagen Docker (tras la primera entrega):**
  - **D1 — contrato OpenAPI en la imagen:** la etapa `build` de `docker/codenetix.Dockerfile` no copiaba `panel/openapi/v1.json`, así que `OpenApiContractTests` no encontraba la instantánea. Se copia ese único archivo antes de los tests (el test no se salta ni cambia).
  - **D2 — `LivePanelHubTests.Reconnection_…` en Linux:** no era una pérdida ni un duplicado. La Ejecución recién creada sigue orquestándose mientras el test añade eventos, así que sus propios eventos se intercalan entre los del test (seq 12 → 18). El test descartaba los eventos en vivo que no esperaba y luego exigía que los `seq` fueran contiguos. Ahora guarda todo lo recibido en vivo hasta `lastSeenSeq` y lo fusiona con el catch-up `afterSeq`; además comprueba que SignalR no entrega dos veces el mismo `seq` (eso sí sería un fallo real). En Windows pasaba solo por temporización.
  - **R1 — Ejecución imposible de cerrar:** `CloseAsync` reintenta hasta `MaxCloseAttempts` (3) con un contexto nuevo cada vez; tras un conflicto solo acepta un estado que sea terminal de verdad al releerlo. El contrato devuelve `RunReconciliationResult(Closed, StillActive, Notified)`. Si `StillActive > 0`, o si el paso 3 lanza, `StartupReconciler` añade un motivo `Diagnostic` en lugar de `Ready`, de modo que el Dispatcher no marca como `Ignored` (`ObjectBusy`) los Eventos pendientes de ese objeto. Test con un trigger SQLite que aborta el `UPDATE` de una fila.
  - **R2+3 — avisos `RunFailed` perdidos:** cada arranque reintenta el aviso de toda Ejecución `Failed(HostRestarted)` sin evento `IssueCommented` y con menos de `MaxNotificationAttempts` (3) `NotificationFailed`, dentro del mismo plazo (10 s por aviso, 30 s en total). Cubre una caída entre cerrar y avisar, el plazo total agotado y el timeout tras crear el comentario. `IssueNotifier` es idempotente por (`runId`, `kind`) y adopta el comentario marcado, así que no duplica. El tope evita reintentar para siempre una issue borrada. La selección usa `run_events` (Pipeline no lee `gh_notifications`, AD-1); si la fila ya estaba `Posted`, el notificador lo devuelve sin llamar a GitHub y se registra el `IssueCommented` que faltaba. `IssueNotifier` guarda `Posted`/`Failed` con un token propio de 5 s, y ante el vencimiento del token del llamante marca `Failed` (`tiempo agotado…`) antes de relanzar, así que ninguna fila queda en `Pending`.
  - **R4 — solo recursos de esta instancia:** `SandboxReconciler` lee la etiqueta `codenetix.run` (`DockerClient.ListContainersAsync`/`ListVolumesAsync`, que devuelven el valor de la etiqueta) y, en un volumen sin etiqueta, el sufijo `<runId>` del nombre. Solo borra si `IRunReader.FindAsync` encuentra esa Ejecución en esta instancia y es terminal. Un `runId` desconocido (otra instancia que comparte el motor Docker) o aún activo se deja y se registra como warning. **Decisión:** esto precisa la regla "todos" de AD-18 para que una instancia nunca borre los Sandboxes de otra; el riesgo residual es que los recursos de una base de datos borrada quedan huérfanos hasta limpiarlos a mano.
  - **R5 — GitHub colgado / 5xx:** dos tests nuevos. (a) Con el POST de comentarios colgado, el arranque termina antes de 35 s (presupuesto 30 s + 5 s), cada Ejecución queda con `NotificationFailed(RunFailed)`, ninguna fila queda en `Pending`, la instancia está `Ready` y el Evento pendiente se despacha. En el arranque siguiente, con GitHub sano, cada aviso se publica exactamente una vez. (b) Con 502, el aviso se reintenta en cada arranque hasta 3 veces y después se deja; una Ejecución ya avisada no se vuelve a avisar.
  - **R6 — enlaces en `workspaces/`:** un enlace simbólico (o junction con `mklink /J` en Windows sin privilegio) llamado como un `shortId` y que apunta a un directorio centinela de fuera: el centinela y su archivo `ReadOnly` quedan intactos. Si el sistema no permite crear enlaces, el test se omite.
  - **R8:** los listados de volúmenes (y el nuevo de contenedores) ignoran las entradas sin `Name`/`Id` de tipo texto en lugar de lanzar.

## Spec Change Log

## Review Triage Log

Revisión 1 (Claude, revisor combinado, 10 hallazgos + build Docker):

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | medium | Una Ejecución que no se pudo cerrar deja el objeto ocupado y el Evento pendiente acaba `Ignored` | patch |
| 2 | medium | Caída entre cerrar y notificar → `RunFailed` nunca publicado, sin reintento | patch |
| 3 | medium | Timeout tras publicar deja la fila `Pending` aunque el comentario exista | patch (con 2) |
| 4 | medium | La limpieza borra Sandboxes de otra instancia en el mismo Docker | patch (filtrar por runs propios) |
| 5 | medium | Sin test de GitHub caído/colgado ni del límite de tiempo | patch |
| 6 | medium | Sin test de junction/symlink con nombre de shortId | patch |
| 7 | low | `WorkspacesDirectory` mal configurado puede borrar carpetas hex ajenas | reject |
| 8 | low | Un volumen sin `Name` aborta toda la limpieza de volúmenes | patch (1 línea) |
| 9 | low | Solo se prueba `PullRequestCreated` como terminal | reject |
| 10 | low | Aserciones débiles (sleep, stop fallido) | reject |
| D1 | high | `docker build`: `OpenApiContractTests` no encuentra `panel/openapi/v1.json` en la etapa sdk (fallo de 4.1, imagen nunca construida desde entonces) | patch |
| D2 | medium | `docker build`: `LivePanelHubTests.Reconnection…` inestable en Linux (12 esperado, 18 recibido) | patch (causa raíz) |
