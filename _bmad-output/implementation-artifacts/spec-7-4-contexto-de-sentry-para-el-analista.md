---
title: 'Historia 7.4: Contexto de Sentry para el Analista'
type: 'feature'
created: '2026-11-23'
status: 'done'
baseline_commit: 'aacd7ac122c512f4a34b24ddb6b8fc3fb479c1c9'
route: 'dispatch'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Una Ejecución que nace de un error de Sentry (7.3) llega al Analista solo con el título y el cuerpo de la issue de seguimiento: sin traza, sin contexto y sin frecuencia, así que la Spec adivina la causa. Además `codenetix fix <url-de-sentry>` (y `POST /api/events/manual`) sigue respondiendo `501` "Soporte de Sentry disponible próximamente" (FR-5, Story 7.4).

**Approach:** Con `CODENETIX__Sentry__AuthToken` y `CODENETIX__Sentry__Organization` (y `CODENETIX__Sentry__BaseUrl` opcional, por defecto `https://sentry.io`, OQ-5), Ingestion —el módulo dueño de la Fuente Sentry— consulta la API de Sentry tras un puerto nuevo de Contracts (`ISentryContextProvider`). En `Analyzing` (AD-16 paso 3) el orquestador pide el contexto del Evento de la Ejecución **una vez**; si es de Sentry, Ingestion lee la incidencia (frecuencia) y su último evento (excepción, traza, migas, release, entorno, etiquetas permitidas), lo sanea (sin cabeceras, cookies, usuario, IP, query strings ni variables), lo acota, lo pasa por `ISecretRedactor` y lo devuelve como `UntrustedText`, que el Analista recibe en un bloque de datos `sentry-error` del mensaje de usuario (nunca en el de sistema, AD-5). Si Sentry no está configurado, no responde, rechaza el token o limita las peticiones, el análisis sigue sin contexto y un `RunEvent` `SentryContextUnavailable` dice por qué. La entrada manual acepta URLs de Sentry: resuelve la incidencia con la API, su proyecto → Repositorio objetivo con el mapa de la 7.3, autoriza como el resto de la entrada manual, asegura la issue de seguimiento y crea el Evento.

## Boundaries & Constraints

**Always:**
- **Configuración de instancia (AD-10), solo la lee Host:**
  - `CODENETIX__Sentry__AuthToken`: token de API de Sentry (*Internal Integration* o *Auth Token* con `event:read` y `project:read`). Sin valor → contexto de Sentry y `fix <url-de-sentry>` desactivados. Con valor: sin caracteres de control, al menos 8 caracteres; se usa recortado. Lo cubre `SecretRedactor` (clave `AuthToken`); nunca se registra, persiste ni aparece en errores ni `RunEvent`. Solo viaja en `Authorization: Bearer` hacia `BaseUrl`.
  - `CODENETIX__Sentry__Organization`: slug de la organización (`[a-z0-9][a-z0-9_-]{0,99}`, sin distinguir mayúsculas). Obligatorio si hay `AuthToken`.
  - `CODENETIX__Sentry__BaseUrl`: por defecto `https://sentry.io`; URL absoluta `http(s)` sin credenciales, query ni fragmento (Sentry autoalojado, OQ-5).
  - Inválidos → `Diagnostic` con un motivo en español que nombra la variable y nunca cita el valor (`InstanceOptionsValidator`).
  - El mapa `CODENETIX__Sentry__Projects__<slug>` de la 7.3 es la **única** fuente de la correspondencia, también en la entrada manual; se enlaza en la composición común (no depende del secreto del webhook).
