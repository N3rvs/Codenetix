---
title: 'Historia 1.4: Lanzar trabajo con `codenetix fix`'
type: 'feature'
created: '2026-10-27'
status: 'done'
route: 'dispatch'
baseline_commit: 'bf56ad67c3410ce0f5630e289242413caab39536'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-1-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** No hay forma de pedirle trabajo a CodeNetix: no existe `codenetix fix`, ni `POST /api/events/manual`, ni dónde guardar un Evento (FR-5, AD-4).

**Approach:** Crear la primera tabla del sistema, `ing_events`, con su `DbContext` en Ingestion y las migraciones que orquesta Persistence. Un endpoint `POST /api/events/manual` normaliza la URL a un `EventEnvelope{Source=Manual}` y lo persiste con la clave `manual:<url>:<requestId>`. El modo `codenetix fix <url>` es un cliente ligero que lee la URL de la API y el token del propio contenedor.

## Boundaries & Constraints

**Always:**
- **AD-1 y AD-11, la base de datos sin romper módulos:**
  - Contracts define `IDatabaseConnection` (`string ConnectionString`) e `IModuleDatabase` (`string Prefix`, `int Order`, `Task MigrateAsync(ct)`).
  - Persistence implementa `IDatabaseConnection` sobre `SqliteDatabase` y ofrece `MigrateAllAsync`, que ejecuta los `IModuleDatabase` registrados por orden.
  - Ingestion tiene su `IngestionDbContext` (EF Core Sqlite 10.0.14) con la tabla `ing_events` y su historial de migraciones `__ef_migrations_ing`, y se registra como `IModuleDatabase`.
  - El paso 1 de `StartupReconciler` pasa a ser: inicializar la BD y ejecutar todas las migraciones.
  - Las migraciones se generan con `dotnet-ef` como herramienta local (`.config/dotnet-tools.json`) y nunca se editan a mano tras generarse.
- **Contracts:** `EventEnvelope` (`Id`, `Source`, `ObjectKey`, `DedupKey`, `Actor?`, `Payload`, `Status`, `IgnoredReason?`, `ReceivedAt`); `EventSource` (`Manual`, `GitHub`, `Sentry`, `GitHubAction`); `EventStatus` (`Accepted`, `Ignored`); `IManualEventIngestion.SubmitAsync(url, requestId, ct)`, que devuelve el resultado (creado, existente o rechazado con motivo). Ids con `Guid.CreateVersion7()` y fechas UTC.
- **`ing_events`:** `id`, `source`, `object_key`, `dedup_key` con índice UNIQUE, `actor` (nulo hasta 1.5), `payload_json`, `status`, `ignored_reason`, `received_at`. La deduplicación usa el índice: si una inserción concurrente choca, se devuelve el Evento existente.
- **URLs (decisión):**
  - **Issue de GitHub:** `https://github.com/<owner>/<repo>/issues/<n>`, con o sin `www.`, barra final, query o fragmento.
  - **Normalización:** se canoniza a `https://github.com/<owner>/<repo>/issues/<n>` con owner y repo en minúsculas. El objeto es `github:<owner>/<repo>#<n>` y la clave `manual:<url-canónica>:<requestId>`.
  - **Sentry:** se reconoce por un host `sentry.io` o `*.sentry.io`, o por una ruta `/organizations/<org>/issues/<id>`.
- **`POST /api/events/manual`** (con `admin-token`, como todo `/api`). Cuerpo `{ "url": "...", "requestId": "<guid>" }`:
  - 201 `{ "eventId": "...", "created": true }` si el Evento es nuevo; 200 con `created: false` si ya existía.
  - 400 `ProblemDetails` si falta `requestId` o no es un GUID, o si la URL no es una issue de GitHub. El mensaje dice qué URLs se admiten.
  - 501 "Soporte de Sentry disponible próximamente." si la URL es de Sentry.
  - 503 con los motivos si la instancia está en `Diagnostic` o en `Starting`.
  - En ningún error se persiste nada.
