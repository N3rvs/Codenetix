---
review: adversarial
target: ../ARCHITECTURE-SPINE.md
lens: 'Dos unidades que cumplen todos los AD al pie de la letra y aun así son incompatibles'
date: '2026-10-27'
---

# Revisión adversarial — Architecture Spine CodeNetix v1

## Veredicto

El spine fija bien los límites estáticos (dependencias hacia Contracts, dueño único del Estado, Sandbox sin secretos), pero deja **sin dueño los flujos dinámicos que cruzan módulos**: quién escribe `RunEvent` fuera de Pipeline, quién posee el Sandbox a lo largo de varias Rondas, dónde se corta el presupuesto, cómo llega la cancelación a un contenedor en marcha y cómo entra `codenetix fix` al sistema. Dos equipos independientes pueden cumplir los 14 AD y producir piezas que no encajan. **No está listo para partir en épicas** sin cerrar al menos H-1 a H-5.

## Método

Para cada hueco se construyen dos unidades (A y B) un nivel más abajo —historias o épicas implementadas por desarrolladores o agentes que no se hablan—. Ambas cumplen todos los AD literalmente. Se muestra el choque y se propone la regla exacta (AD nuevo o endurecido) que lo hace imposible.

Severidad: **Crítica** (rompe la ejecución o la seguridad), **Alta** (datos o comportamiento incoherentes visibles), **Media** (retrabajo seguro, sin pérdida de datos).

---

## H-1 — Nadie posee el camino de escritura de `RunEvent` fuera de Pipeline · Crítica

**AD implicados:** AD-1, AD-2, AD-3, AD-7, AD-11, AD-13.

AD-3 exige que todo lo observable sea un `RunEvent` persistido. AD-7 exige que el Gateway (módulo Sandbox) registre cada petición bloqueada como `RunEvent`. Las líneas de terminal del Programador salen del Sandbox. Pero AD-11 dice que cada módulo posee sus tablas (`run_` es de Pipeline) y AD-1 prohíbe que Sandbox referencie a Pipeline. El spine no dice por qué interfaz se escribe.

- **Unidad A (épica Sandbox):** define en Contracts `ISandboxOutput : IAsyncEnumerable<OutputChunk>` y devuelve la salida al orquestador; los bloqueos del Gateway los acumula en memoria y los devuelve en el `StageResult` al final de la Etapa. Cumple AD-1 y AD-11.
- **Unidad B (épica Panel/terminal):** espera líneas en vivo cada ≤250 ms (AD-3) y construye la terminal de xterm sobre ellas. Con A, la terminal queda vacía hasta que termina la Etapa y los bloqueos del Gateway aparecen minutos después.
- **Variante:** una tercera unidad añade `IRunEventWriter` en Contracts implementado por Persistence, escribiendo directamente en `run_events` → viola de hecho la propiedad de tablas de Pipeline, pero nada lo prohíbe porque Persistence posee el `DbContext` único.

Choques adicionales: (1) ¿quién agrupa en lotes de 250 ms, el productor o el escritor?; (2) ¿quién llama a `ISecretRedactor`, cada productor o el sumidero? (AD-13 dice "todos pasan", no "quién"); (3) los `RunEvent` transaccionales de Pipeline y los asíncronos de Sandbox no tienen un orden común, y el Panel no puede reanudar el flujo sin un cursor.

**Regla propuesta (AD-3 endurecido):**
> Existe una única interfaz `IRunEventSink` en Contracts, implementada **solo** por el módulo Pipeline, que es dueño exclusivo de la tabla `run_events`. Todo módulo que produzca observables (Sandbox, Gateway, Agents, GitHub) llama a `IRunEventSink.Append(runId, type, payload)`; ninguno escribe la tabla de otro modo. El sumidero: (a) aplica `ISecretRedactor` —los productores **no** redactan—; (b) asigna un `seq` monotónico por `runId` (entero, sin huecos) en la misma transacción; (c) hace el agrupamiento de 250 ms para `type = OutputAppended`; (d) publica al hub solo tras confirmar la transacción. Las transiciones de Estado (AD-2) usan el mismo sumidero dentro de su transacción. El Panel reanuda con `GET /runs/{id}/events?afterSeq=N` y el hub envía `seq` en cada mensaje.

---

## H-2 — Quién posee el Sandbox a lo largo de la Ejecución y quién llama a quién en una Etapa · Crítica

