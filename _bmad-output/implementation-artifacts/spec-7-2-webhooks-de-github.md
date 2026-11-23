---
title: 'Historia 7.2: Webhooks de GitHub'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: 'f4484779b6ad6403679f810385df9e9bba7efefb'
route: 'dispatch'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Quien tiene servidor solo puede automatizar con el sondeo (7.1), que tarda hasta un intervalo (5 minutos por defecto) en recoger cada issue etiquetada. `:5001/webhooks/*` existe pero responde 404 "no disponible todavía" (FR-7).

**Approach:** `POST /webhooks/github` en `:5001` (solo en `serve`, activado al definir `CODENETIX__GitHub__WebhookSecret`). Host lee el cuerpo crudo con un límite de tamaño y se lo pasa a Ingestion, que verifica la firma HMAC SHA-256 (`X-Hub-Signature-256`) en tiempo constante, filtra (`ping`, `issues`/`labeled` con la etiqueta de disparo, issue abierta, repositorio permitido) y **encola** el disparo en memoria; la respuesta (2xx) sale sin llamar a GitHub. Un `BackgroundService` de Ingestion resuelve el id del evento `labeled` con el mismo puerto que el sondeo (`ILabeledIssueSource`, con reintentos acotados si GitHub aún no lo muestra) y registra el Evento con **la misma clave** `gh:<owner>/<repo>#<n>:labeled:<labelEventId>` y la misma lógica que el sondeo (deduplicación, "lanzada por otra Fuente", FR-26), extraída a un componente común: un webhook y un sondeo del mismo etiquetado nunca crean dos Eventos.

## Boundaries & Constraints

**Always:**
- **Configuración de instancia (AD-10), solo la lee Host:**
  - `CODENETIX__GitHub__WebhookSecret`: sin valor (o solo espacios) → webhook de GitHub desactivado: `POST :5001/webhooks/github` responde `404` "Webhook de GitHub no configurado" (nunca `401`) y no se registra nada. Con valor: al menos 16 caracteres, sin caracteres de control; se usa recortado. Lo cubre el `SecretRedactor` (clave `WebhookSecret`); nunca se registra ni se persiste.
  - `CODENETIX__GitHub__WebhookRepositories` (opcional): lista `owner/repo` con el mismo formato y tope (100) que `CODENETIX__Polling__Repositories`. Vacía → se acepta cualquier repositorio que envíe un webhook firmado; con valor, los demás se ignoran.
  - **Etiqueta de disparo compartida:** el webhook usa `CODENETIX__Polling__Label` (por defecto `codenetix`), la misma que el sondeo: una sola etiqueta por instancia, coherente con la clave compartida. Se documenta como "etiqueta de disparo (sondeo y webhook)".
  - Valores inválidos → `Diagnostic` con un motivo en español que nombra la variable y nunca cita el valor (`InstanceOptionsValidator`).
- **Orden en `POST /webhooks/github`** (Host, `Features/ReceiveWebhook/`):
  1. Puerto distinto de `:5001` → `404` (guarda de puerto existente, intacta).
  2. Método distinto de `POST` → `405`.
  3. Instancia no `Ready` (`Starting`/`Diagnostic`) → `503`, como hoy (AD-18).
  4. Sin secreto → `404` "no configurado".
  5. Cuerpo de más de 1 MiB (por `Content-Length` o al leerlo) → `413`, sin verificar ni procesar.
  6. Firma ausente, mal formada (`sha256=` + 64 hex) o incorrecta → `401` "Firma no válida", sin `detail` ni pistas, y nada procesado. HMAC SHA-256 del cuerpo crudo con el secreto; comparación con `CryptographicOperations.FixedTimeEquals`.
  7. `Content-Type` distinto de `application/json` (p. ej. el `x-www-form-urlencoded` de GitHub) → `415` con el motivo (configurar *Content type* `application/json`).
  8. `X-GitHub-Event: ping` → `200` `{ "result": "pong" }`.
  9. Otro evento, otra acción, otra etiqueta, pull request, issue cerrada o repositorio fuera de la lista → `200` `{ "result": "ignored", "reason": "…" }` (motivo en español, texto del payload acotado y sin caracteres de control) y nada encolado ni persistido.
  10. JSON ilegible o sin `issue.number`, `repository.full_name` o `sender.login` válidos (con firma válida) → `400` con el motivo.
  11. Aplicable → encolado → `202` `{ "result": "queued" }`; misma `X-GitHub-Delivery` aún en cola → `202` `{ "result": "duplicate" }` sin encolar otra vez; cola llena (1000) → `503`.
  Las respuestas 1–11 no llaman a GitHub: el `2xx` sale en milisegundos (FR-7: < 1 s; GitHub corta a los 10 s).
