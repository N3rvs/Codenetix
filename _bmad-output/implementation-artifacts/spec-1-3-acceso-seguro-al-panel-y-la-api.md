---
title: 'Historia 1.3: Acceso seguro al Panel y la API'
type: 'feature'
created: '2026-10-27'
status: 'done'
route: 'dispatch'
baseline_commit: '0a5af7c011ba686662108ec3b7ada201d08b4bb8'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-1-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Cualquiera que llegue a `:5000` puede usar la API, y por tanto lanzar Ejecuciones con las keys del operador. Además, nada impide exponer la instancia a la red sin un token explícito (FR-4, AD-17).

**Approach:** Un único middleware de autenticación exige `Authorization: Bearer <admin-token>` en `/api/**` y `/hubs/**`. El token se genera en el primer arranque en `/data/admin.token` (0600) o se toma de `CODENETIX__Host__AccessToken`. Exponer la instancia sin `AccessToken` aborta el arranque. Un segundo puerto, `:5001`, sirve solo `/webhooks/*`, sin token.

## Boundaries & Constraints

**Always:**
- **Token de la instancia** (decisión):
  - Si `CODENETIX__Host__AccessToken` tiene valor, ese es el token.
  - Si no, se usa el de `/data/admin.token`. Si el archivo no existe se genera: `cnx_` + 32 bytes aleatorios criptográficos en base64url, escritos con permisos 0600 en Linux y macOS; en Windows, el modo por defecto. Si el archivo ya existe se reutiliza y no se reescribe.
  - El archivo se genera siempre en el arranque (paso de `StartupReconciler` justo después de la base de datos), aunque haya `AccessToken`.
  - El log dice dónde está el token (`/data/admin.token`), nunca su valor.
- **`CODENETIX__Host__Exposed=true`:** exige un `AccessToken` no vacío de al menos 16 caracteres. Si no, el arranque aborta antes de construir el host: código de salida 1 y un mensaje que nombra ambas variables.
- **Middleware de autenticación (AD-17):**
  - Rutas `/api` y `/hubs`, sin distinguir mayúsculas. Única excepción: `GET /api/health`, anónimo por la decisión de 1.1.
  - Esquema `Bearer` sin distinguir mayúsculas. Comparación en tiempo constante (`CryptographicOperations.FixedTimeEquals`).
  - Si falla, 401 `ProblemDetails` con `WWW-Authenticate: Bearer`.
  - Se aplica antes del enrutado, de modo que un `/api` desconocido sin token da 401 y no 404.
- **Puertos:**
  - Kestrel escucha en `:5000` y `:5001`. La decisión se toma por `HttpContext.Connection.LocalPort`, nunca por la cabecera `Host`.
  - En `:5001` solo existe `/webhooks/{**rest}`: sin token; `Diagnostic` o `Starting` → 503 (AD-18); `Ready` → 404 `ProblemDetails` "Fuente de webhook no disponible todavía" (las firmas llegan en la Épica 7). Cualquier otra ruta en `:5001` → 404.
  - En `:5000`, `/webhooks/*` → 404.
  - `LocalPort` = 0 (TestServer) cuenta como `:5000`.
- **Redacción:** `SecretRedactor` añade el patrón `cnx_` seguido de 20 o más caracteres de `[A-Za-z0-9_-]`. `AccessToken` ya está en sus nombres de clave.
- **Imagen:** `EXPOSE 5000 5001`. El `HEALTHCHECK` no cambia, porque `/api/health` sigue siendo anónimo.