**AD implicados:** AD-1, AD-5, AD-6, AD-8.

AD-6: un Sandbox **por Ejecución**, destruido en `finally`. AD-5: `ClaudeCodeAgent` vive en Agents pero "corre solo dentro del Sandbox". AD-1: Agents no puede referenciar Sandbox. AD-8: el clonado lo hace GitHub "al directorio que luego se monta". El ciclo Programando ↔ Revisando puede repetirse varias Rondas.

- **Unidad A (épica Programación):** `ICodingAgent.RunAsync(spec, ct)` en Agents pide a un `ISandboxFactory` (Contracts) un contenedor, ejecuta `claude -p` y lo destruye en su `finally`. Cumple AD-6 literalmente ("lo destruye en un bloque finally").
- **Unidad B (épica Tests/Revisión, FR-12):** espera que el mismo contenedor siga vivo para lanzar `dotnet test` y producir `TestReport`, y que la Ronda 2 continúe sobre el estado de compilación de la Ronda 1. Con A cada Ronda crea un Sandbox nuevo → "uno por Ejecución" se convierte en "uno por Etapa", la caché de NuGet se pierde y el `TestReport` lo produce no se sabe quién.

Otros choques del mismo hueco:
- **Quién produce el `Diff`:** AD-8 prohíbe Git dentro del Sandbox para *push*, pero `/workspace` contiene `.git`; Claude Code puede hacer `git commit`/`checkout` dentro y desplazar `HEAD`. Si GitHub calcula `git diff HEAD` el diff sale vacío.
- **Ruta del volumen:** el host corre en contenedor con el socket de Docker montado. Si GitHub clona en `/tmp/codenetix/<runId>` *dentro* del contenedor host y Sandbox lo monta como bind, el daemon del host interpreta esa ruta en la máquina anfitriona y monta un directorio vacío. Ambas unidades cumplen AD-6/AD-8.

**Regla propuesta (AD-6 endurecido + AD nuevo "Orquestación de Etapa"):**
> 1. **Pipeline es el dueño del ciclo de vida del Sandbox.** El orquestador llama a `ISandboxManager.CreateForRun(runId)` al entrar en `Analizando` (tras el clonado) y a `DisposeAsync()` en el `finally` de la Ejecución completa; ningún otro módulo crea ni destruye contenedores. El objeto `ISandboxSession` (Contracts) expone `ExecAsync(cmd, ct)` y `Output` y se pasa como parámetro.
> 2. **Secuencia canónica por Ronda:** Pipeline → `ICodingAgent.RunAsync(session, spec, feedback, ct)` → Pipeline → `ITestRunner.RunAsync(session, ct)` (implementado en Sandbox, devuelve `TestReport`) → Pipeline → `IGitWorkspace.ComputeDiff(runId)` (GitHub, contra el SHA base fijado al clonar, no contra `HEAD`) → Pipeline → `IReasoningAgent` revisores. Ningún módulo de Etapa llama a otro módulo de Etapa.
> 3. El directorio `.git` **no** se monta en `/workspace` (se monta solo el árbol de trabajo; GitHub conserva el `.git` fuera). El diff se calcula siempre contra `BaseSha`.
> 4. El espacio de trabajo es un volumen Docker con nombre `codenetix-work`, con subcarpeta `<runId>`, montado en el host en `/work` y en el Sandbox con `volume-subpath`; nunca un bind de ruta del contenedor host.

---

## H-3 — Presupuesto: dos contadores, ningún árbitro · Alta

**AD implicados:** AD-7, AD-10; mapa FR-13 → "Sandbox (contador del Gateway), Pipeline".

El Gateway cuenta solo los tokens del Programador. El Analista y los Revisores llaman al LLM desde el host con `IChatClient` y no pasan por el Gateway. El mapa reparte FR-13 entre dos módulos sin decir quién decide cortar.

