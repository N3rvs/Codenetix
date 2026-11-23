---
title: 'Historia 7.3: Sentry: webhooks y correspondencia con el repositorio'
type: 'feature'
created: '2026-11-23'
status: 'done'
baseline_commit: '68b4a35628cfd894d47cf01ba73b6aea09681e59'
route: 'dispatch'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Los errores de producción que detecta Sentry no llegan a CodeNetix: `:5001/webhooks/sentry` responde 404 "no disponible todavía" y no hay forma de asociar un proyecto de Sentry a un Repositorio objetivo (FR-7, FR-8, UJ-2).

**Approach:** `POST /webhooks/sentry` en `:5001` (solo en `serve`, activado con `CODENETIX__Sentry__WebhookSecret`, el *Client Secret* de la integración interna de Sentry). Host lee el cuerpo crudo con límite y Ingestion verifica `Sentry-Hook-Signature` (HMAC SHA-256 hex del cuerpo con el secreto, tiempo constante), filtra (recurso `issue`, acción `created` = incidencia nueva, o `unresolved` por regresión) y resuelve el Repositorio objetivo **solo** con `CODENETIX__Sentry__Projects__<slug>=owner/repo` (AD-10). Sin correspondencia → Evento `Ignored` persistido antes del 2xx con el motivo visible en el Panel. Con correspondencia → se encola y un `BackgroundService` asegura (de forma idempotente, con un marcador) una **issue de seguimiento** en el Repositorio objetivo mediante un puerto nuevo del módulo GitHub y registra el Evento `Source = Sentry`, `ObjectKey = github:<owner>/<repo>#<n>` (el objeto que la cadena ya sabe procesar), clave `sentry:<issueId>` o `sentry:<issueId>:regression:<n>` (AD-4). Todos los Eventos del mismo error comparten objeto: el Dispatcher garantiza como mucho una Ejecución no terminal.

## Boundaries & Constraints

**Always:**
- **Configuración de instancia (AD-10), solo la lee Host:**
  - `CODENETIX__Sentry__WebhookSecret`: sin valor (o solo espacios) → webhook de Sentry desactivado: `POST :5001/webhooks/sentry` responde `404` "Webhook de Sentry no configurado" (nunca `401`). Con valor: al menos 16 caracteres, sin caracteres de control; se usa recortado. Lo cubre `SecretRedactor` (clave `WebhookSecret`); nunca se registra ni se persiste.
  - `CODENETIX__Sentry__Projects__<slug>=owner/repo` (o `Sentry: Projects: <slug>: owner/repo` en `/config/codenetix.instance.yml`): slug de proyecto de Sentry (`[a-z0-9][a-z0-9_-]{0,99}`, sin distinguir mayúsculas) → Repositorio objetivo (`owner/repo`, mismo formato que `CODENETIX__Polling__Repositories`). Como mucho 100 proyectos. Es la **única** fuente de la correspondencia (nunca `codenetix.yml`).
  - Valores inválidos → `Diagnostic` con un motivo en español que nombra la variable y nunca cita el valor (`InstanceOptionsValidator`); un slug inválido no se cita.
- **Orden en `POST /webhooks/sentry`** (Host, mismo esquema que 7.2):
  1. Puerto distinto de `:5001` → `404` (guarda de puerto).
  2. Método distinto de `POST` → `405`.
  3. Instancia no `Ready` → `503` (AD-18).
  4. Sin secreto → `404` "no configurado".
  5. Cuerpo de más de 1 MiB (por `Content-Length` o al leerlo) → `413`.
  6. `Sentry-Hook-Signature` ausente, repetida, mal formada (64 hex) o incorrecta → `401` "Firma no válida", sin `detail`; nada procesado. HMAC SHA-256 del cuerpo crudo con el secreto, `CryptographicOperations.FixedTimeEquals`.
  7. `Content-Type` distinto de `application/json` → `415`.
  8. `Sentry-Hook-Resource` distinto de `issue` (`installation`, `event_alert`, `metric_alert`, `error`, `comment`…), acción distinta de `created`/`unresolved`, o `unresolved` que no es una regresión → `200` `{ "result": "ignored", "reason": "…" }`; nada persistido.
  9. JSON ilegible, o sin `data.issue.id` numérico o `data.issue.project.slug` válido → `400` con el motivo.
  10. Proyecto sin correspondencia → Evento `Ignored` (`Source = Sentry`, `ObjectKey = sentry:<issueId>`, motivo "el proyecto de Sentry '<slug>' no tiene Repositorio objetivo: define CODENETIX__Sentry__Projects__<slug>") persistido **antes** de responder `200` `{ "result": "ignored", "reason": "…" }`; si la clave ya existía, la misma respuesta sin duplicar.
  11. Con correspondencia → encolado → `202` `{ "result": "queued" }`; mismo `Request-ID` aún en cola → `202` `{ "result": "duplicate" }`; cola llena (1000) → `503`.
  Ninguna respuesta llama a GitHub: el `2xx` sale en milisegundos (FR-7: < 1 s).