- **Dueño y puertos (AD-1, AD-8):** la API de Sentry solo la llama Ingestion (`Features/ReadSentryContext/`), nunca Pipeline ni Agents; Pipeline solo ve `ISentryContextProvider` (Contracts). Ingestion sigue referenciando solo Contracts (`ArchitectureTests`).
- **Cliente de Sentry:** `GET {BaseUrl}/api/0/organizations/{org}/issues/{id}/` y `…/issues/{id}/events/latest/`; `Authorization: Bearer`, `Accept: application/json`, timeout de 10 s por petición y 20 s en total; sin seguir redirecciones (el token no sale de `BaseUrl`); respuesta de más de 4 MiB → no disponible. Errores sin el token: 401/403 → "Sentry rechaza el token (HTTP n)"; 404 → "la incidencia de Sentry <id> no existe o el token no puede verla"; 429 → "Sentry ha limitado las peticiones (rate limit)", sin reintentos; 5xx, red, timeout o JSON ilegible → "Sentry no responde".
- **Qué contexto (bloque `sentry-error`, texto plano):** incidencia (`shortId`, id, proyecto, título, culprit, nivel), frecuencia (`count`, `userCount`, `firstSeen`, `lastSeen`), último evento (id y fecha), release y entorno, etiquetas de una **lista permitida** (`environment`, `release`, `dist`, `level`, `handled`, `mechanism`, `logger`, `transaction`, `runtime`, `runtime.name`, `os`, `os.name`, `browser`, `browser.name`, `device.family`; como mucho 15), hasta 3 excepciones encadenadas (`type: value`) con hasta 30 marcos en total —primero los `in_app`, del más reciente al más antiguo— (`module`/`filename`, función, línea y, solo en marcos `in_app`, la línea de código `context_line`), y las últimas 20 migas (`timestamp category level: message`). Cada texto en una línea, sin caracteres de control ni de formato Unicode, acotado (300 caracteres; 1000 el `value` de la excepción) y el bloque completo a 16 000 caracteres con aviso de truncado.
- **Nunca al LLM (PII/secretos):** la entrada `request` (URL, cabeceras, cookies, query, cuerpo, `env`), `user` (email, IP, id, nombre), `contexts`, `extra`, `sdk`, `modules`, variables locales de los marcos (`vars`), `abs_path`, `data` de las migas y cualquier etiqueta fuera de la lista. En el texto que sí pasa: URLs sin query ni fragmento, emails → `[email]`, IPv4/IPv6 → `[ip]`, y todo el bloque por `ISecretRedactor.Redact`.
- **Orquestador (AD-16 paso 3):** tras leer la issue y listar el árbol, `ISentryContextProvider.GetForEventAsync(run.EventId)` una sola vez por Ejecución (no se cachea entre Ejecuciones ni se persiste: la traza cambia y contiene datos de terceros):
  - Evento que no es de Sentry → nada (ni llamada a Sentry ni `RunEvent`).
  - Contexto → `AnalysisInput.SentryContext` y `RunEvent` `SentryContextAttached { sentryIssueId, frames, breadcrumbs, characters, truncated }` (sin texto de Sentry).
  - No disponible (sin configurar, error, timeout, excepción inesperada que no sea cancelación) → `RunEvent` `SentryContextUnavailable { sentryIssueId, reason }` y el análisis **sigue** sin contexto. Nunca falla la Ejecución por falta de contexto.