- **Unidad A (épica Gateway):** cuando el contador de la Ejecución supera el límite, responde `429` al CLI de Claude. Cumple AD-7 ("se contabiliza para el presupuesto").
- **Unidad B (épica Orquestación):** consulta el coste acumulado (de los `RunEvent` de coste) al terminar cada Etapa y, si se superó, transiciona a `Fallida` con motivo `BudgetExceeded`.
- **Resultado:** con A, el CLI recibe 429, lo interpreta como *rate limit*, reintenta o termina con error genérico; el `StageResult` llega como fallo de Programación, B ve el coste por debajo del límite (no cuenta los Revisores, o cuenta solo lo que A registró) y la Ejecución acaba `Fallida` por "error del Programador". Además, la Ronda de revisión con modelos caros puede superar el presupuesto sin que nadie la pare.
- **Atribución:** un único Gateway sirve a todos los Sandboxes; con `MaxConcurrentRuns > 1`, ¿cómo sabe a qué `runId` imputar? Una unidad usa la IP del contenedor, otra la etiqueta `codenetix.run`; ninguna está fijada.

**Regla propuesta (AD nuevo "Libro de presupuesto"):**
> El módulo Pipeline posee `IBudgetLedger` (Contracts), único árbitro de FR-13. El límite efectivo se calcula al crear la Ejecución (AD-10, ver H-6) y se congela en ella. **Todo** gasto de LLM se registra con `ledger.Charge(runId, source, tokensIn, tokensOut, costUsd)`: el Gateway para el Programador y un *middleware* `IChatClient` obligatorio para Analista y Revisores. `Charge` devuelve `Allowed | Exhausted`. Ante `Exhausted`: el Gateway responde `402` con cuerpo `codenetix:budget_exhausted` (el adaptador `ClaudeCodeAgent` lo mapea a `StageOutcome.BudgetExhausted`, no a error) y el ledger dispara la cancelación cooperativa de la Ejecución con motivo `BudgetExceeded` (H-4). La atribución en el Gateway se hace por **la key ficticia única por Ejecución** inyectada en el Sandbox (`ANTHROPIC_API_KEY=cnx-run-<runId>-<nonce>`), nunca por IP.

---

## H-4 — Cancelación: quién la aplica y cómo llega al contenedor · Alta

**AD implicados:** AD-2, AD-6; diagrama de estados.

AD-2 dice que el Estado solo cambia vía `PipelineRun`, pero no quién llama al método ni cómo se coordina con la Etapa en curso. El diagrama no tiene `Decidiendo → Cancelada`.

- **Unidad A (endpoint `POST /runs/{id}/cancel` en Host):** carga el agregado, llama a `run.Cancel()`, guarda y devuelve 202. Cumple AD-2 (usa el método del agregado).
- **Unidad B (orquestador):** sigue con el agregado en memoria; la Etapa de Programación termina 3 min después, aplica `StageResult` → `Revisando`. Dependiendo de la concurrencia optimista: o sobreescribe `Cancelada` (estado imposible) o lanza excepción de transición ilegal y el orquestador cae sin destruir el Sandbox de forma ordenada. El contenedor sigue gastando tokens todo ese tiempo porque nadie le pasó un `CancellationToken`.
- **Decidiendo:** el usuario cancela mientras GitHub hace push; A devuelve 409, otra implementación del Panel muestra el botón activo → PR creado tras "cancelar".

**Regla propuesta (AD-2 endurecido):**
> La cancelación es una **petición**, no una transición: `IRunControl.RequestCancel(runId, reason)` registra la petición (`RunEvent CancellationRequested`) y dispara el `CancellationTokenSource` de la Ejecución, que el orquestador mantiene y pasa a toda llamada de Etapa (`ICodingAgent`, `ITestRunner`, `IReasoningAgent`, operaciones de GitHub). **Solo el orquestador** llama a `PipelineRun.Cancel()`, al observar el token. Un `StageResult` recibido con el token cancelado se descarta. `ISandboxSession` debe matar el proceso y detener el contenedor en ≤ 10 s tras la cancelación del token. `Decidiendo` **no** es cancelable (push y PR son atómicos desde el punto de vista del usuario): `RequestCancel` en `Decidiendo` o estados terminales devuelve 409 `ProblemDetails` y el Panel deshabilita el botón según el Estado. La misma vía la usan el presupuesto (H-3), el *timeout* global y el apagado del host.

---

## H-5 — `codenetix fix` y el formulario del Panel: autenticación y punto de entrada · Alta

**AD implicados:** AD-4, AD-12, AD-14; FR-4, FR-26.

AD-12 dice que `fix` "llama a la API local" y que el Panel "ofrece el mismo formulario". AD-4 dice que **toda** Fuente produce un `EventEnvelope` y que solo el Dispatcher crea Ejecuciones. No se define el endpoint, la autenticación, el actor ni la clave de deduplicación.

