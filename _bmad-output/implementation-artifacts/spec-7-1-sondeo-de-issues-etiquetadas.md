---
title: 'Historia 7.1: Sondeo de issues etiquetadas'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: 'c92b5b048ac473b88527addeb8aff554b6e107c9'
route: 'dispatch'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Hoy CodeNetix solo recibe trabajo a mano (`codenetix fix`, Panel) o desde el runner (`codenetix action`). Un desarrollador sin servidor público no puede automatizar: tiene que lanzar cada issue manualmente (FR-6).

**Approach:** Añadir a Ingestion un sondeo periódico (`BackgroundService`, solo en `serve`, desactivado por defecto) que, tras `Ready` y luego cada intervalo (5 minutos por defecto), lista las issues abiertas con la etiqueta configurada en los repositorios configurados, localiza en la API de eventos de cada issue el evento `labeled` más reciente de esa etiqueta y crea un Evento `Source=GitHub` con la clave `gh:<owner>/<repo>#<issue>:labeled:<labelEventId>` (AD-4), autorizando a quien puso la etiqueta (FR-26). Los datos de GitHub llegan por un puerto nuevo de Contracts implementado por el módulo GitHub (AD-1, AD-8), con peticiones condicionales (`ETag`) y respeto del rate limit.

## Boundaries & Constraints

**Always:**
- **Configuración de instancia (AD-10), solo la lee Host:**
  - `CODENETIX__Polling__Repositories`: lista `owner/repo` (cadena separada por comas o `;`, o `__0`, `__1`…; también lista YAML). Vacía (por defecto) → sondeo desactivado, sin ninguna llamada a GitHub. Como mucho 100 repositorios; duplicados (sin distinguir mayúsculas) se cuentan una vez.
  - `CODENETIX__Polling__Label`: por defecto `codenetix`; 1–50 caracteres, sin comas ni caracteres de control; comparada sin distinguir mayúsculas, como GitHub.
  - `CODENETIX__Polling__IntervalSeconds`: por defecto `300`, entre `60` y `86400`.
  - Valores inválidos → `Diagnostic` con un motivo en español que nombra la variable y no cita valores (`InstanceOptionsValidator`).
- **Puerto en Contracts** (`ILabeledIssueSource`), implementado solo por el módulo GitHub con `GitHubApiClient`; nunca lanza por errores de GitHub: devuelve un resultado con el motivo (sin secretos), si fue rate limit y cuánto esperar.
  - `ListOpenAsync`: `GET /repos/{o}/{r}/issues?state=open&labels=<label>&sort=updated&direction=desc&per_page=100`, paginado por `Link: rel="next"` con tope de 10 páginas, descartando elementos con `pull_request`; devuelve número y `updated_at` de cada issue. La primera página se pide con `If-None-Match` del `ETag` anterior: `304` → "sin cambios" (no consume rate limit).
  - `FindLatestLabelEventAsync`: el evento `labeled` **más reciente** con esa etiqueta en `GET /repos/{o}/{r}/issues/{n}/events?per_page=100`, recorriendo desde la última página (`rel="last"`) hacia atrás con tope de 5 páginas; devuelve `id`, `actor.login` (puede faltar) y `created_at`. Se usa la API de eventos de la issue y no la de timeline: trae solo eventos (sin comentarios ni referencias), los mismos `id` y será la que use el webhook (7.2) para obtener el `labelEventId`, que su payload no trae.
  - Rate limit = `429`, o `403` con `x-ratelimit-remaining: 0` o con `retry-after` (límite secundario); la espera sale de `retry-after` o de `x-ratelimit-reset`.