- **Regresión:** `unresolved` con `data.issue.substatus = regressed`; si el payload no trae `substatus`, cuenta como regresión cuando el `actor` no es un usuario (`actor.type != user`: lo reabrió Sentry, no una persona).
- **Claves (AD-4):** incidencia nueva → `sentry:<issueId>`; regresión → `sentry:<issueId>:regression:<n>` con `n` = 1 + la mayor `n` ya registrada para esa incidencia. Si un Evento previo de la incidencia guarda el mismo `Request-ID` (reentrega), es el mismo Evento: nada nuevo. Carrera del índice único → existente.
- **Procesado asíncrono (Ingestion, `Features/ReceiveSentryWebhook/`):** cola en memoria acotada + `BackgroundService` que espera `IInstanceHealth.WhenReadyAsync` y procesa **de una en una** (así dos entregas del mismo error nunca crean dos issues). Por entrega:
  - clave ya registrada (o mismo `Request-ID`) → nada;
  - issue de seguimiento: la de un Evento anterior de la misma incidencia de Sentry y el mismo repositorio (sin llamar a GitHub); si no hay, `ISentryTrackingIssues.EnsureAsync` (módulo GitHub): busca entre las issues con la etiqueta `codenetix-sentry` (todas, abiertas o cerradas, hasta 5 páginas) la que lleva el marcador `<!-- codenetix:sentry-issue:<issueId> -->` y, si no está, la crea con esa etiqueta y el marcador (idempotente ante reintentos y caídas entre crear y persistir);
  - GitHub no responde → reintento acotado (2, 5, 10 y 20 s); un rate limit corta los reintentos; agotados → nada persistido y aviso en el log. Repositorio inexistente o sin permiso para crear issues (404/410/403/422 sin rate limit) → Evento `Ignored` con el motivo (visible en el Panel);
  - Evento `Accepted`, `Source = Sentry`, `Actor = null`, `ObjectKey = github:<owner>/<repo>#<n>` + `IEventDispatchSignal.Notify()`.
- **Autorización (equivalente de FR-26):** Sentry no tiene actor de GitHub. Un error de Sentry solo genera trabajo con firma válida **y** un proyecto que el operador ha asociado explícitamente en la Configuración de instancia; nada del payload decide el repositorio.
- **Texto no confiable:** título, culprit, nivel, slug y permalink de Sentry son datos. En la issue de seguimiento el texto libre pasa por el saneador de comentarios del módulo GitHub (sin HTML, enlaces, menciones ni referencias), el permalink solo se incluye si es una URL `https` sin caracteres especiales, y el cuerpo deja claro que es contenido de Sentry tratado como dato. En `ing_events` el payload guarda solo `sentryIssueId`, `shortId`, `project`, `kind` (`created`/`regression`), `regression`, `title` (acotado, sin caracteres de control), `permalink`, `repository`, `issueNumber` y `deliveryId` — lo que necesita la 7.4 — nunca el JSON de Sentry, la firma ni el secreto.
- **Panel (FR-8, "motivo visible en el Panel"):** `GET /api/events/ignored?limit=N` (con `admin-token`; por defecto 20, máximo 100) devuelve los Eventos `Ignored` más recientes (`eventId`, `source`, `objectKey`, `reason`, `receivedAt`), y el Historial del Panel muestra una lista "Eventos ignorados" con Fuente, objeto, motivo y fecha. OpenAPI y `generated.ts` se regeneran.
- **Solo en `serve`:** `codenetix action` no registra la recepción ni el procesador de Sentry. El resto de `/webhooks/*` desconocido responde `404` "Fuente de webhook desconocida".
- **OpenAPI:** `/webhooks/sentry` se excluye del documento (vive en `:5001`).
- **README:** sección "Webhooks de Sentry": crear una integración interna, URL `https://<host>/webhooks/sentry`, recurso `issue`, *Client Secret* = `CODENETIX__Sentry__WebhookSecret`, correspondencia `CODENETIX__Sentry__Projects__<slug>`, issue de seguimiento y etiqueta `codenetix-sentry`, deduplicación y regresiones, cola en memoria.
- **Pruebas** en `tests/CodeNetix.Host.Tests` con `CodeNetixFactory` y `FakeGitHub` (sin red ni `:5000`/`:5001` reales: `TestServer` con `LocalPort` = 5001), una prueba por fila de la matriz y firmas HMAC reales; Panel con Vitest.