- **Unidad A (épica CLI):** al ejecutarse vía `docker exec` dentro del mismo contenedor, llama a `http://localhost:5000/api/runs` sin credenciales, porque la API solo escucha en localhost (FR-4).
- **Unidad B (épica Acceso/Panel, FR-4):** protege toda la API con la sesión del Panel (cookie) o un token de acceso. El CLI recibe 401.
- **Unidad C (épica Ingestion manual):** expone `POST /api/events` → `EventEnvelope{Source=Manual}`; clave de deduplicación = URL del issue. Segundo `fix` sobre el mismo issue tras un fallo → `ignored: duplicate`. Mientras tanto A llamaba a `/api/runs` y otro desarrollador implementó ese endpoint creando `PipelineRun` directamente (Host compone todo; nada lo impide en tiempo de compilación) → se salta autorización FR-26 y deduplicación.
- **Actor:** en webhooks el actor es el usuario de GitHub; en `fix` no hay actor. ¿Se comprueba el permiso de escritura de quién?

**Regla propuesta (AD-4 + AD-12 endurecidos):**
> 1. No existe ningún endpoint que cree `PipelineRun`. `fix` y el Panel llaman a `POST /api/events/manual` (Ingestion), que produce `EventEnvelope{Source=Manual}`.
> 2. Autenticación: toda la API (salvo `/webhooks/*`, que usa firma) exige `Authorization: Bearer <admin-token>`. El token se genera en el primer arranque y se guarda en `/data/admin.token` (permisos 0600; no es un secreto de terceros, AD-11 se acota a "secretos de terceros"). El CLI lo lee del fichero al correr vía `docker exec`; el Panel lo obtiene en el login local. 
> 3. Actor de una Fuente manual = la identidad del token de GitHub de la instancia; FR-26 se verifica contra ella.
> 4. Clave de deduplicación manual = `manual:<url>:<requestId>` donde `requestId` es un GUID que genera el cliente (los reintentos de red del mismo comando deduplican; un nuevo `fix` explícito crea un Evento nuevo).

---

## H-6 — `codenetix.yml`: quién lo lee, de qué commit y cuándo se congela · Alta

**AD implicados:** AD-8, AD-10, AD-11.

La Configuración de repositorio vive en el repositorio, que solo puede leer GitHub (AD-8), pero la consumen Pipeline (límites, Rondas, Rutas protegidas para `MergePolicy`), Agents (modelos por agente) y el ledger (presupuesto).

- **Unidad A (GitHub):** expone `IRepoConfigSource.Load(repo)` que lee `codenetix.yml` de la rama por defecto vía API.
- **Unidad B (Pipeline):** lee `/work/<runId>/codenetix.yml` del clon, y lo vuelve a leer en cada Ronda. Como el Programador puede editar ese archivo dentro de `/workspace`, la Ronda 2 se ejecuta con Rutas protegidas vacías y presupuesto ampliado → `MergePolicy` clasifica mal y viola AD-10 ("solo restringir").
- **Variante:** Agents lee la configuración por su cuenta con `IOptions<RepoConfig>` (AD de Configuración: "opciones tipadas por módulo") → valores distintos a los que usa Pipeline.

**Regla propuesta (AD-10 endurecido):**
> La Configuración de repositorio se lee **una vez**, por GitHub, del `BaseSha` de la rama por defecto, antes de `Analizando`. Pipeline calcula la `EffectiveConfig` (instancia ∩ repositorio, AD-10), la persiste como instantánea inmutable en `run_runs.effective_config` y la pasa explícitamente como parámetro a Etapas, ledger y `MergePolicy`. Ningún módulo lee `codenetix.yml` del espacio de trabajo ni registra `RepoConfig` en DI. Un `codenetix.yml` inválido → `Recibida → Fallida` (ver H-8).

---

## H-7 — Tablas de Stage, Spec, Verdict y PR: dueño indefinido y `DbContext` único · Media

**AD implicados:** AD-1, AD-11; diagrama ER.

El ER muestra `STAGE`, `SPEC`, `VERDICT`, `PULL_REQUEST`, pero AD-11 solo enumera prefijos `ing_`, `run_`, `gh_`. Persistence posee "el `DbContext`", pero solo referencia Contracts, así que las entidades EF tendrían que vivir en Contracts o en Persistence, no en el módulo dueño.

