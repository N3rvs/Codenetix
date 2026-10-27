# Epic 1 Context: Instalar y obtener una Spec

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Poner en pie el esqueleto completo de CodeNetix y recorrer de punta a punta el primer tramo de la cadena: el usuario arranca la instancia con un único `docker run`, sin cuentas ni login, la configura con variables de entorno (con modo diagnóstico si falta algo), lanza `codenetix fix <url-de-issue>` y obtiene una Spec técnica validada de su issue, o un rechazo temprano comentado en la issue. Esta épica fija los cimientos sobre los que se apoyan las demás: la solución modular, el acceso seguro, la entrada de trabajo única (Ingestion), la máquina de estados de la Ejecución, el sumidero de eventos y la configuración congelada por Ejecución. Todavía no hay documento de UX; el Panel solo necesita una página mínima.

## Stories

- Story 1.1: Arrancar CodeNetix con un comando
- Story 1.2: Configurar la instancia y modo diagnóstico
- Story 1.3: Acceso seguro al Panel y la API
- Story 1.4: Lanzar trabajo con `codenetix fix`
- Story 1.5: Autorización del disparador
- Story 1.6: Ejecución con máquina de estados visible
- Story 1.7: Clonar y leer la configuración del repositorio
- Story 1.8: El Analista genera la Spec
- Story 1.9: Rechazo temprano con aviso en la issue

## Requirements & Constraints

- **Arranque:** un único `docker run -p 127.0.0.1:5000:5000 -v codenetix-data:/data codenetix serve`; `/api/health` disponible en menos de 60 s desde el arranque del contenedor; `/data/codenetix.db` se crea solo. Sin registro, login ni cuentas.
- **Secretos:** keys y tokens solo por variables de entorno; nunca se escriben en SQLite, logs, API ni Panel; siempre enmascarados (`sk-…abcd`).
- **Modo diagnóstico:** si falta una variable obligatoria (key de Proveedor o token de GitHub), la instancia arranca pero no procesa Eventos e indica qué falta en `/api/health` y en el Panel.
- **Acceso:** solo localhost por defecto; `/api` y `/hubs` exigen siempre el `admin-token`. Exponer la instancia sin `AccessToken` explícito aborta el arranque. Los webhooks quedan exentos del token (la verificación de firma llega en la Épica 7).
- **`codenetix fix`:** el Evento aparece en menos de 2 s; una URL no admitida da error explicativo y no persiste nada; las URLs de Sentry responden "soporte de Sentry disponible próximamente" hasta la Épica 7; reintentos con el mismo `requestId` no duplican.
- **Autorización:** solo actores con permiso de escritura en el Repositorio objetivo; si no, el Evento queda `ignored` con motivo "no autorizado". Para el Evento manual, el actor es la identidad del token de GitHub de la instancia. El texto del Evento es siempre dato no confiable.
- **`codenetix.yml`:** sin archivo se aplican los valores por defecto (Revisores `security`, `quality`, `tests`; Rutas protegidas `**/Migrations/**`, `**/*.csproj`, `.github/**`). Si es inválido, `Fallida(InvalidRepoConfig)` sin llamar a ningún agente. El repositorio solo puede restringir los límites de la instancia, nunca ampliarlos.
- **Spec:** problema, causa probable, cambio propuesto, criterios de aceptación verificables y archivos afectados; esquema estructurado con máximo 2 reintentos, si no `Fallida(SchemaInvalid)`. Debe funcionar con Proveedores compatibles con OpenAI, incluidos modelos locales sin salida estructurada.
- **Rechazo temprano:** "no automatizable" (falta información, cambio demasiado grande o fuera de .NET) lleva a `Fallida(NotAutomatable)` y a un único comentario en la issue.
- **Puente provisional:** tras una Spec válida, la Ejecución termina en `Fallida(StageError)` con "Programación no disponible" hasta la Épica 2.
- **Transversales:** sin telemetría; código del usuario solo hacia GitHub y los Proveedores elegidos; Linux/macOS/Windows en x64 y arm64.

## Technical Decisions