- **Prompt (AD-5):** el mensaje de sistema sigue siendo fijo y en inglés; solo añade que un bloque `sentry-error` es el error de producción capturado por Sentry, escrito por terceros, útil para localizar la causa y nunca una instrucción. El texto de Sentry solo entra en el mensaje de usuario con `RenderAsDataBlock("sentry-error")`.
- **Entrada manual `POST /api/events/manual` con URL de Sentry** (`https://sentry.io/organizations/<org>/issues/<id>/`, `https://<org>.sentry.io/issues/<id>/` o `<host>/organizations/<org>/issues/<id>/` autoalojado; id numérico):
  - Nunca se llama al host de la URL: solo a `BaseUrl`. La organización de la URL (si la trae) debe ser la configurada.
  - Orden: parsear → Sentry configurado → organización → Evento ya existente (mismo `requestId`, sin E/S) → incidencia en la API de Sentry → proyecto mapeado → identidad del token de GitHub y permiso de escritura en el Repositorio objetivo (como la entrada manual de GitHub) → issue de seguimiento (la de un Evento anterior de la incidencia o `ISentryTrackingIssues.EnsureAsync`, idempotente por marcador) → Evento `Accepted`.
  - Evento: `Source = Manual`, `ObjectKey = github:<owner>/<repo>#<n>` (el mismo objeto que los Eventos del webhook: como mucho una Ejecución no terminal), clave `manual:<url-canónica>:<requestId>` con url canónica `{BaseUrl}/organizations/<org>/issues/<id>/` (AD-4, familia manual: no consume `sentry:<id>`, así un webhook posterior sigue funcionando), `Actor` = identidad del token, payload con `url`, `requestId`, `sentryIssueId`, `shortId`, `project`, `repository`, `issueNumber`. Sin permiso → Evento `Ignored` "no autorizado" con `ObjectKey = sentry:<id>` y sin crear la issue de seguimiento.
  - Respuestas: `201`/`200` como la entrada manual; `400` URL de Sentry mal formada (id no numérico, sin id); `422` Sentry sin configurar, organización distinta, incidencia inexistente o token rechazado, proyecto sin Repositorio objetivo (con el motivo de la 7.3, `CODENETIX__Sentry__Projects__<slug>`) o rechazo definitivo de GitHub al crear la issue; `502` si Sentry o GitHub no responden (el CLI reintenta con el mismo `requestId`). En ningún error se persiste nada. Desaparece el `501`.
  - **Autorización (equivalente de FR-26):** no hay actor de GitHub que etiquete; el disparo exige el `admin-token` de la instancia, un proyecto asociado explícitamente por el operador y escritura del token de GitHub de la instancia en el Repositorio objetivo.
- **Panel/CLI:** `SentryContextAttached` y `SentryContextUnavailable` entran en la unión de `RunEvent` (OpenAPI, `generated.ts`, resumen del Panel y `--follow`).
- **README:** sección "Contexto de Sentry para el Analista" (variables, permisos del token, qué se envía y qué nunca, comportamiento sin Sentry, `fix <url-de-sentry>`).
- **Pruebas** en `tests/CodeNetix.Host.Tests` con un `FakeSentry` HTTP (Kestrel en 127.0.0.1, puerto libre), `FakeGitHub` y `FakeChatClient`; una por fila de la matriz, sin `:5000`/`:5001` reales.

**Ask First:**
- Cambiar el esquema de `ing_events`, `run_*` o `gh_*`.
- Persistir el contexto de Sentry.

