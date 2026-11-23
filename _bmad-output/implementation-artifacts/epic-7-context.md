# Epic 7 Context: Entrada automática: sondeo, webhooks y Sentry

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Que el trabajo llegue solo, sin lanzar nada a mano: en `serve`, CodeNetix recoge las issues abiertas con la etiqueta configurada mediante un sondeo periódico (sin URL pública), recibe webhooks de GitHub (al instante, para quien tiene servidor) y webhooks de Sentry (errores nuevos o en regresión, asociados a un Repositorio objetivo por la Configuración de instancia), y el Analista recibe la traza completa del error de Sentry como datos (FR-5, FR-6, FR-7, FR-8, FR-26).

## Stories

- Story 7.1: Sondeo de issues etiquetadas
- Story 7.2: Webhooks de GitHub
- Story 7.3: Sentry: webhooks y correspondencia con el repositorio
- Story 7.4: Contexto de Sentry para el Analista

## Requirements & Constraints

- **Sondeo (FR-6):** consulta periódica de las issues abiertas con la etiqueta configurada (por defecto `codenetix`) en los repositorios configurados; intervalo configurable, 5 minutos por defecto. Una issue con una Ejecución activa o terminada no genera otra, **salvo que se reetiquete**. En `Diagnostic` el sondeo no se ejecuta.
- **Webhooks (FR-7):** endpoints para GitHub (issue etiquetada) y Sentry (incidencia nueva o en regresión). Firma inválida → 401 y no se procesa; respuesta 2xx en menos de 1 s y procesado asíncrono; deduplicación por ID de entrega y por incidencia (N eventos del mismo error de Sentry → como mucho una Ejecución activa). En `Diagnostic`, 503.
- **Sentry → repositorio (FR-8):** cada proyecto de Sentry se asocia a un Repositorio objetivo **solo** por la Configuración de instancia (nunca por `codenetix.yml`). Sin correspondencia → Evento `ignored` con el motivo visible en el Panel.
- **Contexto de Sentry (Story 7.4, FR-5):** con `Sentry:AuthToken` (y `Sentry:BaseUrl` opcional, OQ-5 autoalojado), el Analista recibe traza, contexto y frecuencia como `UntrustedText`; `codenetix fix <url-de-sentry>` deja de responder "próximamente" y crea el Evento.
- **Autorización (FR-26):** una issue de GitHub solo se procesa si **quien puso la etiqueta** tiene escritura (`admin`/`maintain`/`write`) en el Repositorio objetivo; si no, `ignored: no autorizado`. Si GitHub no responde se falla cerrado (no se persiste nada). El texto del Evento siempre es dato, nunca instrucciones.
- **Prioridad:** P1 (operación real). Sentry solo existe en `serve` (el modo Action no lo soporta).

## Technical Decisions

- **Ingestion es la única entrada de trabajo (AD-4):** toda Fuente produce un `EventEnvelope` (Fuente, objeto, clave de deduplicación, actor, payload como datos). Antes de responder 2xx: firma (webhooks), permiso del actor (FR-26) y persistencia con clave única en `ing_events`. Rechazados → `ignored` con motivo. **Solo el Dispatcher** de Pipeline crea `PipelineRun`, con como mucho una Ejecución no terminal por objeto (si el objeto está ocupado al despachar, el Evento pasa a `Ignored`).
- **Claves de deduplicación (AD-4):**
  - GitHub, **compartida por sondeo y webhook**: `gh:<owner>/<repo>#<issue>:labeled:<labelEventId>` (el id del evento `labeled` de la API de eventos de la issue; el payload del webhook no lo trae, así que la 7.2 debe resolverlo con el mismo puerto que la 7.1).
  - Sentry: `sentry:<issueId>`, renovada como `sentry:<issueId>:regression:<n>` tras una regresión.
  - Manual: `manual:<url>:<requestId>`; Action: `gh-action:<owner>/<repo>#<n>:run:<runId>:<attempt>` (6.1).
  - Objeto común de GitHub: `github:<owner>/<repo>#<n>` en minúsculas (`GitHubObjectKey`), igual en todas las Fuentes.
- **Módulos (AD-1, AD-8):** Ingestion solo referencia `CodeNetix.Contracts`; las llamadas a la API de GitHub viven en el módulo GitHub detrás de puertos de Contracts (`IRepositoryPermissions`, `IGitHubIdentity`, …). Solo Host lee el entorno y compone (`ArchitectureTests`).
- **Configuración de instancia (AD-10):** `CODENETIX__*` + `/config/codenetix.instance.yml` opcional; contiene secretos de webhook, `Sentry:AuthToken`/`Sentry:BaseUrl` y el mapa Sentry → repositorio. Valores inválidos → `Diagnostic` con motivos en español sin valores secretos (`InstanceOptionsValidator`).
- **Puertos (AD-17):** `:5000` Panel, `/api`, `/hubs` (con `admin-token`); `:5001` solo `/webhooks/*` (lo único exponible a internet; solo verifica la firma, sin `admin-token`; `WebhookPortGuardMiddleware` ya separa los puertos). Los tests nunca enlazan `:5000`/`:5001`.
- **Arranque (AD-18):** `StartupReconciler` (migraciones → salud → reconciliación → limpieza → `Ready`/`Diagnostic`); después arrancan el Dispatcher **y el sondeo**. En `Diagnostic` ningún `BackgroundService` procesa (esperan `IInstanceHealth.WhenReadyAsync`) y los webhooks responden 503.
- **Un binario, tres modos (AD-12):** el sondeo y los webhooks son de `serve`; `codenetix action` no abre `:5001` ni sondea; `fix` es un cliente ligero.
- **Secretos (AD-13):** un único `ISecretRedactor` en logs y en el sumidero de `RunEvent`; ni tokens ni secretos de webhook en logs ni en `ing_events`.
- **Convenciones:** slices `Features/<Verbo><Sustantivo>/`, ids `Guid.CreateVersion7()`, fechas UTC `DateTimeOffset`, `IOptions<T>` por módulo, `TimeProvider` inyectable.

## Cross-Story Dependencies

- **7.1 → 7.2:** el webhook reutiliza el puerto de GitHub que localiza el evento `labeled` más reciente y el formateador de la clave `gh:…:labeled:…`, de modo que sondeo y webhook deduplican entre sí.
- **7.3 → 7.4:** el Evento de Sentry (clave y mapa a repositorio) es la entrada del contexto que recibe el Analista y del `codenetix fix <url-de-sentry>`.
- **Épicas previas:** reutiliza `IRepositoryPermissions` (1.5), el Dispatcher y su semántica de objeto ocupado (1.6/5.3), `ManualUrlParser` (1.4, Sentry "próximamente") y la composición común de `serve`/`action` (6.1).