- **Clave compartida:** `GitHubLabelEventKey.Format(owner, repo, number, labelEventId)` en Contracts (owner/repo en minúsculas), para que la 7.2 use exactamente la misma.
- **Sondeo (Ingestion, `Features/PollGitHub/`):**
  - Sin repositorios no espera a nada ni llama a GitHub. Con repositorios, espera a `IInstanceHealth.WhenReadyAsync`; en `Diagnostic` no sondea nunca (AD-18). Primer ciclo inmediato al estar `Ready`; después, cada intervalo; nunca dos ciclos solapados.
  - Por cada issue listada: si su `updated_at` coincide con el ya procesado con éxito en este proceso → nada. Si no, evento `labeled` más reciente; sin él → nada (se vuelve a mirar cuando cambie la issue). Con él, clave AD-4: si ya existe en `ing_events` → nada (ni siquiera se consulta el permiso).
  - **Otra Fuente ya lo lanzó:** si hay un Evento `Accepted` del mismo objeto recibido después de la etiqueta (p. ej. `codenetix fix`), el Evento se guarda `Ignored` con `"ya lanzada por otra Fuente después de etiquetarla"` y sin consultar el permiso: la etiqueta ya se consumió y no se duplica la Ejecución.
  - Si no: actor del evento `labeled` vía `IRepositoryPermissions` (FR-26); `Granted` → `Accepted` + `IEventDispatchSignal.Notify()`; `Denied` o sin actor → `Ignored` con `"no autorizado"`; `Unavailable` → no se persiste nada y se reintenta en el siguiente ciclo (falla cerrado).
  - `ObjectKey` = `GitHubObjectKey.Format` en minúsculas, el mismo que el resto de Fuentes: el Dispatcher decide (cola, o `Ignored` si el objeto sigue ocupado). Ingestion nunca crea Ejecuciones.
  - Carrera con el índice único de `dedup_key` → se trata como existente (patrón de `ManualEventIngestion`).
  - El `ETag` de un repositorio solo se guarda si su ciclo terminó sin pendientes (ningún `Unavailable`): un fallo nunca se "olvida" con un `304`.
  - Un repositorio que falla no impide sondear los demás. Un rate limit corta el ciclo (el límite es del token, compartido) y el siguiente se aplaza hasta el `reset`, entre el intervalo y 1 hora.
  - Los fallos se registran con log (motivo fijo del adaptador, pasando por el redactor); el servicio nunca muere por una excepción de un ciclo.
  - Payload persistido: solo `repository`, `issueNumber`, `label`, `labelEventId`, `labeledAt`; nunca el JSON de GitHub ni el token.
- **Solo en `serve`:** `codenetix action` y `codenetix fix` no registran el sondeo.
- **README:** sección "Sondeo de issues etiquetadas" con las tres variables y el comportamiento (desactivado por defecto, autorización de quien etiqueta, reetiquetar para relanzar, issues ya etiquetadas al activarlo).
- **Pruebas** en `tests/CodeNetix.Host.Tests` con `CodeNetixFactory` y `FakeGitHub` (sin red, sin `:5000`/`:5001`): una prueba por fila de la matriz, más el adaptador (paginación, PR, `labeled` más reciente, `304`, rate limit).

**Ask First:**
- Cambiar el esquema de `ing_events` (nueva migración) o modificar el Dispatcher.