**Never:** sesiones, cookies, login ni usuarios; filtrar por IP de origen (dentro de Docker no sirve); validar firmas de webhook (Épica 7); el formulario de token del Panel (llega con la primera vista que llame a una API protegida); que el token aparezca en logs, respuestas o SQLite; leer el entorno fuera de Host.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Primer arranque | `/data` sin `admin.token` | Se crea `admin.token` (0600, prefijo `cnx_`) | — |
| Rearranque | `admin.token` existente | Mismo token; el archivo no se reescribe | — |
| API sin token | `GET /api/nope` sin cabecera | 401 con `WWW-Authenticate: Bearer` | Nunca 404 ni contenido |
| Token incorrecto | `Bearer otro` | 401 | — |
| Token correcto | `Bearer <token>` (o `bearer`) | Pasa al endpoint (`/api/nope` → 404) | — |
| Hub sin token | `GET /hubs/runs` | 401 | — |
| Health anónimo | `GET /api/health` sin token | 200/503 como en 1.2 | — |
| `AccessToken` configurado | `Host__AccessToken=X` (≥ 16) | `Bearer X` pasa; el token del archivo da 401 | — |
| Expuesto sin token | `Host__Exposed=true` sin `AccessToken` o con uno de menos de 16 | Sale con código 1 y un mensaje con `CODENETIX__Host__Exposed` y `CODENETIX__Host__AccessToken` | No arranca Kestrel |
| Webhook en :5001 | `POST :5001/webhooks/github` sin token, `Ready` | 404 "no disponible todavía", nunca 401 | `Diagnostic` → 503 |
| Panel o API en :5001 | `GET :5001/` o `:5001/api/health` | 404 | — |
| Webhook en :5000 | `POST :5000/webhooks/github` | 404 | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Host/ServeMode.cs` -- composición: `UseUrls` pasa a `:5000` y `:5001`; validar `Exposed` antes de `Build()`; registrar el almacén del token; `UseMiddleware` de auth después de `ApiRedactionMiddleware` y antes de `UseDefaultFiles`; mapear los webhooks.
- `src/CodeNetix.Host/CodeNetixCommandLine.cs` -- `ServeAsync` ya convierte cualquier excepción previa al host en `ExitFailure` con un mensaje redactado: aquí entra el aborto por `Exposed`.
- `src/CodeNetix.Host/StartupReconciler.cs` -- paso 1b: asegurar `admin.token` tras la base de datos (mismo `DataDirectory` de `PersistenceOptions`).
- `src/CodeNetix.Host/Configuration/InstanceOptions.cs` -- añadir `Host { Exposed, AccessToken }`.
- `src/CodeNetix.Host/Secrets/SecretRedactor.cs` -- `KnownKeyPattern`: añadir `cnx_`.
- `src/CodeNetix.Host/Features/GetHealth/GetHealthEndpoint.cs` -- `Route` es la excepción anónima.
- `tests/CodeNetix.Host.Tests/CodeNetixFactory.cs` -- hoy no pasa token. Los tests existentes que llaman a `/api/*` (404, redacción del `instance`) recibirán 401: la fábrica debe fijar un `AccessToken` conocido y ofrecer un cliente autorizado.
- `docker/codenetix.Dockerfile` -- `EXPOSE`.

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Host/Access/` -- `AdminTokenStore` (carga o crea el archivo con 0600 y expone el token efectivo), `AccessOptionsValidator` (regla de `Exposed`) y `AdminTokenMiddleware`.
- [x] `src/CodeNetix.Host/Features/ReceiveWebhook/` -- el endpoint provisional de `:5001` con la guarda de puerto.
- [x] `src/CodeNetix.Host/` -- `ServeMode`, `StartupReconciler`, `InstanceOptions` y el patrón `cnx_` del redactor.
- [x] `docker/codenetix.Dockerfile` -- `EXPOSE 5000 5001`.
- [x] `tests/CodeNetix.Host.Tests/` -- un test por fila de la matriz (para el puerto: `TestServer.SendAsync` con `Connection.LocalPort = 5001`); permisos 0600 comprobados solo fuera de Windows; actualizar la fábrica y los tests existentes para que usen el cliente autorizado.

**Acceptance Criteria:**
- Given la imagen recién arrancada, when `docker exec <c> stat -c '%a %U' /data/admin.token`, then devuelve `600 app`.
- Given la imagen arrancada con `-p 127.0.0.1:5000:5000 -p 127.0.0.1:5001:5001`, when `curl :5000/api/nope` sin token, then 401; con `Bearer $(docker exec <c> cat /data/admin.token)`, then 404; y `curl -X POST :5001/webhooks/github`, then no es 401.
- Given `-e CODENETIX__Host__Exposed=true` sin `AccessToken`, when arranca el contenedor, then termina con código 1 y el log explica qué variable falta.
- Given cualquiera de los arranques anteriores, when se leen `docker logs`, then el valor del token no aparece.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde.
- `docker build -f docker/codenetix.Dockerfile -t codenetix .` y los `docker run` y `curl` de los criterios de aceptación (con `MSYS_NO_PATHCONV=1` en Git Bash) -- expected: lo indicado en cada criterio. Al terminar se borran los contenedores y volúmenes de prueba.

## Implementation Notes

- Orden del pipeline: `WebhookPortGuardMiddleware` (en `Features/ReceiveWebhook/`) va **antes** de `ApiRedactionMiddleware` y de la autenticación, para que en `:5001` nunca haya 401 (`:5001/api/health` → 404). `AdminTokenMiddleware` va después de la redacción y antes de `UseDefaultFiles`, como pide el Code Map.
- Comparación: `FixedTimeEquals` sobre los SHA-256 del token presentado y del esperado, para no filtrar la longitud. Solo se acepta una cabecera `Authorization`. Si el token efectivo aún no está cargado, se deniega (fail-closed).
- `admin.token` se escribe en un temporal creado con `UnixCreateMode` 0600 y se mueve sin sobrescribir, así que nunca existe a medias ni con permisos más abiertos. Un `admin.token` existente pero vacío aborta el arranque con un mensaje que nombra la ruta.
- `AccessToken` y la longitud mínima con `Exposed` se comparan tras recortar espacios. Un `Exposed` no booleano también aborta con código 1.
- `InstanceConfigurationTests.Secrets_are_never_written_to_the_database_files` era intermitente con la nueva clase de tests: `SqliteConnection.ClearAllPools()` es global y borra `-wal`/`-shm` entre `File.Exists` y la apertura. El test ahora tolera que desaparezcan esos dos archivos auxiliares.
- Verificado en Docker: `600 app`; 401/404 en `:5000/api/nope`; `/hubs` 401; `:5001/webhooks/github` 503 (Diagnostic) y 404 "no disponible todavía" (Ready); `:5001/` y `:5001/api/health` 404; `:5000/webhooks/github` 404; `Exposed` sin token o con 15 caracteres → código 1; el token del archivo y el `AccessToken` no aparecen en `docker logs`; mismo token tras `docker restart`; `HEALTHCHECK` sigue `healthy`.

## Spec Change Log

## Review Triage Log

| # | Hallazgo | Veredicto | Evidencia | Ruta |
|---|----------|-----------|-----------|------|
| 1 | El 404 de `WebhookPortGuardMiddleware` devuelve `instance` = ruta sin redactar en `:5001` y en `:5000/webhooks` (blind, edge, verif.) | high | Se registra antes de `ApiRedactionMiddleware`; `GET :5001/api/sk-ant-…` devuelve el secreto en claro por el puerto público. | patch |
| 2 | `GET /api/health/` (con barra final) y `HEAD /api/health` dan 401 (blind, edge) | low | Comparación exacta y solo `GET`; las sondas externas suelen usar `HEAD`. El arreglo es directo. | patch |
| 3 | Un `admin.token` existente con 0644 se acepta sin corregirlo (blind, edge) | medium | `TryRead` no mira el modo del archivo; la spec exige 0600. El arreglo es reaplicar 0600 fuera de Windows. | patch |
| 4 | Sin test: `admin.token` vacío, `Exposed` no booleano, webhook en `Starting` → 503 (blind, verif.) | medium | Son reglas de la spec o de sus notas que pueden romperse sin que falle ningún test. | patch |
| 5 | La comprobación de 0600 no se ejecuta nunca: el desarrollo es en Windows y el Dockerfile solo corre ArchitectureTests (verif.) | medium | Quitar `UnixCreateMode` no rompería nada. | patch |
| 6 | El navegador no puede autenticarse en `/hubs` con SignalR, porque WebSocket y SSE no admiten la cabecera (blind, edge) | medium | Hoy no existe ningún hub; se necesitará `access_token` en la query cuando llegue el primero. | defer |
| 7 | `Exposed` es declarativo y un `AccessToken` corto se admite sin exponer (blind) | low | Lo decidió la spec; el token del archivo es fuerte y el filtro por IP no sirve dentro de Docker. | rechazado |
| 8 | Las fuentes de entorno y YAML de `Exposed` no se prueban, y los mensajes nombran siempre la variable de entorno (blind) | low | El enlace es el mismo para las tres fuentes; el mensaje sigue siendo útil. | rechazado |
| 9 | Archivos estáticos alcanzables en `:5001` (blind) | low | La guarda corta todo lo que no sea `/webhooks` y `wwwroot` no tiene esa carpeta. | rechazado |
| 10 | `ListenPorts` está en el archivo del feature y el hash se recalcula en cada petición (blind) | low | Es estética y un coste insignificante. | rechazado |
| 11 | `AccessToken` no ASCII (edge) | low | Poco probable y falla de forma ruidosa (401). | rechazado |
| 12 | Puertos sobrescritos con `ASPNETCORE_URLS`/`--urls` (edge) | low | `UseUrls` es fijo en el código y la imagen no expone otro mecanismo. | rechazado |
| 13 | `sprint-status` en `in-progress` frente a la spec `in-review` (blind) | false | El paso 5 lo sincroniza. | rechazado |