- **Unidad A (Agents):** persiste los Veredictos en `agt_verdicts` con su propio `DbContext` (el módulo es dueño de sus tablas, AD-11).
- **Unidad B (Pipeline):** guarda el `Verdict` serializado en `run_stages.result_json` porque es parte del `StageResult` que aplica al agregado.
- **Unidad C (Panel/historial):** lee `VerdictIssued` de `run_events`. Tres copias, tres esquemas; al cambiar `Verdict` solo se migra una.
- **Identidad de Stage:** ¿una Stage por Revisor o una Stage "Revisión" con N Veredictos? ¿La Ronda 2 reutiliza la Stage? React Flow necesita IDs de nodo estables; un equipo usa `stageId` y otro `(kind, round)`.

**Regla propuesta (AD-11 endurecido):**
> 1. Cada módulo con tablas declara su propio `DbContext` en su proyecto, todos sobre el mismo archivo y con su propio historial de migraciones (`__ef_migrations_<prefijo>`). Persistence se reduce a la cadena de conexión, la configuración de SQLite (WAL, `busy_timeout`) y el orden de aplicación de migraciones. Prefijos cerrados: `ing_` (Ingestion), `run_` (Pipeline), `gh_` (GitHub). **Agents y Sandbox no tienen tablas.**
> 2. Pipeline es dueño de `run_runs`, `run_stages`, `run_specs`, `run_verdicts`, `run_events`. Agents devuelve `Spec`/`Verdict` y no persiste. GitHub es dueño de `gh_pull_requests`.
> 3. Una Stage es `(stageId, runId, kind ∈ {Analysis, Coding, Testing, Review, Decision}, round, reviewerId?)`; hay una Stage `Review` **por Revisor y por Ronda**. Las tablas son la verdad; los `RunEvent` llevan `stageId` y son la proyección observable, escritos en la misma transacción.

---

## H-8 — Máquina de estados incompleta para fallos tempranos y reinicio · Media

**AD implicados:** AD-2, AD-6; FR-24.

Faltan `Recibida → Fallida` (clonado fallido, config inválida, imagen inexistente) y `Decidiendo → Cancelada` (resuelto en H-4 como no cancelable). En el arranque, AD-6 borra contenedores "no asociados a una Ejecución activa" y FR-24 marca las activas como `Fallida`; el orden no está fijado.

- **Unidad A (Sandbox, arranque):** limpia contenedores consultando Ejecuciones activas vía Contracts.
- **Unidad B (Pipeline, arranque):** marca primero todas las activas como `Fallida`. Si B corre antes que A, A no ve ninguna activa y borra todo (correcto por casualidad); si A corre antes, conserva contenedores de Ejecuciones que B marcará `Fallida` un instante después → contenedores huérfanos hasta el siguiente arranque. Con `Recibida` sin salida a `Fallida`, B lanza excepción de transición ilegal al reconciliar una Ejecución que quedó en `Recibida`.

**Regla propuesta (AD-2 + AD-6 endurecidos):**
> Añadir `Recibida → Fallida`. Toda transición a `Fallida`/`Cancelada` lleva un `FailureReason` enumerado en Contracts (`ConfigInvalid`, `CloneFailed`, `BudgetExceeded`, `Timeout`, `HostRestarted`, `StageError`, `UserCancelled`). Arranque en orden fijo y único, ejecutado por un `StartupReconciler` en Host: (1) migraciones; (2) Pipeline marca toda Ejecución no terminal como `Fallida(HostRestarted)`; (3) Sandbox elimina **todos** los contenedores con la etiqueta `codenetix.run` y las subcarpetas de `codenetix-work`; (4) se arranca el Dispatcher.

---

## H-9 — Contratos del hub SignalR fuera del OpenAPI · Media

**AD implicados:** AD-3, AD-14.

AD-14 genera los tipos TypeScript desde OpenAPI, pero OpenAPI no describe los mensajes de SignalR. El `payload` de cada `type` de `RunEvent` no tiene esquema.

- **Unidad A (backend):** envía `RunEvent{type, payload: object}` con `payload` como `JsonElement` libre por tipo.
- **Unidad B (Panel):** cumple AD-14 (no escribe tipos REST a mano) pero, para el hub, escribe a mano interfaces `StageStartedPayload` con `stageName`; el backend usa `stageKind`. Nada falla en el build.