- **`codenetix fix <url>`:**
  - La API es `CODENETIX__Host__Url` si existe y, si no, `http://127.0.0.1:5000`.
  - El token es `CODENETIX__Host__AccessToken` si existe y, si no, `<DataDirectory>/admin.token`.
  - Genera un único `requestId` y reintenta hasta 3 veces con el mismo id ante errores de red o 5xx distintos de 501 y 503.
  - Con éxito imprime "Evento <id> registrado." y sale con 0. Ante un error de la API imprime su `detail` y sale con 1. Sin URL, imprime el uso y sale con 2.
  - Se añade al texto de uso.
- **Panel:** la bienvenida muestra el ejemplo como `docker run --name codenetix …` seguido de `docker exec codenetix codenetix fix <url-de-issue>`. Resuelve la incoherencia que se aplazó en 1.1.

**Never:** crear `PipelineRun` ni Dispatcher (1.6); comprobar permisos o rellenar `actor` (1.5); llamar a la API de GitHub; aceptar URLs de PR, de commit o de otros hosts; persistir el token o secretos en `payload_json`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Issue válida | `fix https://github.com/O/R/issues/12` | 201, 1 fila `Accepted`, objeto `github:o/r#12`; el CLI imprime el id en menos de 2 s | — |
| Variantes de URL | `www.`, `/`, `?x=1`, `#c` | Misma URL canónica | — |
| Reintento | Mismo `requestId` dos veces | 2.ª respuesta 200 `created:false` con el mismo id; 1 fila | — |
| `requestId` distinto | Misma URL y otro id | 2 filas (dedup solo por `requestId`) | — |
| No admitida | `https://github.com/o/r/pull/3`, `https://gitlab.com/...`, `foo` | 400 con explicación; 0 filas; el CLI sale con 1 | — |
| Sentry | `https://acme.sentry.io/issues/123/` | 501 "Soporte de Sentry disponible próximamente."; 0 filas | — |
| Diagnóstico | Instancia en `Diagnostic` | 503 con motivos; 0 filas | El CLI muestra los motivos |
| Sin token | POST sin `Authorization` | 401 (1.3) | — |
| Concurrencia | 2 POST simultáneos con el mismo `requestId` | 1 fila; ambos devuelven el mismo id | Captura de la violación UNIQUE |
| CLI sin URL | `codenetix fix` | Uso y código 2 | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Persistence/SqliteDatabase.cs` -- conexión y PRAGMA ya existentes; reutilizar su `ConnectionString` en la implementación de `IDatabaseConnection`.
- `src/CodeNetix.Host/StartupReconciler.cs` -- paso 1 (BD) y paso 1b (token de 1.3); las migraciones van justo tras `InitializeAsync`.
- `src/CodeNetix.Host/ServeMode.cs` -- composición: `AddCodeNetixIngestion()` y el mapeo del endpoint (slice `Features/SubmitManualEvent/`).
- `src/CodeNetix.Host/CodeNetixCommandLine.cs` -- despacho de modos; añadir `fix`.
- `src/CodeNetix.Host/Access/AdminTokenStore.cs` -- la lectura del token de archivo y de `AccessToken` ya existe: reutilizarla para el cliente (solo lectura, sin crear el archivo).
- `src/CodeNetix.Host/Configuration/InstanceHealth.cs` -- estado para la respuesta 503.
- `tests/CodeNetix.ArchitectureTests/` -- debe seguir en verde: Ingestion solo referencia Contracts, aunque añada paquetes EF.
- `tests/CodeNetix.Host.Tests/CodeNetixFactory.cs` -- cliente autorizado y configuración de instancia completa (`Ready`) disponibles.
- `panel/src/App.tsx` -- texto de bienvenida.

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Contracts/` -- `IDatabaseConnection`, `IModuleDatabase`, `EventEnvelope`, `EventSource`, `EventStatus` e `IManualEventIngestion` (con su tipo de resultado).
- [x] `src/CodeNetix.Persistence/` -- implementación de `IDatabaseConnection` y `MigrateAllAsync`.
- [x] `.config/dotnet-tools.json` y `Directory.Packages.props` -- `dotnet-ef`, EF Core Sqlite y Design 10.0.14.
- [x] `src/CodeNetix.Ingestion/` -- `IngestionDbContext`, la entidad, la migración inicial, `ManualUrlParser` (GitHub, Sentry o rechazo), `ManualEventIngestion` y `AddCodeNetixIngestion`.
- [x] `src/CodeNetix.Host/` -- endpoint `Features/SubmitManualEvent/`, el paso de migraciones en `StartupReconciler` y `FixMode` (cliente) en el despacho.
- [x] `panel/src/App.tsx` -- el comando con `--name codenetix`.
- [x] `tests/` -- tests de tabla de `ManualUrlParser`; un test por fila de la matriz contra el host; un test de concurrencia; tests de `FixMode` contra un servidor local (éxito, 400, 501, 503, reintento con el mismo `requestId`).