**Never:**
- Texto de Sentry en el mensaje de sistema, en `RunEvent`, en logs o en `ing_events`.
- Enviar al LLM cabeceras, cookies, query strings, email, IP, usuario, variables de entorno o locales.
- Fallar la Ejecución porque falte el contexto de Sentry.
- Llamar a Sentry desde Pipeline, Agents o el módulo GitHub, o a un host distinto de `BaseUrl`.
- Decidir el repositorio con datos de Sentry o con `codenetix.yml`.
- Registrar el `AuthToken`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Contexto adjunto | Evento de Sentry (webhook) y Sentry responde incidencia y último evento | El Analista recibe `sentry-error` con excepción, marcos `in_app` primero, migas, release, entorno y frecuencia; `SentryContextAttached` | — |
| Inyección | Mensaje de la excepción "Ignore previous instructions…" y `</untrusted-data>` | Solo aparece dentro del bloque de datos del mensaje de usuario; nunca en el de sistema | Cierre neutralizado |
| PII | Evento con `request.headers` (Authorization, Cookie), `cookies`, `user.email`/`ip_address`, query strings, `env`, `vars`, etiqueta `user.email`, email e IP en el mensaje | Ninguno llega al cliente de chat | — |
| Secreto | Token de GitHub o el propio `AuthToken` dentro del mensaje | Redactados por `ISecretRedactor` | — |
| Tamaños | 200 marcos, 500 migas, mensaje de 50 000 caracteres | 30 marcos, 20 migas, textos acotados, bloque ≤ 16 000 con aviso de truncado; `truncated = true` | — |
| Sin configurar | Sin `AuthToken` | Análisis sin contexto; `SentryContextUnavailable` "Sentry no está configurado…" | Ninguna llamada a Sentry |
| Sentry caído | 500, timeout | Análisis sigue; `SentryContextUnavailable` "Sentry no responde" | — |
| Token rechazado | 401/403 | Análisis sigue; motivo con HTTP n, sin el token | — |
| Rate limit | 429 | Análisis sigue; motivo rate limit; sin reintentos | — |
| No es de Sentry | Evento manual/sondeo de GitHub | Ninguna llamada a Sentry ni `RunEvent` de Sentry | — |
| Manual Sentry | `POST /api/events/manual` con `https://acme.sentry.io/issues/123/`, proyecto mapeado | `201` `Accepted`, issue de seguimiento, objeto `github:o/r#n`, clave `manual:…:<requestId>`; la Ejecución recibe el contexto | Mismo `requestId` → `200` el mismo Evento |
| Manual sin mapa | Proyecto `front-end` sin `Projects` | `422` con `CODENETIX__Sentry__Projects__front-end`; nada persistido | — |
| Manual sin Sentry | Sin `AuthToken` | `422` "Sentry no está configurado…"; nada persistido | — |
| Manual otra organización | `https://other.sentry.io/issues/1/` | `422`; ninguna llamada a Sentry | — |
| Manual URL mala | `https://acme.sentry.io/issues/FRONT-1/` | `400` | — |
| Manual Sentry caído | 5xx | `502`; nada persistido | El CLI reintenta |
| Manual sin permiso | Token de GitHub con `read` | `201` `Ignored` "no autorizado", objeto `sentry:<id>`, sin issue de seguimiento | — |
| Configuración inválida | `AuthToken` sin `Organization`; `BaseUrl=ftp://x`; organización `Acme Corp!` | `Diagnostic` con un motivo por variable, sin valores | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/SentryContext.cs` (nuevo) -- `ISentryContextProvider`, `SentryContextResult`.
- `src/CodeNetix.Contracts/Analysis.cs` -- `AnalysisInput.SentryContext` (opcional).
- `src/CodeNetix.Contracts/Runs.cs` -- `SentryContextAttached`, `SentryContextUnavailable`.
- `src/CodeNetix.Contracts/EventEnvelope.cs` -- `ManualEventOutcome`: `Unprocessable` y `SentryUnavailable` (sustituyen a `SentryNotSupported`).
- `src/CodeNetix.Ingestion/Features/ReadSentryContext/` (nuevo) -- opciones, cliente de la API, constructor del contexto saneado y proveedor.
- `src/CodeNetix.Ingestion/Features/SubmitManualEvent/` -- parser de URLs de Sentry y `ManualSentryEventIngestion`.
- `src/CodeNetix.Ingestion/Features/ReceiveSentryWebhook/SentryEventRecorder.cs` -- issue de seguimiento de un Evento anterior.
- `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` -- paso de contexto en `Analyzing`.
- `src/CodeNetix.Agents/Features/AnalyzeIssue/AnalystPrompt.cs` -- bloque `sentry-error`.
- `src/CodeNetix.Host/` -- opciones, validación, composición, endpoint manual (`422`/`502`), `--follow`.
- `panel/openapi/v1.json`, `panel/src/api/generated.ts`, `panel/src/App.tsx` -- eventos nuevos.
- `README.md` -- sección.
- `tests/CodeNetix.Host.Tests/FakeSentry.cs`, `SentryContextTests.cs` (nuevos) y tests ajustados (`SubmitManualEventTests`, `LaunchFromPanelTests`, `ManualUrlParserTests`, `InstanceConfigurationTests`).

## Tasks & Acceptance

**Execution:**
- [x] `CodeNetix.Contracts` -- puerto, resultado, `RunEvent` y `AnalysisInput`.
- [x] `CodeNetix.Ingestion` -- cliente de Sentry, saneado, proveedor y entrada manual.
- [x] `CodeNetix.Pipeline` y `CodeNetix.Agents` -- paso del orquestador y prompt.
- [x] `CodeNetix.Host` -- opciones, validación, composición, endpoint; README.
- [x] Panel -- OpenAPI, cliente y resumen.
- [x] Pruebas -- una por fila.

**Acceptance Criteria:**
- Given `Sentry:AuthToken` (y `Sentry:BaseUrl` opcional) configurados, when se analiza un Evento de Sentry, then el Analista recibe la traza, el contexto y la frecuencia del error como `UntrustedText` en un bloque de datos y nunca en el mensaje de sistema.
- Given Sentry no disponible, when se analiza un Evento de Sentry, then la Ejecución continúa sin contexto y un `RunEvent` registra el motivo.
- Given una URL de Sentry de un proyecto mapeado, when se lanza `codenetix fix <url-de-sentry>`, then se crea el Evento (ya no "próximamente") (FR-5).

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde (dos veces seguidas).
- `npm --prefix panel run check:api` y `npm --prefix panel test` -- expected: en verde.
- `docker build -f docker/codenetix.Dockerfile -t codenetix .` -- expected: imagen construida.

## Implementation Notes

- **Dueño de la API de Sentry: Ingestion** (`Features/ReadSentryContext/`). Sentry es una Fuente y Ingestion ya es su dueño (webhook, `ing_events` con el `sentryIssueId`); ponerlo en el módulo GitHub mezclaría dominios y un proyecto nuevo exigiría cambiar `ArchitectureTests` y la composición. Pipeline solo ve `ISentryContextProvider` (Contracts) y Agents solo `AnalysisInput.SentryContext` (`UntrustedText`). Ingestion sigue referenciando solo Contracts; el cliente usa `HttpClient` con `SocketsHttpHandler` propio (como `GitHubApiClient`), sin `IHttpClientFactory`.
- **Cliente:** `SentryApiClient` con `AllowAutoRedirect = false`, timeout por petición (10 s) con token propio y plazo total del proveedor (20 s), lectura acotada a 4 MiB, sin reintentos. 3xx/5xx/red/JSON → "Sentry no responde (HTTP n | sin respuesta en N s)"; 401/403 → "Sentry rechaza el token (HTTP n)"; 404/410 → "la incidencia … no existe o el token no puede verla"; 429 → rate limit. Ningún motivo cita el cuerpo de la respuesta (el fake devuelve el token en el cuerpo de error y se comprueba que no aparece).
- **Contexto:** `SentryIssueInfo` (incidencia) + `SentryContextBuilder` (lista cerrada de campos; formatos de la API —`entries`, `inApp`, `lineNo`, `context`— y del evento crudo —`exception.values`, `in_app`, `lineno`, `context_line`, etiquetas como pares—). Marcos: del más reciente al más antiguo, primero los `in_app`, 30 en total entre hasta 3 excepciones (la principal primero). `SentryText.Scrub`: una línea, sin Cc/Cf, URLs sin query/fragmento, `?clave=valor` sueltos → `?[query]`, emails → `[email]`, IPv4/IPv6 (completa o con `::`, sin tocar `std::vector` ni horas) → `[ip]`; después `ISecretRedactor.Redact` sobre el bloque completo y recorte a 16 000 con aviso. Un fallo al leer el último evento también es `Unavailable` (sin contexto parcial: más simple y predecible).
- **Orquestador:** `GetSentryContextAsync` tras `ls-files`; `ISentryContextProvider` es un parámetro opcional del constructor (lo registra siempre Ingestion). Excepción inesperada que no es cancelación → `SentryContextUnavailable(null, "fallo inesperado…")` y aviso en el log. Una vez por Ejecución, sin caché ni persistencia.
- **Prompt:** regla fija nueva en el sistema (inglés) sobre el bloque `sentry-error`; el bloque va tras el cuerpo de la issue en el mensaje de usuario.
- **Entrada manual:** `ManualUrlParser` extrae organización (de la ruta o del subdominio `<org>.sentry.io`) e id numérico; `ManualSentryEventIngestion` (dependencia opcional de `ManualEventIngestion`, así el test que construye `ManualEventIngestion` a mano sigue igual) con un cerrojo propio para que dos `fix` simultáneos no creen dos issues. Reutiliza la issue de seguimiento de un Evento anterior (`SentryEventRecorder.FindTrackingIssueAsync`: Eventos del webhook y manuales de Sentry) o `ISentryTrackingIssues.EnsureAsync`. `ManualEventOutcome.SentryNotSupported` desaparece; nuevos `Unprocessable` (`422`) y `SentryUnavailable` (`502`, título "Sentry no responde"). Sin Sentry configurado una URL de Sentry da `422` con "Sentry no está configurado: define CODENETIX__Sentry__AuthToken y CODENETIX__Sentry__Organization". El mensaje de URL no admitida menciona ahora también Sentry.
- **Clave del Evento manual:** `manual:<BaseUrl>/organizations/<org>/issues/<id>/:<requestId>` (familia manual de AD-4): `fix` es una petición deliberada y no debe chocar con `sentry:<id>` del webhook (ni consumirla); el objeto común `github:<owner>/<repo>#<n>` garantiza como mucho una Ejecución no terminal entre webhook y `fix`.
- **Autorización de `fix <url-de-sentry>`:** `admin-token` + proyecto mapeado por el operador + escritura del token de GitHub de la instancia en el Repositorio objetivo (igual que la entrada manual de GitHub). Sin permiso, Evento `Ignored` "no autorizado" sobre `sentry:<id>` y sin issue de seguimiento.
- **Host:** `SentryInstanceOptions.AuthToken/Organization/BaseUrl` (+ efectivos), validación (`InvalidSentryAuthToken`, `MissingSentryOrganization`, `InvalidSentryOrganization`, `InvalidSentryBaseUrl`); el mapa `Projects` y `SentryApiOptions` se enlazan en la composición común (`AddSentryOptions`); `AddSentryWebhooks` solo fija el secreto. `--follow` imprime los dos eventos nuevos. `SecretRedactor` ya cubría la clave `AuthToken`.
- **Panel:** OpenAPI regenerado (`/api/events/manual`: `422` en lugar de `501`; `RunEventSentryContextAttached`/`Unavailable`), `generated.ts`, resumen en `App.tsx` y Etapa `analyzing` en `stageOutput.ts`.
- **Tests:** `FakeSentry` (Kestrel, 127.0.0.1) y `SentryContextTests` (16 tests / 36 casos): una por fila de la matriz, con prueba de inyección (nada de Sentry en el mensaje de sistema, cierre del bloque neutralizado), de PII (cabeceras, cookies, cuerpo, `env`, usuario, email, IPv4/IPv6, query strings, `vars`, `abs_path`, `contexts`, `extra`, etiquetas fuera de la lista, `data` de migas y los tokens de GitHub y Sentry nunca llegan al cliente de chat), tamaños, fallos de Sentry (500/401/403/429/404/timeout y 502 en el último evento) y la tabla del parser. Ajustados: `SubmitManualEventTests`, `LaunchFromPanelTests`, `FixModeTests` (`501` → `422` sin Sentry configurado) y `OpenApiContractTests` (ejemplos de los nuevos `RunEvent`).
- **Verificación:** `dotnet build CodeNetix.slnx` 0 advertencias; `dotnet test --solution CodeNetix.slnx` 1229/1229 dos veces seguidas (0 omitidos: Docker disponible); `npm --prefix panel run check:api` al día, `npm --prefix panel test` 79/79, `npm --prefix panel run build` en verde; `docker build -f docker/codenetix.Dockerfile -t codenetix .` en verde.
- **Riesgos / pendientes:**
  - No probado contra Sentry real: forma de la API según su documentación (`organizations/{org}/issues/{id}/events/latest/`); versiones antiguas de Sentry autoalojado podrían exponer solo `/api/0/issues/{id}/…`.
  - El saneado es por lista cerrada de campos + patrones; un mensaje de excepción puede contener PII que no sea email/IP/URL (nombres, DNI…). Se documenta revisar el *data scrubbing* de Sentry.
  - `BaseUrl` `http://` envía el token en claro (admitido para Sentry autoalojado en red interna, como `GitHub:ApiUrl`).
  - Carrera teórica entre un `fix` y el procesador del webhook creando la issue de seguimiento a la vez (cada uno serializa lo suyo; la búsqueda por marcador lo mitiga).
  - Las Ejecuciones de un `fix` sobre la URL de GitHub de una issue de seguimiento no reciben contexto (el Evento no lleva `sentryIssueId`).