**Never:**
- Crear Ejecuciones desde Ingestion (solo el Dispatcher, AD-4).
- Referenciar el módulo GitHub desde Ingestion (AD-1; solo Contracts).
- Sondear en `Diagnostic`, en `codenetix action` o sin repositorios configurados.
- Registrar o persistir el token o el JSON de GitHub.
- Implementar webhooks (7.2) ni Sentry (7.3/7.4).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Issue nueva etiquetada | `o/r#12` abierta con `codenetix`; evento `labeled` id 900 de `alice` con `write` | Un Evento `Accepted`, `Source=GitHub`, clave `gh:o/r#12:labeled:900`, objeto `github:o/r#12`, actor `alice`; el Dispatcher crea la Ejecución | — |
| Ciclos repetidos | Mismo estado en el siguiente ciclo | Primera página con `If-None-Match` → `304`; ni eventos, ni permiso, ni Evento nuevo | — |
| Ejecución en curso | La Ejecución de `#12` sigue no terminal y GitHub ya no devuelve `304` | Misma clave → ningún Evento nuevo ni consulta de permiso | — |
| Lanzada por otra Fuente | `codenetix fix` de `#12` aceptado después de la etiqueta | Evento `Ignored` (`ya lanzada por otra Fuente…`), sin permiso ni segunda Ejecución | — |
| Reetiquetado | Se quita y se vuelve a poner la etiqueta (evento 950) | Nuevo Evento `gh:o/r#12:labeled:950` `Accepted`; lo despacha el Dispatcher | — |
| Sin permiso | `alice` con `read`, o evento sin actor | Evento `Ignored` con `"no autorizado"`, sin Ejecución | — |
| GitHub no responde | `5xx` al listar, al leer eventos o al consultar el permiso | Nada persistido ni `ETag` guardado; los demás repositorios siguen; el siguiente ciclo lo crea | Log con el motivo, sin token |
| Rate limit | `403` + `x-ratelimit-remaining: 0` | Nada persistido; el ciclo se corta; el siguiente espera al `reset` (entre intervalo y 1 h) | Log redactado |
| PR etiquetado | Elemento con `pull_request` | Se descarta | — |
| Sin evento `labeled` | La etiqueta no aparece en los eventos (p. ej. renombrada) | Nada; no se vuelve a pedir hasta que cambie la issue | — |
| Modo `Diagnostic` | Falta la key del Proveedor | Ningún ciclo, ninguna llamada a GitHub | — |
| Sin repositorios | Por defecto | Sondeo desactivado, ninguna llamada | — |
| Configuración inválida | `IntervalSeconds=10`, repo `foo`, etiqueta con coma | `Diagnostic` con un motivo por variable | — |
| Modo `action` | Repositorios de sondeo configurados | No se registra el sondeo | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/LabeledIssues.cs` (nuevo) -- `ILabeledIssueSource`, resultados, `GitHubLabelEventKey`.
- `src/CodeNetix.GitHub/GitHubApiClient.cs` -- `If-None-Match`, `ETag`, espera de rate limit y límite secundario.
- `src/CodeNetix.GitHub/Features/ListLabeledIssues/LabeledIssueSource.cs` (nuevo) + `GitHubLinkHeader` y registro en `GitHubServiceCollectionExtensions.cs`.
- `src/CodeNetix.Ingestion/Features/PollGitHub/` (nuevo) -- `GitHubPollingOptions`, `GitHubPoller` (un ciclo), `GitHubPollingService` (`BackgroundService`); `AddCodeNetixGitHubPolling`.
- `src/CodeNetix.Host/Configuration/InstanceOptions.cs`, `InstanceOptionsValidator.cs`, `CodeNetixComposition.cs`, `ServeMode.cs` -- sección `Polling`, lectura de la lista, validación y registro solo en `serve`.
- `README.md` -- sección de sondeo.
- `tests/CodeNetix.Host.Tests/FakeGitHub.cs`, `GitHubPollingTests.cs` (nuevo).

## Tasks & Acceptance

**Execution:**
- [x] `CodeNetix.Contracts` -- puerto `ILabeledIssueSource` y clave compartida.
- [x] `CodeNetix.GitHub` -- peticiones condicionales y rate limit en `GitHubApiClient`; `LabeledIssueSource` con paginación y topes.
- [x] `CodeNetix.Ingestion` -- `GitHubPoller` (deduplicación, otra Fuente, FR-26, `ETag`, aislamiento, backoff) y `GitHubPollingService` (desactivado, `Ready`, intervalo).
- [x] `CodeNetix.Host` -- opciones `Polling`, validación, mapeo a `GitHubPollingOptions` y registro solo en `serve`; README.
- [x] `tests/CodeNetix.Host.Tests` -- `FakeGitHub` con listado, eventos, `ETag` y permisos por login; una prueba por fila.

**Acceptance Criteria:**
- Given repositorios configurados para el sondeo, when pasa el intervalo (5 minutos por defecto), then Ingestion consulta el evento `labeled` de las issues abiertas con la etiqueta y crea Eventos con la clave `gh:<owner>/<repo>#<issue>:labeled:<labelEventId>` (FR-6, AD-4).
- Given una issue con una Ejecución no terminal, when se sondea de nuevo, then no se genera otra; y reetiquetarla sí genera un Evento nuevo.
- Given la instancia en modo `Diagnostic`, when pasa el intervalo, then el sondeo no se ejecuta.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde (dos veces seguidas).

