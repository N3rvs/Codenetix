---
title: 'Historia 6.1: Modo `codenetix action`'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: '606f1407c2e3bb69117ef35a60309eea93c28fd2'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** El binario solo tiene `serve`, `fix` y `healthcheck`. No hay forma de ejecutar la factoría dentro de un job de GitHub Actions: nadie convierte el evento del runner (`GITHUB_EVENT_PATH`) en un `EventEnvelope{Source=GitHubAction}`, no hay autorización FR-26 para ese actor, y la traza solo es visible en el Panel, que en un runner no existe (FR-25, AD-12).

**Approach:** Nuevo modo `codenetix action` en Host que reutiliza la composición de `serve` (Persistence, Ingestion, Pipeline, GitHub, Agents, Sandbox, Egress Gateway, `StartupReconciler`) sin Panel, API, hub ni webhooks, sobre un `DataDirectory` temporal. Host lee el entorno del runner; Ingestion normaliza el evento `issues/labeled` con la etiqueta de disparo, autoriza al `sender` (FR-26) y persiste el Evento; el Dispatcher crea y orquesta la Ejecución como siempre; Host vuelca sus `RunEvent` a la salida estándar del job, espera a que el orquestador termine (avisos y liberación del Sandbox incluidos) y sale con un código según el Estado final.

## Boundaries & Constraints

**Always:**
- **Entorno (solo Host lo lee):** `GITHUB_EVENT_NAME` y `GITHUB_EVENT_PATH` (obligatorios), `GITHUB_REPOSITORY`, `GITHUB_RUN_ID`, `GITHUB_RUN_ATTEMPT`, `GITHUB_ACTIONS`, `GITHUB_TOKEN`, `GITHUB_API_URL` y `GITHUB_SERVER_URL`, más la Configuración de instancia de siempre (`CODENETIX__*` y archivo YAML opcional).
  - Token: `CODENETIX__GitHub__Token` o, si falta, `GITHUB_TOKEN`. API y base de clonado: `CODENETIX__GitHub__ApiUrl`/`CloneBaseUrl` o, si faltan, `GITHUB_API_URL`/`GITHUB_SERVER_URL` (GitHub Enterprise sin configuración extra).
  - Con `GITHUB_TOKEN` (token de instalación, sin `GET /user`) la identidad del bot es `github-actions[bot]` salvo `CODENETIX__GitHub__Login`; nunca se llama a `GET /user` en ese caso.
  - Etiqueta de disparo: `CODENETIX__Action__Label` (por defecto `codenetix`, sin distinguir mayúsculas como GitHub).
  - `DataDirectory`: `CODENETIX__Persistence__DataDirectory` o, si falta, un directorio nuevo `codenetix-action-<guid>` bajo `RUNNER_TEMP` (o el temporal del sistema) que se borra al terminar (mejor esfuerzo).
- **Normalización y autorización en Ingestion (AD-4, FR-26):** contrato nuevo en Contracts (`IGitHubActionEventIngestion`) implementado en `CodeNetix.Ingestion`:
  - `Parse` (pura, sin E/S) acepta solo `issues` con `action: labeled`, etiqueta igual a la de disparo, `issue.number > 0`, issue abierta y que no sea un PR; `repository.full_name` y `sender.login` válidos. Cualquier otro evento es `NotApplicable` (con el motivo); un JSON ilegible o incompleto, o un `GITHUB_REPOSITORY` distinto del repositorio del evento, es `Invalid`.
  - `SubmitAsync` comprueba el permiso de escritura del `sender` (no de la identidad del token) con `IRepositoryPermissions`: sin permiso → Evento `Ignored` (`no autorizado`); GitHub no responde → nada persistido (falla cerrado); con permiso → `Accepted` y aviso al Dispatcher. Nunca crea Ejecuciones.
  - `EventEnvelope`: `Source=GitHubAction`, `ObjectKey=github:<owner>/<repo>#<n>` (minúsculas), `Actor=<sender>`, `DedupKey=gh-action:<owner>/<repo>#<n>:run:<GITHUB_RUN_ID>:<GITHUB_RUN_ATTEMPT>` (idempotente), `Payload` solo con datos del disparo (evento, repositorio, número, etiqueta, run); nunca el token ni el cuerpo del evento.