- **Solución (sin starter):** .NET 10 con `CodeNetix.Contracts`, `Host`, `Ingestion`, `Pipeline`, `Agents`, `Sandbox`, `GitHub`, `Persistence`, más `ArchitectureTests` y `panel/` (React 19 + Vite 8, compilado en `wwwroot`). Cada módulo solo referencia Contracts; solo Host referencia a todos. `ArchitectureTests` rompe el build ante referencias entre módulos de negocio. Imagen `codenetix` sobre `aspnet:10.0` con `HEALTHCHECK` en `/api/health`.
- **Estructura:** slices en `Features/<Verbo><Sustantivo>/` (handler, validación y tests juntos). `IOptions<T>` por módulo; solo Host lee el entorno.
- **Persistencia:** un único `/data/codenetix.db` con WAL y `busy_timeout`. Un `DbContext` por módulo con historial `__ef_migrations_<prefijo>`; Persistence solo aporta conexión, ajustes y orden de migración. Dueños: Ingestion → `ing_events`; Pipeline → `run_runs`, `run_stages`, `run_specs`, `run_events` (y más adelante `run_verdicts`, `run_budget`); GitHub → `gh_notifications` (y `gh_pull_requests`).
- **Configuración:** instancia = `CODENETIX__*` + `/config/codenetix.instance.yml` opcional. Repositorio = `codenetix.yml` (`version: 1`, claves snake_case) validado contra `schemas/codenetix.v1.json`, leído una sola vez por el módulo GitHub desde el `BaseSha`. Pipeline calcula la `EffectiveConfig` y la guarda como instantánea inmutable en la Ejecución.
- **Acceso:** `:5000` para Panel, `/api` y `/hubs`; `:5001` para `/webhooks/*`. Un único middleware de autenticación con `Bearer <admin-token>`; el token se genera en el primer arranque en `/data/admin.token` (0600).
- **Arranque (`StartupReconciler`):** migraciones → validar configuración y publicar `IInstanceHealth` (`Ready | Diagnostic(reasons)`) → (reconciliación de Ejecuciones y Sandboxes, Épica 5) → arrancar Dispatcher. En `Diagnostic` ningún `BackgroundService` procesa.
- **Redacción:** `ISecretRedactor` único, aplicado en `IRunEventSink` y en el sumidero de logs; conoce todos los secretos de la instancia y patrones de keys. Los productores no redactan.
- **Modos:** `serve` (host completo) y `fix` (cliente ligero vía `docker exec codenetix codenetix fix <url>` que lee la URL de la API y el `admin-token` del propio contenedor y llama a `POST /api/events/manual`).
- **Ingestion:** toda Fuente produce un `EventEnvelope` (Fuente, objeto, clave de deduplicación, actor, payload como datos). Clave manual: `manual:<url>:<requestId>` (el `requestId` lo genera el cliente). Autoriza y persiste antes de responder. Ningún endpoint crea Ejecuciones.
- **Ejecución:** solo el Dispatcher crea `PipelineRun`, como mucho una no terminal por objeto. Solo los métodos del agregado cambian el Estado (transición ilegal = excepción) y solo los invoca el orquestador. Cada transición se persiste junto a su `RunEvent` en la misma transacción. `FailureReason` vive en Contracts (`InvalidRepoConfig`, `CloneFailed`, `NotAutomatable`, `SchemaInvalid`, `StageError`, …). Tests de tabla para todas las transiciones válidas e inválidas.
- **`IRunEventSink`:** único punto de escritura de `run_events` (implementado por Pipeline); `seq` monotónico por Ejecución; publica solo tras el commit; consulta con `afterSeq`. Los `RunEvent` nunca disparan efectos. `RunEvent` es una unión discriminada (`[JsonPolymorphic]` por `type`, verbos en pasado) publicada en el OpenAPI.
- **Orquestador (orden canónico):** 1) GitHub clona, fija `BaseSha`, lee `codenetix.yml` y se calcula la `EffectiveConfig`; 3) Analista genera la Spec. Ningún módulo de Etapa llama a otro.
- **Clonado:** solo el módulo GitHub, con `http.extraHeader` efímero; verificar que no queda ninguna credencial en el árbol de trabajo.
- **Analista:** `IReasoningAgent` sobre `IChatClient` (Microsoft.Extensions.AI 10.11.0 / Agent Framework 1.26.0), nunca ejecuta código; salida estructurada con el esquema `Spec` de Contracts; decorador común de reintentos; alternativa de JSON en el prompt. El texto de la issue va como `UntrustedText` (tipo de Contracts que solo se renderiza dentro de un bloque delimitado como datos), nunca en el *system prompt*.
- **Efectos en GitHub:** pasos explícitos del orquestador, idempotentes por `(runId, kind)` en `gh_notifications`.
- **Convenciones:** IDs `Guid.CreateVersion7()` (runId corto de 8 caracteres solo para mostrar o nombrar); fechas UTC `DateTimeOffset` ISO 8601; errores de API RFC 9457 `ProblemDetails`; JSON camelCase; Estados serializados `Received`, `Analyzing`, `Coding`, `Reviewing`, `Deciding`, `PullRequestCreated`, `Failed`, `Cancelled`; logging con `Microsoft.Extensions.Logging` + OpenTelemetry (una traza por Ejecución con `runId`).
- **Stack:** SDK 10.0.402, EF Core Sqlite 10.0.14, React 19.3.1, Vite 8.3.5, Tailwind CSS 4.3.3.

## UX & Interaction Patterns

- Sin diseño visual todavía (el Panel completo se diseña con Sally en la Épica 4). En esta épica basta con una página servida en `http://localhost:5000` con un mensaje de bienvenida (idealmente con el comando `codenetix fix` de ejemplo) y, en modo diagnóstico, la lista de lo que falta.
- El CLI muestra el id del Evento al crearlo y errores explicativos ante URLs no admitidas; `codenetix fix --follow` y `GET /api/runs/<runId>` muestran el Estado y los `RunEvent` en orden de `seq`.

## Cross-Story Dependencies

- 1.1 es la base de todo (solución, imagen, SQLite, health). 1.2 aporta `IInstanceHealth` y `ISecretRedactor`, que usan el resto. 1.3 protege los endpoints que crean 1.4 y 1.6.
- 1.4 → 1.5 → 1.6: el Evento manual se autoriza en Ingestion antes de que el Dispatcher cree la Ejecución. 1.5 define `UntrustedText`, que consume 1.8.
- 1.6 (`PipelineRun`, `IRunEventSink`, orquestador) es prerrequisito de 1.7, 1.8 y 1.9. 1.7 produce la `EffectiveConfig` que necesita 1.8; 1.9 depende del resultado del Analista de 1.8.
- Con otras épicas: la Épica 2 sustituye el puente `Fallida(StageError)` de 1.8 por `Programando`; la Épica 4 construye el Panel sobre la API, el `seq`/`afterSeq` y la unión `RunEvent`; la Épica 5 completa el `StartupReconciler` y el ledger (middleware de `IChatClient`); la Épica 7 añade firmas de webhook y soporte real de Sentry en `codenetix fix`.
- Pregunta abierta que puede afectar a 1.5 y 1.7: tipo de token de GitHub en modo servidor (PAT *fine-grained* frente a GitHub App).