## Implementation Notes

- **Puerto y adaptador:** `ILabeledIssueSource` (Contracts, `LabeledIssues.cs`) devuelve resultados (`Ok`/`NotModified`/`Unavailable`, `Found`/`NotFound`/`Unavailable`) con `GitHubQueryFailure(Reason, RateLimited, RetryAfter)`; nunca lanza. `LabeledIssueSource` (GitHub, `Features/ListLabeledIssues/`) pagina por número de página (nunca sigue la URL del `Link`) con `GitHubLinkHeader` (`rel="next"`/`rel="last"`). `IssueNotifier` conserva su propio `LastPage` (sin refactor). El `ETag` solo se devuelve si la lista cabe en una página (con varias, un `304` de la primera no garantiza nada de las demás); más de 10 páginas → `Truncated`, sin `ETag` y con aviso en el log. Eventos de una issue con `404/410` → `NotFound` (borrada o transferida), no indisponible.
- **API de eventos vs timeline:** se usa `GET /issues/{n}/events`: solo eventos, mismos `id` y lo que necesitará la 7.2, cuyo payload de webhook no trae el id del evento `labeled`. El evento más reciente está en la última página: se pide la primera para leer `rel="last"` y se recorre hacia atrás (máx. 5 páginas).
- **`GitHubApiClient`:** nuevo `GetAsync(path, ifNoneMatch, ct)` (`If-None-Match`); `GitHubResponse` lleva `ETag` y `RetryAfter`; `304` se devuelve sin intentar leer cuerpo. Rate limit ahora también es `403` con `retry-after`, o `403` cuyo `message` menciona `secondary rate limit` (límite secundario, con espera mínima de 60 s); la espera sale de `retry-after` (segundos o fecha) o de `x-ratelimit-reset` (en el pasado → 0). Cambio compatible: los demás llamantes no usan los campos nuevos.
- **Ahorro de rate limit:** dos niveles. `ETag` por repositorio en memoria (con `sort=updated&direction=desc`, cualquier issue etiquetada o reetiquetada sube a la primera página y cambia el `ETag`; un `304` no cuenta en el límite). Y, por issue, el `updated_at` de la última vez que se resolvió con éxito: si no ha cambiado no se piden sus eventos. Tras reiniciar se pierde la caché y la deduplicación la da `ing_events` (un ciclo completo de eventos, sin consultas de permiso).
- **Pendientes nunca tapados:** el `ETag` solo se guarda si el repositorio terminó sin pendientes; un `Unavailable` al listar deja el repositorio para el siguiente ciclo; un fallo de una issue (eventos, permiso o excepción) la deja pendiente, el repositorio sin `ETag` y **se sigue con las demás issues** (un 5xx permanente en una issue no bloquea a las más antiguas); solo un rate limit corta el repositorio. Los demás repositorios siguen. Un rate limit corta todo el ciclo (el límite es del token) y `GitHubPollingService.Backoff` espera `clamp(retryAfter, intervalo, max(intervalo, 1 h))`.
- **Interacción con otras Fuentes:** el Dispatcher es de concurrencia 1 (espera a cada orquestación), así que un Evento de sondeo de una issue que ya se está ejecutando no se marca `ObjectBusy`: queda en cola y se ejecutaría después. Para no duplicar trabajo cuando el operador ya lanzó la issue a mano después de etiquetarla, si existe un Evento `Accepted` de otra Fuente (`Source != GitHub`) del mismo objeto con `received_at >= created_at` del evento `labeled`, el Evento del sondeo se guarda `Ignored` (`ya lanzada por otra Fuente después de etiquetarla`) sin consultar permiso. Una etiqueta puesta después de un `fix` sí cuenta (es un disparo nuevo). Webhooks (7.2) comparten clave, así que se deduplican por el índice único (la carrera se trata como existente).
- **FR-26:** se autoriza a `actor.login` del evento `labeled`. GitHub muestra a los usuarios borrados como `ghost`, que no es colaborador (permiso `404` → `Denied`) → `Ignored` "no autorizado". Un actor ausente o con un login no plausible (defensivo; GitHub no lo devuelve así) → `Ignored` "no autorizado" sin consultar.
- **Configuración:** `PollingInstanceOptions` en `InstanceOptions`; la lista se lee en crudo con un `PostConfigure<IConfiguration>` (el binder no separa una cadena con comas), en la composición común para que `Validate` vea lo mismo en `serve` y `action`. La validación se aplica también en `action` (un valor inválido es inválido en cualquier modo), pero solo `serve` llama a `AddGitHubPolling` (registra `AddCodeNetixGitHubPolling` de Ingestion y mapea a `GitHubPollingOptions`: owner/repo en minúsculas, sin duplicados). Etiqueta vacía o solo espacios = por defecto.
- **Servicio:** desactivado = `return` inmediato, sin esperar a `Ready` ni tocar GitHub. Con repositorios, `WhenReadyAsync` (en `Diagnostic` no completa nunca), ciclo inmediato y `Task.Delay(intervalo, TimeProvider)`. `GitHubPoller` serializa ciclos con un `SemaphoreSlim` (también frente a llamadas directas de las pruebas) y expone `CompletedCycles`.
- **Logs:** mensajes fijos en español con repositorio, número, id de Evento, actor y el motivo del adaptador (nunca cuerpos de GitHub ni el token); pasan además por `RedactingConsoleFormatter`. La prueba de indisponibilidad captura los logs **sin redactar** y comprueba que el token no aparece.
- **Ingestion:** añade referencias explícitas a `Microsoft.Extensions.Hosting.Abstractions`, `Logging.Abstractions` y `Options` (como Pipeline). Sin migraciones ni cambios en el Dispatcher (nada de "Ask First"). Sin cambios de API ni de OpenAPI (no aplica `check:api`) ni de la imagen.
- **Pruebas (`GitHubPollingTests`, 31 tests / 43 casos; + 1 en `ActionModeTests`):** el primer ciclo lo lanza el servicio al estar `Ready` (intervalo de 5 min, no vence en la prueba) y los siguientes se lanzan con `GitHubPoller.RunCycleAsync`; el vencimiento del intervalo se prueba sobrescribiendo `GitHubPollingOptions.Interval` a 150 ms. `FakeGitHub` gana listado con filtro por etiqueta, `ETag`/`304`, paginación con `Link`, eventos de issue, permiso por login y `x-ratelimit-reset`.
- **Verificación:** `dotnet build CodeNetix.slnx` 0 advertencias; `dotnet test --solution CodeNetix.slnx` en verde dos veces seguidas (1063 tests; la primera con 1062 correctos y 1 omitido, la segunda 1063/1063). Tras los parches de la revisión 1: 1078 tests, en verde dos veces (1077 + 1 omitido —la prueba de fuego del Sandbox real, que a veces se omite sin Docker disponible— y 1078/1078).
- **Parches de la revisión 1:**
  1. `GitHubPoller`: un fallo de una issue que no es rate limit ya no corta el repositorio: queda pendiente (sin `ETag`) y se procesan las demás; las excepciones por issue también se capturan por issue. Test: eventos de `#20` siempre `502`, `#12` (más antigua) se acepta y en el siguiente ciclo solo se reintenta `#20`.
  2. Test: un `fix` anterior a la etiqueta y una etiqueta sin `created_at` no suprimen el Evento del sondeo (ambos `Accepted`, con consulta de permiso).
  3. Costura `IGitHubPollCycle` (la implementa `GitHubPoller`) para probar `GitHubPollingService` con un `TimeProvider` manual: tras un rate limit de 20 min no hay ciclo a los 5 min ni a 19:59, sí a los 20 min; con 5 h se acota a 1 h.
  4. Tests del adaptador: más de 10 páginas → `Truncated` sin `ETag` (y aviso del poller), tope de 5 páginas de eventos (`1, 7, 6, 5, 4, 3`), `404/410` → `NotFound`, `429`, `retry-after` en segundos y fecha HTTP, mensaje de límite secundario (60 s), `x-ratelimit-reset` pasado → 0, y un `403` normal que no es rate limit.
  5. `LabeledIssueSource`: `ETag` solo con lista de una página (test con 150 issues → `null`; con 1 issue → `304`).
  7. `GitHubApiClient`: `403` con `secondary rate limit` en el `message` → rate limit con espera mínima `SecondaryRateLimitMinimumWait` (60 s).
  8. README: renombrar/transferir el repositorio y actualizar la configuración relanza las issues abiertas etiquetadas.
  9. Sin evento `labeled`: aviso `Warning` una vez por (issue, `updated_at`) (`WarnedWithoutLabelEvent` por repositorio, podado como `Processed`).
  10. Test con actor `ghost` (permiso `404`) → `Ignored` "no autorizado"; nota de FR-26 corregida.
  11. `GitHubPoller.Dispose` idempotente con bandera; solo libera el semáforo si no hay un ciclo en curso; después `RunCycleAsync` lanza `ObjectDisposedException`.
  - El hallazgo 6 (rate limit en la consulta de permisos sin backoff) queda diferido: `IRepositoryPermissions` no expone si fue rate limit; la issue queda pendiente y el repositorio se reintenta en el siguiente ciclo.