### Parches de la revisión 1

1. **Redacción antes del recorte:** `SentryText.Line`/`Scrub` aceptan el `ISecretRedactor` y lo aplican al texto crudo antes de acotarlo; `SentryContextBuilder` lo pasa en cada campo y `SentryIssueInfo.Parse` también (contexto y `fix`). Se mantiene la redacción final del bloque. Test: valor de excepción con 960 caracteres de relleno seguidos del `AuthToken` y la key del Proveedor; ningún fragmento de 16 caracteres de ninguno llega al cliente de chat.
2. **Cerrojo por incidencia compartido:** `SentryIssueLocks` (singleton de Ingestion, una entrada por `sentryIssueId`, liberada cuando nadie la usa). `ManualSentryEventIngestion` lo toma solo alrededor de "buscar o asegurar la issue + insertar" (las lecturas de Sentry y de permisos quedan fuera; sustituye al `SemaphoreSlim` global) y `SentryWebhookProcessor` también, volviendo a consultar dentro `FindTrackingIssueAsync` (incluye Eventos manuales). Test: 4 `fix` en paralelo de la misma incidencia → una sola issue de seguimiento, 4 Eventos sobre ella y ninguna entrada de cerrojo viva.
3. (a) `project.slug` ausente o inválido en `fix` → `422` "la incidencia de Sentry no trae un proyecto válido" (test). (b) `SentryText.Text`: un sustituto UTF-16 suelto (`ArgumentException`/`InvalidOperationException`) da `null`; lo usan `String`, `Scalar`, `context` y `release`. (c) `SentryText.SafePath`: ruta absoluta (`/`, `\`, `X:\`, `file://`, URL) → carpeta de usuario (`/home/<u>`, `/Users/<u>`, `X:\Users\<u>`) por `~` o, si no hay, los 3 últimos segmentos (tabla de tests). (d) Test: un proveedor que lanza `InvalidOperationException` → `SentryContextUnavailable` con id nulo y después `SpecGenerated`. (e) Test: `302` a otro servidor → no se sigue (el otro no recibe nada), motivo "Sentry no responde (HTTP 302)" y la Ejecución continúa.
- Fixture: el token de Sentry de los tests y el `eventID` del fake ya no comparten `0123456789abcdef` (falso positivo del test de fragmentos).
- Verificación: `dotnet build CodeNetix.slnx` 0 advertencias; `dotnet test --solution CodeNetix.slnx` 1241 en verde dos veces (1 omitido en una: prueba de fuego del Sandbox sin Docker).

## Spec Change Log

## Review Triage Log

Revisión 1:

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | medium | La redacción de secretos ocurre tras el recorte: un secreto partido en el límite de un campo se filtra | patch |
| 2 | medium | `fix` y el procesador del webhook usan cerrojos distintos y pueden crear dos issues de seguimiento | patch |
| 3a | low | `project.slug` inválido en `fix` daba `502` en lugar de `422` | patch |
| 3b | low | Un sustituto UTF-16 suelto hace lanzar a `GetString` | patch |
| 3c | low | El `filename` absoluto de un marco puede revelar la carpeta del usuario | patch |
| 3d | low | Sin test de un proveedor que lanza | patch |
| 3e | low | Sin test de redirección | patch |