**Acceptance Criteria:**
- Given la imagen arrancada con `--name codenetix` y configuración completa, when `docker exec codenetix codenetix fix https://github.com/o/r/issues/12`, then imprime "Evento <id> registrado." en menos de 2 s y existe una fila en `ing_events`.
- Given `codenetix.db` creada por 1.3, when arranca 1.4, then la migración `ing` se aplica sin perder datos y se registra en `__ef_migrations_ing`.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde.
- `docker build ...` y después `docker run --name codenetix ... -e CODENETIX__GitHub__Token=... -e CODENETIX__Providers__Local__ApiKey=...`, `time docker exec codenetix codenetix fix https://github.com/o/r/issues/12` y una URL de Sentry -- expected: el id en menos de 2 s y el mensaje de Sentry con código 1. Al terminar se borran el contenedor y el volumen.

## Implementation Notes

- Migración generada con `dotnet dotnet-ef migrations add InitialIngestion --project src/CodeNetix.Ingestion --startup-project src/CodeNetix.Ingestion --context IngestionDbContext --output-dir Data/Migrations` (fábrica de diseño `IngestionDesignTimeDbContextFactory`). El SDK 10 crea el manifiesto en la raíz; se movió a `.config/dotnet-tools.json`.
- `ManualEventResult` distingue `Created`, `Existing`, `Rejected` y `SentryNotSupported` (este último para responder 501 sin que Host vuelva a analizar la URL).
- El endpoint comprueba primero la salud (503), después el cuerpo (400) y por último la URL (400/501). El 503 lleva `instanceStatus` y `reasons` como extensiones; `detail` incluye los motivos en líneas `- …` para que el CLI los muestre.
- "Reintenta hasta 3 veces" se interpreta como 3 reintentos (hasta 4 peticiones), con esperas de 250 ms, 500 ms y 1 s y el mismo `requestId`.
- El cliente lee la configuración como `serve` (entorno + YAML de instancia; gana el entorno); `AdminTokenStore.ReadEffectiveTokenReadOnly` no crea el archivo ni corrige sus permisos. Un 503 con `instanceStatus: Starting` se reintenta con el mismo presupuesto; `Diagnostic` es definitivo.
- `received_at` se guarda como ticks UTC (INTEGER, conversor de valor) para que EF pueda ordenar y comparar; la migración inicial se regeneró.
- `repo.git` se canoniza como `repo`. La comprobación de prefijos duplicados vive en `MigrateAllAsync` (lanza `DatabaseMigrationException`, que registra `StartupReconciler`).
- Se filtran los logs de `Microsoft.EntityFrameworkCore` a `Warning` (registraba cada SQL en `Information`).
- Verificado en Docker: `fix` de una issue en 0,5 s con código 0; Sentry con el mensaje y código 1; sin URL, uso y código 2. Contenedor, volumen e imagen eliminados.