- **Riesgos / pendientes:**
  - La caché (`ETag`, `updated_at`) es en memoria: tras cada reinicio se piden de nuevo los eventos de todas las issues etiquetadas (una o dos peticiones por issue).
  - Al activar el sondeo se procesan también las issues que ya llevaban la etiqueta (documentado en el README).
  - La regla "lanzada por otra Fuente" compara el reloj del host con `created_at` de GitHub: un desfase grande podría duplicar o suprimir un disparo en el borde.
  - No probado contra GitHub real (solo `FakeGitHub`).

## Spec Change Log

## Review Triage Log

Revisión 1 (Claude, revisor combinado, 11 hallazgos):

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | medium | Un fallo persistente (no rate limit) en una issue corta el repo y bloquea las issues más antiguas indefinidamente | patch |
| 2 | medium | Sin test de que un `fix` anterior al etiquetado no suprime la etiqueta | patch |
| 3 | medium | El bucle del servicio no prueba que espera el backoff | patch |
| 4 | medium | Comportamientos del adaptador sin test (truncado, tope de eventos, 404/410, 429/retry-after, reset pasado) | patch |
| 5 | medium | ETag de la página 1 guardado con listas multipágina → issue saltada que nunca se reprocesa | patch |
| 6 | low | Rate limit en la comprobación de permisos no activa el backoff | defer |
| 7 | low | Límite secundario 403 sin cabeceras no se trata como rate limit | patch |
| 8 | low | Renombrar/transferir el repo relanza las issues etiquetadas | patch (documentar en README) |
| 9 | low | Etiqueta renombrada o >500 eventos: solo log Debug | patch (Warning una vez) |
| 10 | low | Usuario borrado llega como `ghost`, no `null`; sin test | patch |
| 11 | low | `Dispose` del semáforo durante un ciclo en curso | patch |
