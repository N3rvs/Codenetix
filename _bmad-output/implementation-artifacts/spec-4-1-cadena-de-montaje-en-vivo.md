---
title: 'Historia 4.1: Cadena de montaje en vivo'
type: 'feature'
created: '2026-11-21'
status: 'done'
baseline_commit: 'b5d6580ff0471c8ab52f005b9501effe431eebea'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-4-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Aunque CodeNetix ejecuta todo el pipeline completo hasta abrir Pull Requests, el Panel web en `:5000` solo muestra el estado de salud estático sin permitir autenticarse ni observar en directo el avance de cada Ejecución y sus Revisores.

**Approach:** Añadir el hub SignalR `/hubs/runs` (autenticado vía `Authorization: Bearer` y `access_token` en query solo para `/hubs/**`), publicar cada `RunEvent` al hub solo tras el commit en SQLite (`IRunEventPublisher`), exponer el esquema OpenAPI con la unión polimórfica `RunEvent` + prueba de contrato (AD-14) y un listado `GET /api/runs`, y evolucionar `panel/` (React + React Flow + `@microsoft/signalr`) con pantalla de `admin-token`, tipos TypeScript generados desde el OpenAPI, grafo en vivo con un nodo por cada Revisor y reanudación sin pérdida vía `GET /api/runs/{id}/events?afterSeq=N`.

## Boundaries & Constraints

**Always:**
- **Autenticación de `/hubs/**` (AD-17):** `AdminTokenMiddleware` acepta `access_token` en query **únicamente** cuando la ruta empieza por `/hubs` (nunca en `/api/**`), validado en tiempo constante con el mismo `FixedTimeEquals` que `Bearer`. El token nunca aparece en logs ni respuestas de error. Actualizar `deferred-work.md` marcando como resuelta la entrada de 1.3.
- **Publicación post-commit (AD-3, AD-1):** `CodeNetix.Contracts` define `IRunEventPublisher` (`PublishAsync(RunEvent ev, CancellationToken ct)`). `CodeNetix.Pipeline` (en `RunOrchestrator` / `RunEventSink`) invoca `IRunEventPublisher` **únicamente después** de que `SaveChangesAsync` haya confirmado en SQLite; un fallo del hub nunca revierte ni rompe la Ejecución.
- **Hub SignalR `/hubs/runs`:** Expone métodos `SubscribeRun(Guid runId)` / `UnsubscribeRun(Guid runId)` (grupos `run:<runId>`) y envía el evento `RunEvent` serializado con el mismo discriminador polimórfico `type` y camelCase que `GET /api/runs/{id}/events`.
- **OpenAPI y prueba de contrato (AD-14):**
  - El Host expone `/openapi/v1.json` (mediante `Microsoft.AspNetCore.OpenApi`) incluyendo explícitamente `GET /api/runs/{id}/events` y el esquema polimórfico `RunEvent` con todos sus subtipos.
  - Los tipos TypeScript del Panel (`panel/src/api/generated.ts`) se generan desde el OpenAPI (script reproducible en `panel/package.json`) y el Panel importa los tipos de `RunEvent` y DTOs desde ese archivo generado.
  - Existe una prueba de contrato automatizada en `CodeNetix.Host.Tests` que descarga `/openapi/v1.json` y valida un ejemplo serializado de **cada** subtipo concreto de `RunEvent` contra el esquema OpenAPI (fallando si falta algún subtipo en el esquema).
- **Endpoint `GET /api/runs`:** Devuelve las Ejecuciones recientes ordenadas por `CreatedAt` descendente (`IReadOnlyList<RunSummary>`) para que el Panel pueda listar/seleccionar la Ejecución activa.
- **Panel React + React Flow (`panel/`):**
  - Al abrirse en estado `Ready` sin `admin-token` guardado en `localStorage`, solicita el `admin-token`, lo valida contra `/api/runs` (si 401 muestra error claro; si 200 lo guarda en `localStorage` y entra al Panel).
  - Muestra un grafo React Flow (`@xyflow/react`) con los nodos de la cadena de montaje: `Received` → `Analyzing` → `Coding` → `Testing` → nodos `Review` (uno por cada Revisor activo, p. ej. `security`, `quality`, `tests`) → `Deciding` → nodo terminal (`PullRequestCreated` / `Failed` / `Cancelled`), resaltando claramente el nodo activo y el estado de cada etapa.
  - Al recibir eventos vía `/hubs/runs` actualiza el grafo en tiempo real (< 1 s) sin recargar, y al reconectar llama a `GET /api/runs/{id}/events?afterSeq=N` fusionando por `seq` sin duplicados ni pérdida de eventos.
  - Compilar `panel/` con `npm run build` hacia `src/CodeNetix.Host/wwwroot/` para que el binario sirva el Panel actualizado.