- **Procesado asíncrono (Ingestion, `Features/ReceiveGitHubWebhook/`):** cola en memoria acotada + `BackgroundService` que espera `IInstanceHealth.WhenReadyAsync` y procesa hasta 4 entregas a la vez. Por entrega:
  - `ILabeledIssueSource.FindLatestLabelEventAsync` (el mismo que el sondeo). Si no hay evento `labeled` de la etiqueta, o el más reciente es **anterior** al etiquetado del webhook (`created_at` < `issue.updated_at` del payload − 60 s: la API aún no muestra el nuevo), o GitHub no responde → reintento acotado (esperas 2, 5, 10 y 20 s; 5 intentos, ~37 s). Un rate limit corta los reintentos. Agotados → nada persistido y aviso en el log ("lo recuperan el sondeo o «Redeliver» en GitHub").
  - Con el evento: **componente común con el sondeo** (`LabelEventRecorder`): clave ya en `ing_events` → nada; Evento `Accepted` de otra Fuente (`Source != GitHub`) del mismo objeto recibido después de la etiqueta → `Ignored` "ya lanzada por otra Fuente después de etiquetarla"; si no, FR-26 sobre `actor` del evento `labeled` (quien etiquetó, el `sender` del webhook): `Granted` → `Accepted` + `IEventDispatchSignal.Notify()`; `Denied` o sin actor → `Ignored` "no autorizado"; `Unavailable` → nada persistido y se reintenta con la misma pauta (falla cerrado). Carrera del índice único → existente.
  - `Source = GitHub`, `ObjectKey = GitHubObjectKey.Format`, `DedupKey = GitHubLabelEventKey.Format`; payload: `repository`, `issueNumber`, `label`, `labelEventId`, `labeledAt` y `deliveryId` (`X-GitHub-Delivery`, trazabilidad); nunca el JSON de GitHub, la firma ni el secreto. El sondeo persiste el mismo payload sin `deliveryId`.
  - `X-GitHub-Delivery` (GUID; otro formato se trata como ausente) aparece en los logs de recepción y de procesado.
  - Reentrega (`Redeliver`) idempotente: mismo evento `labeled` → misma clave → ningún Evento nuevo.
- **Solo en `serve`:** `codenetix action` no registra la recepción ni el procesador (no abre `:5001`). El resto de `/webhooks/*` (Sentry, 7.3) sigue con la respuesta provisional.
- **OpenAPI:** la ruta se excluye del documento (no es API del Panel; vive en `:5001`): `panel/openapi/v1.json` no cambia.
- **README:** sección "Webhooks de GitHub": publicar `:5001`, URL `https://<host>/webhooks/github`, *Content type* `application/json`, *Secret* = `CODENETIX__GitHub__WebhookSecret`, eventos "Issues"; etiqueta compartida, autorización, deduplicación con el sondeo, la cola en memoria y cómo recuperar una entrega perdida.
- **Pruebas** en `tests/CodeNetix.Host.Tests` con `CodeNetixFactory` y `FakeGitHub` (sin red, sin enlazar `:5000`/`:5001`: `TestServer` con `LocalPort` = 5001): una prueba por fila de la matriz, con firmas HMAC reales, y una de deduplicación webhook + sondeo en ambos órdenes.