- **Mismo Pipeline, Sandbox y Gateway (AD-12):** misma composición que `serve` (extraída a un punto común, no duplicada) y mismo `StartupReconciler` (AD-18). Kestrel solo escucha el Egress Gateway (`:8080`/`/llm` con la guarda de puertos); el proxy `CONNECT` de `:8081` sigue igual. Sin `:5000`, `:5001`, Panel, `/api`, `/hubs` ni OpenAPI.
- **Salida del job:**
  - stdout: una línea por `RunEvent`, en orden de `seq` y sin duplicados, con el formato de `fix --follow` (`[n] Tipo …`); `OutputAppended` imprime su texto (ya redactado por el sumidero); al final, una línea `Resultado: …` con el Estado, el motivo o la URL del PR.
  - Logs del host a stderr (con el formateador que redacta).
  - Con `GITHUB_ACTIONS=true`, todo stdout va entre `::stop-commands::<token aleatorio>` y `::<token>::`: la salida del repositorio no puede inyectar comandos de workflow.
- **Fin y códigos de salida:** se espera a que el orquestador termine con la Ejecución (aviso en la issue y Sandbox liberado), no solo al Estado terminal.
  - `0`: `PullRequestCreated`, o evento que no aplica (mensaje con el motivo, sin arrancar el host, sin Docker ni GitHub).
  - `1`: `Failed`/`Cancelled`, Evento `Ignored`, GitHub no responde, `Diagnostic` (motivos en stderr), fallo de arranque o job interrumpido.
  - `2`: uso: fuera de GitHub Actions, argumentos extra, archivo de evento ilegible/inválido o repositorio que no coincide.
- **Pipeline:** el Dispatcher avisa a los `IRunCompletionObserver` registrados (Contracts) cuando termina de orquestar una Ejecución, también si el orquestador lanza; un observador que falla no detiene el Dispatcher.
- Tests en `tests/CodeNetix.Host.Tests` con fakes (GitHub, chat, Sandbox): un test por fila de la matriz, sin Docker, sin red y sin enlazar `:5000`/`:5001`/`:8080`.