**Regla propuesta (AD-14 endurecido):**
> Cada `type` de `RunEvent` tiene un DTO de payload en Contracts, agrupado en una unión discriminada (`[JsonPolymorphic]` por `type`). El esquema `RunEvent` (con la unión) se publica en el OpenAPI vía `GET /runs/{id}/events`, y el hub `/hubs/runs` envía exactamente ese mismo tipo. Los tipos TS del hub se importan de los generados. Una prueba de contrato serializa un ejemplo de cada `type` y lo valida contra el esquema OpenAPI.

---

## H-10 — Notificaciones en GitHub (FR-19): disparador ambiguo · Media

**AD implicados:** AD-3, AD-8.

- **Unidad A (GitHub):** se suscribe al flujo de `RunEvent` (es "la única fuente") y comenta en el issue al ver `StateChanged → PRCreado|Fallida`.
- **Unidad B (Pipeline):** llama explícitamente a `IGitHubNotifier.Notify(...)` en la Etapa de Decisión.
- **Resultado:** comentarios duplicados; y en modo `action` la suscripción de A (al hub) no existe → ningún comentario.

**Regla propuesta (AD-3 aclarado + AD-8):**
> Los `RunEvent` son para observación (Panel, stdout), **nunca** disparan efectos laterales. Los efectos en GitHub (comentario, PR, etiquetas) son pasos explícitos del orquestador vía interfaces de GitHub, idempotentes por `(runId, NotificationKind)` registrados en `gh_notifications`.

---

## Otros puntos menores

- **ER `EVENT ||--o| PIPELINE_RUN` vs reintentos:** "máximo una Ejecución activa por Evento" sugiere varias en total; el ER dice cero o una. Fijar `EVENT ||--o{ PIPELINE_RUN` con unicidad parcial sobre las no terminales, o declarar que un reintento crea un Evento nuevo (coherente con H-5.4).
- **Modo `action`:** AD-12 dice "mismo Pipeline", pero no qué Ingestion: fijar que el modo `action` construye un `EventEnvelope{Source=GitHubAction}` desde `GITHUB_EVENT_PATH` y pasa por el mismo Ingestion (sin firma, con verificación FR-26), y que el `admin-token` no aplica.
- **Commit de resultado:** no se dice si GitHub hace commit por Ronda o solo en `Decidiendo`. Fijar: un único commit en `Decidiendo`, a partir del árbol de trabajo, sobre `BaseSha`.
- **`ISecretRedactor` y la key ficticia por Ejecución (H-3):** el marcador no es secreto, pero la key real sí; el redactor debe recibir también las keys de Proveedor de Revisores cargadas en tiempo de ejecución.

## Resumen

| # | Hueco | Severidad | Cambio |
| --- | --- | --- | --- |
| H-1 | Escritura de `RunEvent` fuera de Pipeline | Crítica | AD-3 endurecido: `IRunEventSink` único, `seq`, redacción y lotes en el sumidero |
| H-2 | Dueño del Sandbox y secuencia de llamadas por Etapa | Crítica | AD-6 endurecido + AD nuevo de orquestación; volumen con nombre; `.git` fuera |
| H-3 | Presupuesto sin árbitro | Alta | AD nuevo `IBudgetLedger`; key ficticia por Ejecución |
| H-4 | Cancelación | Alta | AD-2 endurecido: petición + token; `Decidiendo` no cancelable |
| H-5 | `fix`/Panel: auth y punto de entrada | Alta | AD-4/AD-12: `POST /api/events/manual`, `admin-token`, actor y dedup |
| H-6 | Lectura de `codenetix.yml` | Alta | AD-10: instantánea `EffectiveConfig` desde `BaseSha` |
| H-7 | Tablas Stage/Spec/Verdict/PR | Media | AD-11: `DbContext` por módulo, dueños cerrados, identidad de Stage |
| H-8 | Estados y arranque | Media | `Recibida → Fallida`, `FailureReason`, `StartupReconciler` |
| H-9 | Payloads del hub sin contrato | Media | AD-14: unión discriminada en OpenAPI |
| H-10 | Notificaciones duplicadas | Media | Eventos sin efectos laterales; notificación explícita e idempotente |