## Spec Change Log

## Review Triage Log

| # | Hallazgo | Veredicto | Evidencia | Ruta |
|---|----------|-----------|-----------|------|
| 1 | `fix` solo lee el entorno: ignora `AccessToken` y `DataDirectory` del YAML de instancia (edge, verif.) | medium | `FixMode` construye la configuración solo con `AddEnvironmentVariables`, mientras que `serve` carga el YAML; resultado: 401 o "no se encuentra el admin-token". | patch |
| 2 | `fix` no reintenta un 503 en `Starting` (blind) | medium | `IsRetryable` trata todo 503 como final; un `fix` lanzado justo tras `docker run` falla sin necesidad. | patch |
| 3 | `ReceivedAt` (`DateTimeOffset`) en TEXT: EF Sqlite no traduce `ORDER BY` ni comparaciones (blind) | medium | Limitación conocida del proveedor; las historias 4.3 y 1.6 ordenarán por fecha. Se arregla ahora, con una sola migración. | patch |
| 4 | El fallo de migración no se prueba y la comprobación de prefijo duplicado está fuera del `catch` (blind, edge, verif.) | medium | Si se quitara el `throw`, la instancia publicaría `Ready` sin `ing_events`. | patch |
| 5 | Sin test de `AccessToken` frente al archivo en el cliente, ni de que el modo del archivo no cambie (verif., blind) | medium | Si se invierte la precedencia, todos los tests siguen en verde y el `fix` real recibe 401. | patch |
| 6 | El reintento por error de red no se cuenta en el test (verif.) | medium | Quitar la rama de reintento no rompe ningún test. | patch |
| 7 | La recuperación de la violación UNIQUE solo se cubre de forma probabilística (verif.) | medium | El pre-check puede servir todas las peticiones; el `catch` podría estar roto sin que falle nada. | patch |
| 8 | 503 en `Starting` del endpoint manual sin test (verif.) | medium | Solo se prueba `Diagnostic`. | patch |
| 9 | `https://github.com/o/r.git/issues/1` da un objeto distinto de `r` (blind) | low | Se arregla quitando `.git` del repo antes de canonizar. | patch |
| 10 | Archivo vacío `spec-1-4-….md` en la raíz del repo (blind) | low | Resto accidental. | patch (borrado directo) |
| 11 | El mismo `requestId` con otra URL crea otro Evento (blind) | false | La clave de AD-4 incluye la URL a propósito. | rechazado |
| 12 | Hasta unos 42 s colgado en el peor caso (blind) | low | Solo pasa con un servidor que no responde; los reintentos están acotados. | rechazado |
| 13 | Las conexiones de EF no aplican `PRAGMA busy_timeout` (blind) | low | Microsoft.Data.Sqlite reintenta los `SQLITE_BUSY` durante `DefaultTimeout`, que ya está fijado. | rechazado |
| 14 | El comando del Panel lleva `…` y no se puede pegar (blind) | low | El texto lo fija la intención congelada. | rechazado |
| 15 | Sentry autoalojado por la ruta `/organizations/` en cualquier host (blind) | low | Decisión de la spec; un falso positivo solo devuelve un 501 en vez de un 400. | rechazado |
| 16 | No hay proyecto `Ingestion.Tests` (blind) | low | Organización de los tests, sin daño concreto. | rechazado |
| 17 | Higiene: salto de línea final, BOM, comentario del `DbContext` (blind) | low | Cosmético. | rechazado |
| 18 | `baseline_commit` fuera del historial (blind) | false | Es una instantánea interna a propósito (sin commits por decisión del usuario). | rechazado |
| 19 | Token no ASCII, `Host__Url` con query (edge) | low | Poco probable y falla de forma ruidosa. | rechazado |
| 20 | Bloqueo `__EFMigrationsLock` huérfano tras morir el proceso a mitad de migración (edge) | low | Requiere matar el contenedor justo durante la primera migración. | rechazado |