**Never:**
- Imprimir secretos (token, keys) en stdout, stderr o logs; guardar el JSON del evento o el token en `ing_events`.
- Saltarse la autorización FR-26 (el modo Action no tiene firma, pero sí permiso de escritura) ni crear Ejecuciones fuera del Dispatcher.
- Abrir los puertos del Panel o de webhooks, o servir `/api`.
- Dispararse con eventos de PR (`pull_request_target`, `workflow_run`) o cualquier otro distinto de `issues/labeled`.
- Construir `action.yml` (Historia 6.2).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Issue etiquetada por quien puede escribir | `issues/labeled` con `codenetix`, `sender` con `write`, revisores aprueban | Evento `GitHubAction` `Accepted`, una Ejecución hasta `PullRequestCreated`, `RunEvent` en stdout por `seq` y `Resultado: PullRequestCreated … <url>`, aviso en la issue publicado antes de salir, directorio temporal borrado | exit 0 |
| Ejecución fallida | Igual, pero el Analista declara la issue no automatizable | `[n] StateChanged Analyzing → Failed (NotAutomatable: …)`, `Resultado: Failed (NotAutomatable)` | exit 1 |
| Evento que no aplica | `push`; `issues/opened`; `issues/labeled` con otra etiqueta; issue cerrada | Mensaje "nada que hacer: <motivo>" | exit 0, sin arrancar el host ni llamar a GitHub |
| Actor sin permiso | `sender` con `read` | Evento `Ignored` (`no autorizado`) sin Ejecución; stderr nombra al actor y el repositorio | exit 1 |
| GitHub no responde | Permiso → 502 | Nada persistido; stderr con el motivo | exit 1 |
| Fuera de Actions o evento inválido | Sin `GITHUB_EVENT_PATH`; archivo inexistente o JSON roto; `GITHUB_REPOSITORY` distinto | Mensaje de uso en stderr | exit 2 |
| Instancia en `Diagnostic` | Falta la key del Proveedor | Motivos en stderr, sin Evento ni Ejecución | exit 1 |
| Credenciales del runner | Solo `GITHUB_TOKEN` + `GITHUB_API_URL` | GitHub recibe ese token; nunca `GET /user`; el token no aparece en stdout ni stderr | — |
| Comandos de workflow | `GITHUB_ACTIONS=true` | stdout empieza por `::stop-commands::<token>` y termina con `::<token>::` | — |
| Job interrumpido | Cancelación mientras el Programador trabaja | El host se detiene; stderr "interrumpida" | exit 1 |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/EventEnvelope.cs` -- `IGitHubActionEventIngestion` y sus tipos (`GitHubActionEventInput`, `GitHubActionTrigger`, resultado de `Parse`).
- `src/CodeNetix.Contracts/EventDispatch.cs` -- `IRunCompletionObserver`.
- `src/CodeNetix.Ingestion/Features/SubmitGitHubActionEvent/` (nuevo) -- `GitHubActionEventIngestion` (parseo, autorización, persistencia); registro en `IngestionServiceCollectionExtensions`.
- `src/CodeNetix.Pipeline/Features/DispatchEvents/Dispatcher.cs` -- avisar a los observadores al terminar cada orquestación.
- `src/CodeNetix.GitHub/GitHubOptions.cs`, `GitHubIdentity.cs` -- `Login` configurado sin `GET /user`.
- `src/CodeNetix.Host/CodeNetixComposition.cs` (nuevo), `ServeMode.cs` -- extraer la composición común.
- `src/CodeNetix.Host/ActionMode.cs` (nuevo), `CodeNetixCommandLine.cs` -- el modo, su entorno, la salida y los códigos.
- `tests/CodeNetix.Host.Tests/ActionModeTests.cs` (nuevo) -- una prueba por fila.

## Tasks & Acceptance

**Execution:**
- [x] `CodeNetix.Contracts` -- `IGitHubActionEventIngestion` (+ tipos) e `IRunCompletionObserver`.
- [x] `CodeNetix.Ingestion` -- `GitHubActionEventIngestion`: `Parse` puro y `SubmitAsync` con FR-26 sobre el `sender`, `Source=GitHubAction`, clave idempotente y aviso al Dispatcher.
- [x] `CodeNetix.Pipeline` -- el Dispatcher avisa a los observadores tras cada orquestación.
- [x] `CodeNetix.GitHub` -- `GitHubOptions.Login` evita `GET /user`.
- [x] `CodeNetix.Host` -- composición común extraída de `serve`; `ActionMode` (entorno, directorio temporal, solo Gateway, `StartupReconciler`, volcado de `RunEvent`, `stop-commands`, códigos de salida); `action` en el despacho y en el uso.
- [x] `tests/CodeNetix.Host.Tests/ActionModeTests.cs` -- una prueba por fila de la matriz.

**Acceptance Criteria:**
- Given un runner con Docker y una issue etiquetada `codenetix` por un actor con escritura, when se ejecuta `codenetix action`, then se construye un `EventEnvelope{Source=GitHubAction}` desde `GITHUB_EVENT_PATH` que pasa por Ingestion con la autorización de FR-26 (AD-12).
- Given ese Evento aceptado, when el Dispatcher lo orquesta, then se usan el mismo Pipeline, Sandbox y Gateway sobre una SQLite temporal y los `RunEvent` se vuelcan a la salida estándar del job.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde.

## Implementation Notes

- **Composición común (`CodeNetixComposition.AddCodeNetixServices`):** se movió de `ServeMode` todo lo que no es propio del Panel (módulos, opciones, resolución de agentes, precios, redactor, logs, `InstanceHealth`, `AdminTokenStore` y `StartupReconciler`). `serve` añade sus puertos, `AccessOptionsValidator`, SignalR, `RunsHubEventPublisher`, OpenAPI y endpoints; `action` solo `WebhookPortGuardMiddleware` + `MapLlmGateway` en `http://+:<LlmGatewayPort>` (8080). El orden de registro de los servicios alojados (Dispatcher, proxy `CONNECT`, `StartupReconciler`) no cambia. `StartupReconciler` también escribe `admin.token` en el directorio temporal: inofensivo y evita bifurcar el arranque AD-18.
- **Orden de `action`:** leer entorno → leer el archivo del evento (≤ 25 MB) → completar la configuración con los valores del runner (solo lo que `CODENETIX__*`/YAML no fija) → construir el host → `Parse` (puro) → si no aplica, salir con 0 **sin arrancar** (ni Docker, ni GitHub, ni base de datos) → `StartAsync` (AD-18 completo) → si no está `Ready`, motivos y 1 → `SubmitAsync` (FR-26) → esperar `runId` en `IEventReader` → volcar `IRunReader.GetEventsAsync(afterSeq)` hasta que el Dispatcher avisa del fin de la orquestación, con un drenaje final → `Resultado:` → `StopAsync` (30 s) y borrar el directorio temporal.
- **Volcado por sondeo, no por el publicador:** se lee `run_events` (fuente de verdad, AD-3) cada 250 ms en lugar de engancharse a `IRunEventPublisher`: orden por `seq`, sin huecos ni duplicados aunque un `PublishAsync` falle. En `action` queda el `NullRunEventPublisher` de Pipeline.
- **Fin = orquestación terminada, no Estado terminal:** el aviso en la issue (`NotifyTerminalAsync`), `SandboxReleased` y el borrado del árbol ocurren después del `StateChanged` terminal; salir en ese momento cortaría el comentario. Decisión: `IRunCompletionObserver` en Contracts, que el Dispatcher invoca en un `finally` tras orquestar (también si el orquestador lanza); `ActionRunCompletion` lo implementa en Host. Un observador que lanza se registra y no afecta al bucle.
- **FR-26 sobre `sender`:** la autorización es la de quien puso la etiqueta, no la del token (que en Actions es siempre el bot). La consulta `collaborators/{login}/permission` funciona con `GITHUB_TOKEN`. Etiqueta comparada sin mayúsculas, como GitHub.
- **`GITHUB_TOKEN` y `GET /user`:** un token de instalación recibe 403 en `GET /user`, que `IssueNotifier` usa para reconocer sus comentarios marcados. `GitHubOptions.Login` (nuevo) fija la identidad sin llamar a GitHub; `action` pone `github-actions[bot]` si el token viene de `GITHUB_TOKEN` y no hay `CODENETIX__GitHub__Login`. `GitHubIdentity` conserva el constructor de un parámetro que usan los tests.
- **Clave de deduplicación:** el evento del runner no trae el id del evento `labeled` del timeline (AD-4 `gh:…:labeled:<id>`), así que se usa `gh-action:<owner>/<repo>#<n>:run:<GITHUB_RUN_ID>:<GITHUB_RUN_ATTEMPT>`: un reintento del job es un Evento nuevo y una segunda llamada en el mismo intento, el mismo. Sin `GITHUB_RUN_ID`, una clave aleatoria (`:local:<guid>`).
- **Códigos de salida:** un actor sin permiso sale con 1 (como `fix`), no con 0: el mantenedor ve en rojo que alguien sin escritura intentó disparar trabajo. "Nada que hacer" es 0 para que un workflow con `on: issues: [labeled]` no falle por otras etiquetas.
- **Comandos de workflow:** con `GITHUB_ACTIONS=true` se emite `::stop-commands::<guid>` antes de cualquier salida y `::<guid>::` en un `finally`; el token es aleatorio y el código del repositorio no lo ve. Todo stdout/stderr del modo pasa por `SecretRedactor`, construido con la configuración ya completada (incluye el `GITHUB_TOKEN` copiado a `CODENETIX:GitHub:Token`); los logs del host van a stderr (`LogToStandardErrorThreshold = Trace`) con el formateador que redacta, y `Microsoft.Hosting.Lifetime`/`Microsoft.AspNetCore` solo en `Warning`.
- **Formato:** el de `fix --follow` (`FixMode.FormatEvent`) más `OutputAppended` (una línea `[n] stdout | …` por línea de texto), `OutputTruncated`, `TokensConsumed`, `IssueCommented`, `NotificationFailed`, `EgressBlocked`, `CodingAttemptFailed` y `AnalysisRejected`. `fix` no cambia.
- **Directorio temporal:** `RUNNER_TEMP/codenetix-action-<guid>` (o `%TEMP%`); se borra tras `SqliteConnection.ClearAllPools()`, quitando el solo lectura sin seguir enlaces. Con `CODENETIX__Persistence__DataDirectory` explícito no se borra nada.
- **Pruebas (`ActionModeTests`, 17 casos en 10 tests):** `ActionMode.RunAsync` en proceso con `ActionModeHooks` (entorno en memoria sustituyendo las fuentes de entorno/línea de comandos, Kestrel en `127.0.0.1:0`, proxy `CONNECT` efímero y los fakes de `CodeNetixFactory`: GitHub, chat, Sandbox, tests y un publicador de PR falso). La fila de credenciales del runner clona de verdad desde `GITHUB_SERVER_URL` (repositorio local) y comprueba que todas las peticiones a la API llevan `Bearer <GITHUB_TOKEN>`, que no hay `GET /user` y que el token citado por el Analista sale enmascarado.
- **README:** sección corta del modo con sus variables; el workflow de ejemplo y `action.yml` son de la 6.2.
- **Pruebas inestables previas corregidas (solo tests, ajenas a la 6.1):**
  - `LivePanelHubTests.Reconnection_…` seguía fallando en `docker build` (9 esperado, 12 recibido): el parche D2 de la 5.3 admitía los eventos del orquestador intercalados, pero comparaba el número de `seq` del tramo con los del test. Ahora exige el tramo `[primero, último]` completo (sin huecos ni duplicados), que es lo que el test quiere demostrar.
  - `CancelRunTests.Cancelling_active_run_in_Coding_…` fallaba a veces con toda la suite: leía la Ejecución justo al ver la sesión liberada, antes de que se confirmara `SandboxReleased` (que va después de `DisposeAsync`). Ahora espera hasta 10 s a verlo.