**Never:**
- No aceptar `access_token` en query string en `/api/**` (solo en `/hubs/**`).
- No publicar en SignalR antes del `SaveChangesAsync` de SQLite.
- No escribir a mano en el Panel definiciones duplicadas de la unión `RunEvent` fuera del archivo generado desde OpenAPI.
- No romper `GET /api/health` anónimo ni los tests existentes de `AdminTokenMiddleware`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Autenticación `/hubs/runs` sin token | Conexión a `/hubs/runs` sin `Bearer` ni `access_token` | `401 Unauthorized` | Rechazado por `AdminTokenMiddleware` |
| Autenticación `/hubs/runs` con `?access_token=<valid>` | Conexión SignalR WebSocket/SSE a `/hubs/runs?access_token=...` | Autorizado (`200` / upgrade WebSocket); token no aparece en logs | `access_token` en `/api/runs` sigue devolviendo `401 Unauthorized` |
| Entrega en vivo por SignalR | Cliente suscrito a `SubscribeRun(runId)` mientras la Ejecución avanza | Recibe `RunEvent` (`StateChanged`, `SpecGenerated`, `VerdictRecorded`, `PullRequestOpened`, etc.) en orden y con el mismo JSON polimórfico que `/events` | Fallo de publicación no rompe el pipeline |
| Reconexión sin pérdida | Cliente pierde conexión tras `seq = 5` y reconecta cuando ya va por `seq = 12` | Llama `GET /api/runs/{id}/events?afterSeq=5`, recupera `6..12` y deduplica por `seq` con eventos en vivo | `afterSeq < 0` → `400 Bad Request` |
| Prueba de contrato OpenAPI (AD-14) | Todos los subtipos concretos de `RunEvent` (reflexión sobre `[JsonDerivedType]`) | Cada subtipo serializado valida contra el esquema de `/openapi/v1.json` | Falla el test si se añade un `RunEvent` sin reflejarlo en OpenAPI |
| Primera apertura del Panel sin token | `localStorage` vacío, instancia en `Ready` | Muestra formulario de `admin-token`; token inválido → mensaje de error; token válido → guarda en `localStorage` y carga el Panel | Botón para cambiar/cerrar sesión del token |
| Grafo React Flow con Revisores | Ejecución con eventos hasta `Reviewing` / `PullRequestCreated` | Renderiza nodos de etapas + 1 nodo por cada Revisor activo con nodo activo resaltado | `npm run build` (`tsc --noEmit && vite build`) sin errores |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/Runs.cs` -- añadir `IRunEventPublisher` y `IRunReader.ListRecentAsync(int limit, CancellationToken)`.
- `src/CodeNetix.Pipeline/Events/RunEventLog.cs`, `Features/GetRun/RunReader.cs`, `Features/RunPipeline/RunOrchestrator.cs` -- publicar eventos confirmados en `IRunEventPublisher` tras `SaveChangesAsync`, e implementar `ListRecentAsync`.
- `src/CodeNetix.Host/Access/AdminTokenMiddleware.cs` -- aceptar `access_token` en query solo en `/hubs/**` con comparación en tiempo constante.
- `src/CodeNetix.Host/Hubs/RunsHub.cs` (nuevo) y `RunsHubEventPublisher.cs` (nuevo) -- hub SignalR `/hubs/runs` con `SubscribeRun`/`UnsubscribeRun` y emisor `IRunEventPublisher`.
- `src/CodeNetix.Host/Features/ListRuns/ListRunsEndpoint.cs` (nuevo), `ServeMode.cs`, `CodeNetix.Host.csproj`, `Directory.Packages.props` -- registrar SignalR, `Microsoft.AspNetCore.OpenApi` (`/openapi/v1.json`), `GET /api/runs` y mapear `/hubs/runs`.
- `panel/` (`package.json`, `vite.config.ts`, `src/api/generated.ts`, `src/api/client.ts`, `src/graph/buildPipelineGraph.ts`, `src/App.tsx`, `src/styles.css`) -- instalar `@microsoft/signalr` y `@xyflow/react`, generador de tipos desde OpenAPI, autenticación con `admin-token`, grafo React Flow en vivo + reanudación `afterSeq`, build a `src/CodeNetix.Host/wwwroot/`.
- `tests/CodeNetix.Host.Tests/LivePanelHubTests.cs` (nuevo), `OpenApiContractTests.cs` (nuevo) -- pruebas de `/hubs/runs` con `HubConnection`, `access_token` en `/hubs` vs rechazado en `/api`, entrega en vivo + reanudación `afterSeq`, `GET /api/runs` y validación de contrato OpenAPI para todos los subtipos de `RunEvent`.
- `_bmad-output/implementation-artifacts/deferred-work.md` -- marcar como resuelta la entrada de 1.3 sobre `access_token` en `/hubs/**`.

## Tasks & Acceptance

**Execution:**
- [x] `Directory.Packages.props`, `src/CodeNetix.Contracts/Runs.cs` y `src/CodeNetix.Host/Access/AdminTokenMiddleware.cs` -- añadir `Microsoft.AspNetCore.OpenApi`, definir `IRunEventPublisher` y `IRunReader.ListRecentAsync`, y habilitar `access_token` en query solo para `/hubs/**` (cerrando el diferido de 1.3 en `deferred-work.md`) -- cumple AD-1, AD-3 y AD-17.
- [x] `src/CodeNetix.Pipeline/` y `src/CodeNetix.Host/` (`Hubs/RunsHub.cs`, `RunsHubEventPublisher.cs`, `Features/ListRuns/ListRunsEndpoint.cs`, `ServeMode.cs`) -- implementar publicación post-commit a `/hubs/runs`, `GET /api/runs` y endpoint OpenAPI `/openapi/v1.json` con unión polimórfica `RunEvent` -- cumple AD-3 y AD-14.
- [x] `panel/` (`package.json`, `src/api/generated.ts`, `src/graph/buildPipelineGraph.ts`, `src/App.tsx`, `src/styles.css`) y `src/CodeNetix.Host/wwwroot/` -- instalar `@microsoft/signalr` y `@xyflow/react`, generar tipos TS desde OpenAPI, construir el Panel autenticado con grafo React Flow en vivo (un nodo por Revisor) y reanudación por `afterSeq`, y compilar a `wwwroot/` -- cumple FR-20, AD-14 y AD-17.
- [x] `tests/CodeNetix.Host.Tests/LivePanelHubTests.cs` y `OpenApiContractTests.cs` -- cubrir las 7 filas de la matriz I/O: autenticación `/hubs` vs `/api`, streaming real en `/hubs/runs`, reanudación sin pérdida con `afterSeq`, `GET /api/runs` y validación de contrato OpenAPI para cada subtipo de `RunEvent`.

**Acceptance Criteria:**
- Given el Panel abierto por primera vez en estado `Ready`, when solicita el `admin-token` y el usuario introduce uno válido, then queda guardado en el navegador (`localStorage`) y se autentican las peticiones a `/api/**` y `/hubs/runs`.
- Given una Ejecución activa, when el Panel se suscribe a `/hubs/runs`, then muestra un grafo React Flow con las Etapas y un nodo `Review` por cada Revisor activo resaltando el nodo activo, reflejando cada cambio en < 1 s sin recargar, y al reconectar reanuda con `GET /api/runs/{id}/events?afterSeq=N` sin perder eventos.
- Given el documento OpenAPI publicado por el Host, when corre la prueba de contrato y se generan los tipos del Panel, then cada subtipo de `RunEvent` valida contra el esquema y el Panel compila limpiamente (`tsc --noEmit && vite build`) usando exclusivamente los tipos generados.

## Implementation Notes
- Publicación post-commit implementada mediante `RunEventLog.SaveAndPublishAsync` en `CodeNetix.Pipeline`, garantizando que `IRunEventPublisher.PublishAsync` se invoca solo tras `SaveChangesAsync` y nunca falla la Ejecución si SignalR falla.
- `RunsHubEventPublisher` serializa explícitamente desde `RunEvent` (`JsonSerializer.SerializeToElement<RunEvent>`) para preservar el discriminador `"type"` y camelCase en SignalR.
- `panel/scripts/generate-api.mjs` genera `panel/src/api/generated.ts` de forma determinista y sin dependencias externas desde `panel/openapi/v1.json` (exportado desde `/openapi/v1.json` del Host) o directamente desde una URL con `CODENETIX_ADMIN_TOKEN`.
- Verificación: `npm --prefix panel run generate:api`, `npm --prefix panel run build` (0 errores) y `dotnet test CodeNetix.slnx` (909 correctos, 11 omitidos por ausencia de daemon Docker en el host, 0 errores, 0 warnings).

## Spec Change Log

## Review Triage Log
- **Capa 1 (`blind-hunter`), Capa 2 (`edge-case-hunter`), Capa 3 (`verification-gap`) — Iteración 1:**
  - `[PATCH][CRITICAL]` (`verification-gap` #1): Añadir comprobación de drift en `OpenApiContractTests.cs` comparando `/openapi/v1.json` vivo contra `panel/openapi/v1.json` (`JsonNode.DeepEquals` ignorando `servers`), y añadir modo `--check` / script `check:api` en `panel/scripts/generate-api.mjs` y `panel/package.json`.
  - `[PATCH][HIGH]` (`blind-hunter` #1, `edge-case-hunter` #1, `verification-gap` #2): Redactar siempre el valor del parámetro `access_token` en query strings en `SecretRedactor.cs` (sin exponer sufijo ni depender del prefijo `cnx_`), y verificarlo en `LivePanelHubTests.cs` capturando logs reales del pipeline de logging durante una petición a `/hubs/runs/negotiate?access_token=...` con un token sin prefijo `cnx_`.
  - `[PATCH][HIGH]` (`verification-gap` #3): Añadir pruebas reales de invariante post-commit de `RunEventLog.SaveAndPublishAsync` (visibilidad en nuevo `PipelineDbContext` dentro de `PublishAsync` y `Attempts == 0` cuando `SaveChangesAsync` lanza `DbUpdateException`).
  - `[PATCH][HIGH]` (`verification-gap` #4, #6): Mejorar `LivePanelHubTests.cs` para ejercitar desconexión y reconexión real de `HubConnection` con `afterSeq` + deduplicación por `seq`, además de `UnsubscribeRun` y aislamiento de grupos `runA` vs `runB`.
  - `[PATCH][HIGH]` (`edge-case-hunter` #2, #3, `blind-hunter` #4, #5): Corregir `panel/src/graph/buildPipelineGraph.ts` para contar la ronda actual de revisión (evitando estados `completed` obsoletos de R1 durante `Coding`/R2), preservar `DEFAULT_REVIEWERS` junto a los revisores descubiertos (sin desapariciones bruscas) y eliminar `visitedStates` sin uso.
  - `[PATCH][HIGH]` (`verification-gap` #5): Añadir tests automatizados en `panel/` (Vitest) para `mergeRunEvents`, `buildPipelineGraph` y `deriveCurrentState` (incluyendo rondas múltiples, estados terminales y deduplicación `afterSeq`).
  - `[PATCH][MEDIUM]` (`blind-hunter` #2): Validar esquema `https:`/`http:` con `new URL(...)` en el enlace de PR de `panel/src/App.tsx` y usar `rel="noopener noreferrer"`.
  - `[PATCH][MEDIUM]` (`edge-case-hunter` #4, #5, #6, #8, #9, `blind-hunter` #3, #4, #5): Endurecer `panel/src/App.tsx` y `panel/src/api/client.ts`: fijar `hubStatus = 'connected'` solo tras `SubscribeRun`, sincronizar la barra lateral `runs` también en `catchUpEvents`, usar `AbortController`/comprobación de `selectedRunIdRef` en `catch`, rellenar huecos de secuencia (`lastContiguousSeq`), propagar `logoutReason` a `TokenGate` (incluyendo 401 en SignalR), envolver `localStorage.setItem` en `try/catch` y encadenar `UnsubscribeRun` antes de `stop()`.
  - `[PATCH][MEDIUM]` (`verification-gap` #7, #8, #9, `edge-case-hunter` #7, #10, #11): Completar `OpenApiContractTests.cs` (`?access_token=` rechazado en `/openapi/v1.json`, muestras con `FailureReason` no nulo, `From = null`, `StageId = null`, validación de `RunSummary` y `pattern`), añadir `limit=500` en `LivePanelHubTests.cs`, añadir fallback 404 para `/hubs/{**rest}` y `/openapi/{**rest}` en `ServeMode.cs`, y alinear manejo de `enum`/`allOf` en `panel/scripts/generate-api.mjs`.