**Ask First:**
- Cambiar el esquema de `ing_events` o de `gh_*`, o modificar el Dispatcher o el orquestador.

**Never:**
- Crear Ejecuciones desde Ingestion o desde el endpoint (solo el Dispatcher).
- Llamar a GitHub (o a Sentry) antes de responder al webhook.
- Decidir el repositorio con datos del payload o con `codenetix.yml`.
- Responder `401` sin secreto configurado o con detalles de por qué falla la firma.
- Registrar o persistir el secreto, la firma o el JSON del webhook.
- Referenciar el módulo GitHub desde Ingestion (AD-1).
- Implementar el contexto de Sentry para el Analista ni `codenetix fix <url-de-sentry>` (7.4: `POST /api/events/manual` sigue respondiendo 501 a URLs de Sentry).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Incidencia nueva | `issue`/`created`, proyecto `front-end` → `o/r`, firma válida | `202` en < 1 s; después issue de seguimiento `o/r#N` con etiqueta y marcador, Evento `Accepted` `sentry:123`, objeto `github:o/r#N`, `Source = Sentry`; el Dispatcher crea la Ejecución | — |
| Proyecto sin correspondencia | `front-end` sin `Projects` | `200` `ignored` con el motivo; Evento `Ignored` `sentry:123` persistido; ninguna llamada a GitHub; aparece en `GET /api/events/ignored` | Reentrega: mismo Evento |
| Firma inválida | Sin cabecera, repetida, hex corto, no hex, otro secreto o cuerpo alterado | `401` sin detalle; nada encolado ni persistido | — |
| Sin secreto | `WebhookSecret` vacío | `404` "Webhook de Sentry no configurado" | — |
| `Diagnostic` | Falta la key del Proveedor | `503` | — |
| Puerto `:5000` | `POST /webhooks/sentry` en `:5000` | `404` | — |
| No aplica | `installation`; `event_alert`; `issue`/`resolved`; `unresolved` reabierta por una persona | `200` `ignored` con motivo; nada persistido | — |
| Reentrega / N eventos | Mismo `created` dos veces (en cola o ya procesado), otro `Request-ID` de la misma incidencia | Un único Evento y una única issue de seguimiento | Carrera del índice → existente |
| Regresión | `unresolved` `substatus=regressed` tras `created` | Evento `sentry:123:regression:1` sobre la misma issue de seguimiento (sin crear otra); la siguiente regresión → `:regression:2`; reentrega con el mismo `Request-ID` → nada | — |
| Ejecución activa | Regresión mientras la Ejecución anterior no ha terminado | El Dispatcher la marca `Ignored` (objeto ocupado): como mucho una no terminal | — |
| Issue ya creada | Caída entre crear la issue y persistir (issue con marcador existe, sin Evento) | Se reutiliza la issue existente; no se crea otra | — |
| GitHub no responde | `5xx` al listar o crear | Reintento acotado; nada persistido mientras falle | Agotados: aviso en el log |
| Repositorio inaccesible | `404`/`403` al crear la issue | Evento `Ignored` con el motivo | — |
| Cuerpo grande | > 1 MiB | `413` | — |
| Tipo de contenido | `text/plain` firmado | `415` | — |
| JSON inválido | Firma válida, cuerpo ilegible o sin `data.issue.id` | `400` | — |
| Cola llena | 1000 entregas pendientes | `503` | — |
| Método | `GET /webhooks/sentry` en `:5001` | `405` | — |
| Configuración inválida | Secreto de 8 caracteres; `Projects__front-end=foo`; slug `Front End!` | `Diagnostic` con un motivo por variable, sin valores | — |
| Modo `action` | Secreto configurado | No se registran ni la recepción ni el procesador | — |
| Texto hostil | Título con `@org/equipo`, `<img>`, `[x](http://…)`, `#1` | Issue de seguimiento sin menciones, HTML, enlaces ni referencias; payload acotado | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/SentryWebhooks.cs` (nuevo) -- `ISentryWebhookIngestion`, entrega/recibo, `SentryEventKey`, `ISentryTrackingIssues` y su petición/resultado.
- `src/CodeNetix.Contracts/EventDispatch.cs` -- `IIgnoredEventReader` + `IgnoredEventInfo`.
- `src/CodeNetix.Ingestion/Features/ReceiveSentryWebhook/` (nuevo) -- opciones, firma, recepción (filtro, correspondencia, cola), `SentryEventRecorder` (claves, reentregas, persistencia) y `SentryWebhookProcessor`.
- `src/CodeNetix.Ingestion/Features/ReceiveGitHubWebhook/GitHubWebhookSignature.cs` -- comparación HMAC compartida (`WebhookHmac`).
- `src/CodeNetix.Ingestion/Features/DispatchEvents/AcceptedEventSource.cs` -- lista de ignorados.
- `src/CodeNetix.GitHub/Features/TrackSentryIssue/SentryTrackingIssues.cs` (nuevo) -- busca o crea la issue de seguimiento con el saneador.
- `src/CodeNetix.Host/Features/ReceiveWebhook/ReceiveWebhookEndpoint.cs` -- `POST /webhooks/sentry`, comprobaciones comunes.
- `src/CodeNetix.Host/Features/ListIgnoredEvents/` (nuevo) -- `GET /api/events/ignored`.
- `src/CodeNetix.Host/Configuration/InstanceOptions.cs`, `InstanceOptionsValidator.cs`, `CodeNetixComposition.cs`, `ServeMode.cs` -- `Sentry`, validación y registro solo en `serve`.
- `panel/openapi/v1.json`, `panel/src/api/*`, `panel/src/components/IgnoredEventsList.tsx`, `RunHistoryView.tsx` -- lista de ignorados.
- `README.md` -- sección de Sentry.
- `tests/CodeNetix.Host.Tests/SentryWebhookTests.cs` (nuevo), `FakeGitHub.cs`, `AccessTests.cs`, `ActionModeTests.cs`, `InstanceConfigurationTests.cs`; `panel/src/__tests__/ignored.test.ts` (nuevo).

## Tasks & Acceptance

**Execution:**
- [x] `CodeNetix.Contracts` -- puertos de recepción, issue de seguimiento y lista de ignorados.
- [x] `CodeNetix.GitHub` -- `SentryTrackingIssues` idempotente.
- [x] `CodeNetix.Ingestion` -- firma, filtro, correspondencia, cola, procesador y registro.
- [x] `CodeNetix.Host` -- endpoints, opciones, validación, registro solo en `serve`; README.
- [x] Panel -- OpenAPI, cliente y lista de Eventos ignorados.
- [x] Pruebas -- una por fila.

**Acceptance Criteria:**
- Given el mapa de Sentry a repositorio en la Configuración de instancia, when llega un webhook de Sentry con firma válida, then se crea un Evento con la clave `sentry:<issueId>`, o `sentry:<issueId>:regression:<n>` si es una regresión (FR-7, FR-8).
- Given N eventos del mismo error, when llegan, then generan como mucho una Ejecución no terminal.
- Given un proyecto sin correspondencia, when llega su webhook, then queda como `ignored` con su motivo visible en el Panel.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde (dos veces seguidas).
- `npm --prefix panel run check:api` y `npm --prefix panel test` -- expected: en verde.

## Implementation Notes

- **Cómo se convierte un error de Sentry en trabajo:** issue de seguimiento en GitHub + Evento `Source = Sentry` sobre `github:<owner>/<repo>#<n>`. La cadena (clonado, Analista, aviso final, PR "Closes #n") está construida alrededor de una issue de GitHub (`GitHubObjectKey.TryParse` en cada Etapa del orquestador); un objeto `sentry:<id>` exigiría cambiar el orquestador (Ask First) y adelantar la 7.4. Así la 7.3 funciona de extremo a extremo sin tocar Pipeline, el Dispatcher da "como mucho una no terminal" por objeto y la 7.4 encuentra en el payload (`sentryIssueId`, `permalink`, `project`) lo que necesita para pedir la traza. Desviación consciente de AD-8 ("efectos los invoca el orquestador"): crear la issue es parte de normalizar la entrada (el objeto debe existir antes del Evento); lo hace el módulo GitHub tras el puerto `ISentryTrackingIssues` y es idempotente por marcador. Los Eventos sin Repositorio objetivo usan `ObjectKey = sentry:<id>` (nunca se despachan).
- **Persistencia antes del 2xx:** sí para el proyecto sin correspondencia (la clave se conoce sin E/S externa). Para el aplicable, cola en memoria como en la 7.2 (crear la issue exige llamar a GitHub; FR-7 < 1 s). Procesador con concurrencia 1: dos entregas del mismo error nunca crean dos issues; el volumen de Sentry es bajo.
- **Idempotencia de la issue:** primero los Eventos anteriores de la misma incidencia (sin llamar a GitHub); si no, listado `labels=codenetix-sentry&state=all` (5 páginas, más recientes primero) buscando el marcador en el cuerpo; el texto de Sentry pasa por `IssueCommentSanitizer`, que elimina comentarios HTML, así que el marcador no se puede falsificar. Un 201 ilegible se trata como `Unavailable` y el reintento la encuentra.
- **Regresiones:** `n` = 1 + la mayor ya registrada para esa incidencia. Una entrega es "la misma" si su **cuerpo firmado** (SHA-256 del cuerpo crudo, guardado como `bodySha256` en el payload) o su `Request-ID` coinciden con los de un Evento anterior de la incidencia: el `Request-ID` no está firmado, así que repetir un cuerpo capturado con otro `Request-ID` (o sin él) no crea nada (revisión 1, parche 1). Resolver la clave e insertar ocurre bajo un cerrojo del `SentryEventRecorder` (compartido por la ruta síncrona sin correspondencia y el procesador) más el índice único: dos regresiones simultáneas reciben números consecutivos, sin repetir ni saltar.
- **Recurso elegido:** `issue` (`created`, `unresolved` con `substatus = regressed`; sin `substatus`, regresión solo si `actor.type = sentry` — usuario, `application` o actor ausente no lo son; revisión 1, parche 2). `event_alert` no se procesa: su payload trae el id numérico del proyecto, no el slug, y dispara por evento, no por incidencia.
- **Firma:** `WebhookHmac` (nuevo, compartido con GitHub): 64 hex (mayúsculas o minúsculas), `FixedTimeEquals`; cabecera repetida o con prefijo → `401`. El *timestamp* de Sentry no está firmado, así que no se usa como protección de repetición; la deduplicación por el hash del cuerpo firmado hace inocua una repetición.
- **Panel:** `GET /api/events/ignored` (puerto `IIgnoredEventReader` nuevo para no romper los dobles de `IEventReader`) y lista plegable "Eventos ignorados" en el Historial; OpenAPI y `generated.ts` regenerados (`check:api` en verde).
- **`/webhooks/*` desconocido:** "Fuente de webhook desconocida" (ya no "no disponible todavía"); `AccessTests` ajustado.
- **Sin cambios** de esquema, Dispatcher, orquestador ni Dockerfile (no se ha ejecutado `docker build`; cambian el README y el Panel que la imagen compila, y `npm --prefix panel run build` pasa).
- **Verificación:** `dotnet build CodeNetix.slnx` 0 advertencias. `dotnet test --solution CodeNetix.slnx`: la primera ejecución dio 1179/1180 con 1 fallo que no se pudo identificar (la salida se perdió; no se reprodujo); las tres siguientes, seguidas, 1180/1180. `SentryWebhookTests` (24 tests / 40 casos) en verde cinco veces. Panel: `check:api` al día y 79 tests de Vitest en verde (4 nuevos).
- **Parches de la revisión 1:**
  1. Deduplicación por contenido firmado (`bodySha256`) además del `Request-ID`, también en la lista de pendientes en memoria (ahora por hash del cuerpo); numeración atómica bajo cerrojo. Tests: repetir el cuerpo capturado con otro `Request-ID`, sin él y varias veces → ningún Evento nuevo; una regresión con otro `lastSeen` → `:regression:2`; 8 regresiones simultáneas sin correspondencia → 1..8 consecutivos.
  2. Sin `substatus`, solo `actor.type = sentry` es regresión. Tests: `application`, `user` y actor ausente → `ignored`; `sentry` → regresión.
  3. Rate limit: se espera la mayor entre la pauta y el `Retry-After` de GitHub, con tope `MaxRetryAfter` (5 min), dentro de los mismos 5 intentos. Al abandonar (intentos agotados, cola llena al reencolar o fallo inesperado) se registra un Evento `Ignored` "no procesada (…); se procesará si Sentry vuelve a enviarla" con clave `sentry-unprocessed:<id>:<hash16>` y objeto `sentry:<id>`: visible en el Panel y sin consumir la clave de la incidencia (una reentrega posterior se procesa). Tests: 5xx agotado → Evento "no procesada" y la reentrega posterior queda `Accepted`; 429 con `Retry-After: 3600` → espera acotada al tope (log) y Evento "no procesada"; 429 que luego se levanta → `Accepted`.
  4. `FakeGitHub` filtra el listado por `state` (por defecto `open`, como GitHub) y devuelve el estado de cada issue. Test: una issue de seguimiento **cerrada** con el marcador se reutiliza sin crear otra.
  5. Título: sin caracteres de control ni de formato Unicode (Cf: bidi, ancho cero), en una línea y con menciones, `#n`, `owner/repo#n`, `GH-n`, `//` y `www.` rotos con U+200B (`IssueCommentSanitizer.BreakReferences`, extraído del saneador). El cuerpo también descarta los Cf. El test de texto hostil comprueba el título.
  7. Si el 201 no trae la etiqueta `codenetix-sentry` (GitHub la descarta en silencio sin permiso de escritura) se registra un aviso. Tests con y sin etiqueta.
  8. Rechazo: filas 410 y 422 añadidas.
  9. Test: dos regresiones sin correspondencia + reentrega de la primera → `:regression:1`, `:regression:2` y nada más.
  10. Test: dos `created` sin `Request-ID` → un Evento y una issue.
  - Hallazgo 6 (issue de seguimiento borrada o transferida se reutiliza siempre) aplazado. `SentryWebhookTests`: 37 tests / 53 casos.
  - Verificación tras los parches: `dotnet build CodeNetix.slnx` 0 advertencias; `dotnet test --solution CodeNetix.slnx` en verde dos veces seguidas (1193: la primera 1192 + 1 omitido —prueba de fuego del Sandbox sin Docker—, la segunda 1193/1193). Sin cambios de API ni de Panel (no aplica `check:api`/Vitest).
- **Riesgos / pendientes:**
  - Pérdida de entregas en cola al reiniciar (sin bandeja persistente; Sentry no reentrega solo).
  - Un cuerpo legítimamente idéntico a uno anterior (misma incidencia, mismos campos, incluido `lastSeen`) se trata como la misma entrega; en la práctica dos avisos distintos de Sentry difieren al menos en `lastSeen`.
  - Un rechazo definitivo de GitHub (404/403/422 al crear la issue) consume la clave como `Ignored`: tras corregir el token, la incidencia solo vuelve a entrar con su siguiente regresión.
  - El texto de un error de producción puede contener datos controlados por usuarios externos: llega al Analista como dato, el Sandbox no tiene red ni secretos y el destino es siempre un PR revisado por una persona; aun así es una superficie de inyección de prompts mayor que la de una issue etiquetada por alguien con escritura.
  - No probado contra Sentry ni GitHub reales (forma del payload según la documentación de integraciones de Sentry; `FakeGitHub`).

## Spec Change Log

## Review Triage Log

Revisión 1 (Claude, revisor combinado, 10 hallazgos):

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | medium-high | Reenviar un aviso de regresión firmado con otro `Request-ID` crea Ejecuciones sin límite (coste LLM) | patch |
| 2 | medium | `unresolved` sin `substatus` cuenta como regresión con actor ausente o `application` | patch |
| 3 | medium | Rate limit o cola llena: la entrega desaparece solo con un log, sin rastro en el Panel | patch |
| 4 | medium | Nada verifica que la búsqueda de la issue de seguimiento incluya cerradas (`state=all`) | patch |
| 5 | low | El título de la issue de seguimiento conserva referencias, URLs y caracteres bidi | patch |
| 6 | low | Issue de seguimiento borrada o transferida se reutiliza para siempre | defer |
| 7 | low | Si GitHub descarta la etiqueta al crear, la búsqueda por marcador falla | patch (warning) |
| 8 | low | 422 y 410 sin test | patch |
| 9 | low | Regresiones de proyectos no mapeados sin test | patch |
| 10 | low | Entregas sin `Request-ID` sin test | patch |