**Ask First:**
- Cambiar el esquema de `ing_events` (p. ej. una bandeja de entregas persistente) o modificar el Dispatcher.

**Never:**
- Crear Ejecuciones desde Ingestion o desde el endpoint (solo el Dispatcher, AD-4).
- Llamar a GitHub antes de responder al webhook.
- Responder `401` sin secreto configurado, o dar en un `401` detalles de por qué falla la firma.
- Registrar o persistir el secreto, la firma o el JSON del webhook.
- Referenciar el módulo GitHub desde Ingestion (AD-1; solo Contracts).
- Implementar Sentry (7.3/7.4).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Issue etiquetada | `issues`/`labeled` `codenetix`, `o/r#12`, firma válida; evento `labeled` 900 de `alice` con `write` | `202` en < 1 s (aunque GitHub tarde); después un Evento `Accepted`, clave `gh:o/r#12:labeled:900`, actor `alice`, `deliveryId` en el payload; el Dispatcher crea la Ejecución | — |
| Firma inválida | Sin cabecera, `sha1=`, hex corto o firma de otro cuerpo/secreto | `401` sin detalle; nada encolado, ninguna llamada a GitHub | — |
| Sin secreto | `WebhookSecret` vacío | `404` "Webhook de GitHub no configurado" (también con firma) | — |
| `Diagnostic` / `Starting` | Falta la key del Proveedor | `503` | — |
| Puerto `:5000` | `POST /webhooks/github` en `:5000` | `404` (guarda de puerto) | — |
| `ping` | `X-GitHub-Event: ping` firmado | `200` `pong` | — |
| No aplica | `push`; `issues`/`opened`; etiqueta `bug`; PR; issue cerrada | `200` `ignored` con motivo; nada persistido | — |
| Repositorio no permitido | `WebhookRepositories=o/s`, llega `o/r` | `200` `ignored`; nada persistido ni llamado | — |
| Sin permiso | `alice` con `read` | Evento `Ignored` "no autorizado", sin Ejecución | — |
| Webhook + sondeo | Mismo etiquetado por las dos vías, en cualquier orden | Un único Evento | Carrera del índice → existente |
| Lanzada por otra Fuente | `codenetix fix` aceptado después de etiquetar | Evento `Ignored` "ya lanzada por otra Fuente…", sin permiso | — |
| Reentrega | Misma entrega dos veces (en cola o ya procesada) | `202` (`duplicate` si aún en cola); un único Evento | — |
| Evento aún no visible | La API de eventos aún no trae el `labeled` (o solo uno antiguo) | Reintenta y, cuando aparece, crea el Evento con el id nuevo | Agotados: nada persistido, aviso en el log |
| GitHub no responde | `5xx` en eventos o permiso | Reintento acotado; nada persistido mientras falle | Log sin secreto ni token |
| Cuerpo grande | > 1 MiB | `413` | — |
| Formulario | `Content-Type: application/x-www-form-urlencoded` firmado | `415` con el motivo | — |
| JSON inválido | Firma válida, cuerpo ilegible o sin `issue.number` | `400` | — |
| Cola llena | 1000 entregas pendientes | `503` | GitHub permite reentregar |
| Método | `GET /webhooks/github` en `:5001` | `405` | — |
| Configuración inválida | Secreto de 8 caracteres; repo `foo` en `WebhookRepositories` | `Diagnostic` con un motivo por variable | — |
| Modo `action` | Secreto configurado | No se registra ni la recepción ni el procesador | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/GitHubWebhooks.cs` (nuevo) -- `IGitHubWebhookIngestion`, `GitHubWebhookDelivery`, `GitHubWebhookReceipt`/`Outcome`.
- `src/CodeNetix.Ingestion/Features/RecordLabelEvent/LabelEventRecorder.cs` (nuevo) -- clave, otra Fuente, FR-26 y persistencia, común a sondeo y webhook.
- `src/CodeNetix.Ingestion/Features/PollGitHub/GitHubPoller.cs` -- usa `LabelEventRecorder`.
- `src/CodeNetix.Ingestion/Features/ReceiveGitHubWebhook/` (nuevo) -- `GitHubWebhookOptions`, `GitHubWebhookSignature`, `GitHubWebhookIngestion` (filtro y cola), `GitHubWebhookProcessor` (`BackgroundService`); `AddCodeNetixGitHubWebhooks`.
- `src/CodeNetix.Ingestion/Features/SubmitGitHubActionEvent/GitHubActionEventIngestion.cs` -- utilidades de lectura del payload compartidas.
- `src/CodeNetix.Host/Features/ReceiveWebhook/ReceiveWebhookEndpoint.cs` -- `POST /webhooks/github`.
- `src/CodeNetix.Host/Configuration/InstanceOptions.cs`, `InstanceOptionsValidator.cs`, `CodeNetixComposition.cs`, `ServeMode.cs` -- `WebhookSecret`, `WebhookRepositories`, validación y registro solo en `serve`.
- `README.md` -- sección de webhooks.
- `tests/CodeNetix.Host.Tests/GitHubWebhookTests.cs` (nuevo), `AccessTests.cs`, `GitHubPollingTests.cs`, `ActionModeTests.cs`.

## Tasks & Acceptance

**Execution:**
- [x] `CodeNetix.Contracts` -- puerto de recepción del webhook.
- [x] `CodeNetix.Ingestion` -- `LabelEventRecorder` extraído del sondeo; firma, filtro, cola y procesador con reintentos.
- [x] `CodeNetix.Host` -- endpoint, opciones, validación, registro solo en `serve`; README.
- [x] `tests/CodeNetix.Host.Tests` -- una prueba por fila; ajustar las de puertos.

**Acceptance Criteria:**
- Given un webhook de GitHub en `:5001/webhooks/github`, when la firma es válida, then responde 2xx en menos de 1 s y procesa el Evento de forma asíncrona, con la misma clave de deduplicación que el sondeo (FR-7).
- Given una firma no válida, when llega el webhook, then responde 401 y no se procesa.
- Given la instancia en `Diagnostic`, when llega el webhook, then responde 503.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde (dos veces seguidas).

## Implementation Notes

- **Síncrono vs. en cola:** en cola. Resolver el `labelEventId` exige 1–N peticiones a la API de eventos más la de permiso, y a veces reintentos de decenas de segundos (consistencia eventual de GitHub): incompatible con FR-7 (< 1 s) y con el corte de 10 s de GitHub. El endpoint solo verifica firma y filtra (sin E/S) y encola. Desviación consciente de AD-4 ("persistir antes del 2xx"): la clave de deduplicación no se conoce sin llamar a GitHub y una bandeja persistente sería un cambio de esquema (Ask First). La cola es en memoria: se pierde en un reinicio; lo recuperan el sondeo (misma clave) o *Redeliver* (idempotente). Documentado en el README.
- **Componente común:** `LabelEventRecorder` (`Features/RecordLabelEvent/`) es la antigua mitad de `GitHubPoller.ProcessIssueAsync` (clave, "otra Fuente", FR-26, persistencia y carrera del índice); el sondeo y el webhook lo usan igual. `GitHubPoller.AlreadyLaunchedReason` sigue existiendo (alias). El payload usa `WhenWritingNull`: el del sondeo ya no escribe `"labeledAt":null` cuando GitHub no da fecha (cambio menor) y nunca lleva `deliveryId`.
- **FR-26:** se autoriza al `actor` del evento `labeled` resuelto (el mismo criterio que el sondeo, para que una clave tenga una sola decisión). Normalmente es el `sender` del webhook; si difiere, un etiquetado posterior sustituyó al de la entrega y se registra ese (log informativo).
- **Evento aún no visible:** se considera "no visible" si no hay `labeled` de la etiqueta o si el más reciente tiene `created_at` anterior a `issue.updated_at` del payload − 5 s (60 s antes de la revisión 1) (si no, una entrega nueva podría resolver un etiquetado antiguo ya registrado y perderse en silencio). Esperas 2/5/10/20 s (5 intentos); un rate limit corta los reintentos. `RetryDelays` es configurable solo en código (las pruebas usan 50–300 ms).
- **Etiqueta:** compartida, `CODENETIX__Polling__Label` (una etiqueta de disparo por instancia). **Lista de repositorios:** propia (`CODENETIX__GitHub__WebhookRepositories`), opcional, leída en crudo como la del sondeo.
- **Respuestas:** `ignored` es `200` con motivo (no `204`): el motivo aparece en *Recent Deliveries* de GitHub. `405` para otros métodos. Sin secreto, el servicio está registrado pero `Enabled = false` → `404`; fuera de `serve` no está registrado y el endpoint lo trata igual (se resuelve con `GetService`, no como parámetro).
- **Seguridad:** HMAC con `CryptographicOperations.FixedTimeEquals` sobre los bytes crudos; la cabecera se valida por formato antes (`sha256=` en minúsculas, exactamente como lo envía GitHub, + 64 hex en cualquier caso; una cabecera repetida es inválida). El `401` no lleva `detail`. `X-GitHub-Delivery` solo se acepta como GUID (otro valor nunca llega a los logs). El secreto ya estaba en `SecretRedactor` (`WebhookSecret`); además nunca se registra (prueba con logs sin redactar).
- **Procesador:** `BackgroundService` que espera `Ready`, hasta 4 intentos a la vez (un reintento se reencola tras su espera, sin ocupar hueco); cada entrega se libera de la lista de pendientes al terminar, así un *Redeliver* posterior vuelve a encolarse (y da `Existing`).
- **Sin cambios** de esquema, Dispatcher, OpenAPI (ruta con `ExcludeFromDescription`; no aplica `check:api`) ni Dockerfile (no se ha ejecutado `docker build`; solo cambia el README que la imagen copia).
- **Pruebas:** `GitHubWebhookTests` (27 tests / 42 casos) con firmas HMAC reales (incluido el vector de la documentación de GitHub), deduplicación webhook→sondeo y sondeo→webhook; `AccessTests` ajustado (`/webhooks/github` sin secreto → "no configurado"; el resto de `/webhooks/*` sigue provisional); `ActionModeTests` comprueba que `action` no registra ni la recepción ni el procesador.
- **Verificación:** `dotnet build CodeNetix.slnx` 0 advertencias; `dotnet test --solution CodeNetix.slnx` en verde dos veces seguidas (1120 tests; la primera 1119 + 1 omitido —la prueba de fuego del Sandbox real sin Docker—, la segunda 1120/1120).
- **Parches de la revisión 1:**
  1. Margen de frescura 60 s → 5 s (las dos fechas son de GitHub, con precisión de segundos): un reetiquetado rápido ya no se resuelve al etiquetado anterior, ya registrado (`Existing`), y se pierde. Con `updated_at` en el payload, la comprobación va **antes** de registrar, así que un `Existing` con un evento más antiguo que `updated_at − margen` ya no puede darse (se reintenta como "aún no visible"). Tests: −4 s aceptado al primer intento, −6 s reintentado y descartado; reetiquetado a los 30 s (dentro del margen antiguo) → dos Eventos (900 y 950). Las pruebas usan la misma fecha (`LabeledAt`) en el evento falso y en el payload.
  2. Tests de cuerpo sin `Content-Length` (> 1 MiB → `413`, nada encolado) y frontera exacta con y sin `Content-Length` (1 MiB → `202`; 1 MiB + 1 → `413`).
  3. 404/410 en la API de eventos (issue borrada o transferida, o repositorio invisible para el token) es **definitivo**: `LabelEventLookup.IssueNotFound` (sigue siendo `NotFound`, con `IssueMissing = true`; el sondeo no cambia) y el procesador lo descarta sin reintentar. Los reintentos ya no duermen dentro de un hueco: cada intento libera el suyo y programa el siguiente (`TryRequeue` tras la espera; con la cola llena, descarte con log). Test: con `MaxConcurrency = 1` y la #12 reintentando cada 30 s, la #13 se procesa enseguida.
  4+8. `WriteAccess.RateLimited` (propiedad `init`, compatible) lo marca `RepositoryPermissions`; `LabelEventRecorder` lo propaga en `Unavailable` y el webhook deja de reintentar. El sondeo también lo aprovecha: un rate limit en el permiso corta el ciclo con backoff (resuelve el hallazgo 6 diferido de la 7.1). Tests: eventos `429`, eventos `403` con cuota agotada y permiso `403` con cuota agotada → una sola llamada y el log de descarte. `GitHubModuleTests` actualizado (los `WriteAccess` de rate limit llevan `RateLimited = true`).
  5. Con `issue.updated_at` en el payload y un evento sin `created_at` → "aún no visible" (reintento). Si el payload no trae `updated_at` y el resultado es `Existing`, aviso `Warning` (no se pudo comprobar que fuera el etiquetado de esa entrega). Tests de ambos.
  9. Test: `sender` `bob`, evento de `mallory` con `read` → `Ignored` "no autorizado" con actor `mallory`; nunca se consulta el permiso de `bob`.
  10. Test: dos entregas firmadas con ids distintos del mismo etiquetado, en paralelo → un único Evento y ningún "fallo inesperado" (la carrera del índice único se trata como existente).
  11. Prefijo: **estricto en minúsculas** (`SHA256=` → `401`), como lo envía GitHub y como dice la spec; el hex admite mayúsculas. Cabecera `X-Hub-Signature-256` repetida → `401`. Ambas variantes en la teoría de firmas no válidas.- **Riesgos / pendientes:**
  - Pérdida de entregas en cola al reiniciar (sin bandeja persistente); mitigado por sondeo y *Redeliver*.
  - El criterio de frescura compara `created_at` del evento con `updated_at` del payload (ambos de GitHub, sin reloj local); un *Redeliver* muy posterior a un reetiquetado registra el etiquetado más reciente, que es correcto pero ya no el de esa entrega.
  - No probado contra GitHub real (solo `FakeGitHub`).
  - Un reintento programado que encuentra la cola llena se descarta (log); la entrega sigue recuperable por sondeo o *Redeliver*.

## Spec Change Log

## Review Triage Log

Revisión 1 (Claude, revisor combinado, 11 hallazgos):

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | medium | Re-etiquetado rápido (<60 s) se pierde: resuelve al evento anterior y queda `Existing` | patch |
| 2 | medium | Límite de 1 MiB en cuerpo sin Content-Length (chunked) y frontera exacta sin test | patch |
| 3 | low-medium | Los reintentos ocupan los 4 huecos; 404 de repo invisible se reintenta entero | patch (404 de repo = final; reencolar con retraso si es sencillo) |
| 4 | low | Rate limit en la comprobación de permisos no corta los reintentos | patch |
| 5 | low | Sin `created_at` se omite la comprobación de frescura | patch |
| 6 | low | Entregas aceptadas durante el apagado se pierden sin log | reject |
| 7 | low | 503 por cola llena sin test a nivel de host | reject |
| 8 | low | Corte por rate limit sin test | patch (junto a 4) |
| 9 | low | Ruta "actor distinto del sender" sin test | patch |
| 10 | low | Dos entregas concurrentes de la misma etiqueta sin test | patch |
| 11 | low | Frontera de frescura y variantes de cabecera de firma sin test | patch |