- **Verificación:** `dotnet build` 0 advertencias; `dotnet test --solution CodeNetix.slnx` 1008/1008 en verde dos veces seguidas en Windows; `docker build -f docker/codenetix.Dockerfile` en verde (Linux: 1001 tests, 989 correctos y 12 omitidos por no tener Docker dentro del build).
- **Riesgos / pendientes:**
  - Cancelación del job: al recibir SIGINT/SIGTERM el host se detiene y la Ejecución queda no terminal en una base de datos que se borra; no se publica `RunCancelled` en la issue. Una cancelación ordenada (`IRunControl` antes de parar) queda para la 6.2 junto con el `timeout-minutes` del workflow.
  - El `HEALTHCHECK` de la imagen consulta `/api/health` en `:5000`, que `action` no abre: la 6.2 debe lanzar el contenedor con `--no-healthcheck`.
  - Los PR con `GITHUB_TOKEN` exigen activar "Allow GitHub Actions to create and approve pull requests" en el repositorio; documentarlo en la 6.2.
  - No se ha probado en un runner real (Docker del runner, red `codenetix-sandbox` con el alias `gateway` desde el contenedor de la acción): eso llega con la 6.2.

## Spec Change Log

## Review Triage Log

Revisión 1 (Claude, revisor combinado, 11 hallazgos):

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | medium | `appsettings*.json` del directorio de trabajo (repo objetivo) se carga y puede redirigir la key del Proveedor | patch (también en `serve`) |
| 2 | medium | Espera infinita si la Ejecución ya terminó en otro proceso o el Evento queda sin Ejecución | patch |
| 3 | low/medium | Excepciones inesperadas escapan: sin limpieza, fuera de stop-commands y con redactor vacío | patch |
| 4 | low | Parada de 30 s mayor que el margen del runner al cancelar | defer (6.2) |
| 5 | low | `Login` por defecto solo si el token vino de `GITHUB_TOKEN` | patch |
| 6 | medium | Test de "nunca `GET /user`" no puede fallar | patch |
| 7 | medium | Enmascarado y stop-commands no verificados sobre los logs del host | patch |
| 8 | medium | Nada prueba que `action` no sirva `/api`, `/hubs` ni OpenAPI | patch |
| 9 | medium | Aviso de finalización del Dispatcher sin tests | patch |
| 10 | low | Casos límite del parseo del evento y deduplicación sin test | reject |
| 11 | low | El drenaje final puede perder eventos tardíos | patch (1 lectura más) |
