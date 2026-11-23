---
title: 'Historia 4.3: Historial de Ejecuciones'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: '4e3513159f435c66bc584700565fec0ee5384648'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-4-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Aunque CodeNetix persiste todas las Ejecuciones, Specs, `TestReport`, Veredictos y Pull Requests en SQLite dentro de `/data`, el Panel solo ofrece la vista en vivo del grafo y la terminal, sin una vista de Historial donde el desarrollador pueda filtrar Ejecuciones pasadas ni inspeccionar su Spec, Veredictos con hallazgos, enlace al PR, `FailureReason`, duración y consumo/coste (cuando exista).

**Approach:** Aprovechar los contratos ya existentes (`GET /api/runs` y `GET /api/runs/{id}` con `RunResponse` ya generado en `panel/src/api/generated.ts`), añadir la navegación **En vivo** / **Historial** en el Panel (`panel/`) con filtrado por estado y búsqueda por `shortId`/`objectKey`, y construir la vista de detalle histórico (`RunHistoryView` + `RunHistoryDetail` + helpers puros `runMetrics.ts`) mostrando Estado, `FailureReason`/`FailureDetail`, Spec, Veredictos con hallazgos (`archivo:línea`), `TestReport`, enlace validado al PR con clase de riesgo, duración y resumen de tokens/coste (cuando exista). Verificar con una prueba de integración real de reinicio en `CodeNetix.Host.Tests` (`RunHistoryPersistenceTests.cs`) que el historial completo sobrevive a un reinicio del Host cuando persiste `/data`.

## Boundaries & Constraints

**Always:**
- **Navegación y vista de Historial (`panel/`):**
  - Añadir pestañas/selector de vista en el Panel: **En vivo** y **Historial** (accesibles con `aria-selected`/`aria-pressed` y transición directa desde cualquier Ejecución del historial hacia su vista en vivo).
  - La vista **Historial** lista las Ejecuciones recientes (`GET /api/runs?limit=200`) con filtros por estado (`Todos`, `PR abierto`, `Fallida`, `Cancelada`, `En curso`), búsqueda por `shortId` o `objectKey`, estado vacío claro y botón de actualización.
- **Detalle completo de Ejecución (`GET /api/runs/{id}` → `RunResponse`):**
  - Añadir `fetchRunDetail(token, runId, signal?)` en `panel/src/api/client.ts` utilizando exclusivamente los tipos generados en `panel/src/api/generated.ts` (AD-14), con `AbortController` y guarda contra carreras al cambiar rápido de selección, y manejo de `401 Unauthorized` → `onLogout(reason)`.
  - Mostrar para la Ejecución seleccionada:
    - **Estado** y, si falló, **`FailureReason` y `FailureDetail`**.
    - **Duración** (`formatDuration(createdAt, updatedAt)`) y marcas temporales legibles.
    - **Coste y tokens:** mostrar el coste en USD cuando exista y el resumen de tokens consumidos (`TokensConsumed` agrupado por entrada/salida y desglose por rol/modelo); cuando todavía no exista libro de presupuesto (Épica 5), indicarlo claramente (`"No disponible"` / `"Sin coste registrado"`) sin mostrar `NaN` ni `$0.00` engañoso.
    - **Enlace al Pull Request** (si existe `pullRequest`): validado con esquema `https:`/`http:` (`rel="noopener noreferrer"`), mostrando `#número`, rama, SHA corto, clase de riesgo (`Minor` / `NeedsAttention`) y rutas protegidas coincidentes.
    - **Spec** (si existe `spec`): problema, causa probable, cambio propuesto, criterios de aceptación y archivos afectados.
    - **Veredictos** (si existen `verdicts`): agrupados/ordenados por Ronda y Revisor, mostrando decisión (`Aprobado` / `Cambios pedidos` / `Rechazado`), modelo, intentos, resumen y hallazgos con `archivo:línea`, severidad y mensaje.
    - **Resumen de pruebas** (si existe `testReport`): estado de compilación, pasados/fallados/omitidos, duración y casos fallados.
- **Persistencia tras reinicio en `/data` (FR-22):**
  - Añadir `tests/CodeNetix.Host.Tests/RunHistoryPersistenceTests.cs` que arranque una instancia del Host sobre un directorio `/data` temporal, persista Ejecuciones terminadas (una `PullRequestCreated` con Spec, `TestReport`, Veredictos, PR y `TokensConsumed`; otra `Failed(ReviewRejected)` con `FailureDetail`), detenga por completo el primer Host, arranque un **segundo** Host apuntando al mismo `/data` y verifique vía `GET /api/runs` y `GET /api/runs/{id}` que todos los datos del historial siguen intactos.
  - Verificar también en `OpenApiContractTests.cs` que un `RunResponse` completo valida contra el esquema 200 de `GET /api/runs/{id}`.
- **Contratos y build:**
  - Mantener `npm --prefix panel run check:api`, `npm --prefix panel test`, `npm --prefix panel run build` (actualizando `src/CodeNetix.Host/wwwroot/`) y `dotnet test CodeNetix.slnx` con 0 errores y 0 warnings.

**Never:**
- No duplicar en `panel/` definiciones de `RunResponse`, `Spec`, `RunVerdict`, `PullRequestInfo` o `TestReport` fuera de `panel/src/api/generated.ts`.
- No renderizar enlaces (`pullRequest.url`) sin validar `https:`/`http:`, ni usar `dangerouslySetInnerHTML`.
- No inventar ni estimar costes en dólares falsos si no existen datos de coste; mostrar `"No disponible"` cuando no exista.
- No introducir migraciones de SQLite innecesarias (las tablas `run_runs`, `run_specs`, `run_test_reports`, `run_verdicts`, `run_events` y `gh_pull_requests` ya persisten todos los datos).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Listado de historial con filtros | 10 Ejecuciones mixtas (`PullRequestCreated`, `Failed`, `Cancelled`, `Coding`) | Filtra correctamente por estado y búsqueda (`shortId`/`objectKey`, insensible a mayúsculas) preservando orden descendente | Si ningún elemento coincide, muestra mensaje vacío claro sin error |
| Detalle de Ejecución exitosa (`PullRequestCreated`) | `RunResponse` con `spec`, `testReport`, `verdicts`, `pullRequest` y `TokensConsumed` | Muestra Estado, duración, tokens/coste, enlace validado al PR con riesgo, Spec completa, Veredictos con hallazgos y resumen de tests | URL de PR con esquema inválido (`javascript:`) no se renderiza como enlace clicable |
| Detalle de Ejecución fallida (`Failed`) sin PR ni Spec | `RunResponse` con `failureReason = CloneFailed`, `failureDetail`, `spec = null`, `verdicts = []`, `pullRequest = null` | Muestra `FailureReason` y `FailureDetail` destacados; secciones vacías indican limpiamente que no se generaron | No rompe renderizado ante campos `null` o arrays vacíos |
| Cambio rápido de Ejecución en Historial | Usuario selecciona `runA` y antes de responder selecciona `runB` | Cancela la petición de `runA` (`AbortController`) y solo muestra `runB`; error/respuesta tardía de `runA` nunca sobreescribe `runB` | `401 Unauthorized` invoca `onLogout(reason)` |
| Persistencia tras reinicio con `/data` compartido | Host #1 persiste Ejecuciones completas en `/data`, se apaga y arranca Host #2 sobre el mismo `/data` | `GET /api/runs` y `GET /api/runs/{id}` en Host #2 devuelven Estado, `FailureReason`, Spec, Veredictos, `TestReport`, PR y eventos íntegros | Test de integración real en `RunHistoryPersistenceTests.cs` |
| Duración y tokens/coste en bordes | `updatedAt < createdAt` o sin `TokensConsumed` | `formatDuration` acota a `0s` (nunca negativo ni `NaN`); coste muestra `"No disponible"` y tokens `0` cuando no hay eventos | Sin `NaN` ni excepciones ante fechas inválidas |

</frozen-after-approval>

## Code Map

- `panel/src/api/client.ts` -- añadir `fetchRunDetail(token, runId, signal?)` hacia `GET /api/runs/{id}` (`RunResponse`) y exportar `isSafeHttpUrl` compartida.
- `panel/src/history/runMetrics.ts` (nuevo) -- utilidades puras deterministas: `formatDuration`, `formatDateTime`, `aggregateTokenUsage`, `filterHistoryRuns`, `formatCostDisplay`, `groupVerdictsByRound`.
- `panel/src/components/RunHistoryView.tsx` (nuevo) -- vista de Historial con filtros por estado, buscador por `shortId`/`objectKey`, lista de Ejecuciones y panel de detalle (`RunHistoryDetail`).
- `panel/src/App.tsx` y `panel/src/styles.css` -- pestañas de navegación **En vivo** / **Historial**, botón para saltar del historial a la vista en vivo de una Ejecución y estilos del historial y detalle.
- `panel/src/__tests__/history.test.ts` (nuevo) -- pruebas unitarias Vitest cubriendo `formatDuration`, `aggregateTokenUsage`, `filterHistoryRuns`, `groupVerdictsByRound`, `formatCostDisplay` y validación de URL segura.
- `tests/CodeNetix.Host.Tests/RunHistoryPersistenceTests.cs` (nuevo) -- prueba de integración de reinicio completo del Host compartiendo `/data` verificando conservación de `GET /api/runs` y `GET /api/runs/{id}`.
- `tests/CodeNetix.Host.Tests/OpenApiContractTests.cs` -- añadir validación de contrato de `RunResponse` contra el esquema 200 de `GET /api/runs/{id}`.
- `src/CodeNetix.Host/wwwroot/` -- rebuild de `panel/` (`npm run build`).

## Tasks & Acceptance

**Execution:**
- [x] `panel/src/api/client.ts` y `panel/src/history/runMetrics.ts` -- añadir `fetchRunDetail` (`GET /api/runs/{id}` → `RunResponse`) y helpers puros de duración, tokens/coste, agrupación de veredictos y filtrado de historial -- cumple FR-22 y AD-14.
- [x] `panel/src/components/RunHistoryView.tsx`, `panel/src/App.tsx` y `panel/src/styles.css` -- implementar navegación **En vivo** / **Historial**, listado filtrable y detalle completo (Estado, `FailureReason`, Spec, Veredictos con hallazgos, `TestReport`, PR validado, duración y tokens/coste) y compilar a `src/CodeNetix.Host/wwwroot/` -- cumple FR-22.
- [x] `panel/src/__tests__/history.test.ts`, `tests/CodeNetix.Host.Tests/RunHistoryPersistenceTests.cs` y `OpenApiContractTests.cs` -- cubrir las 6 filas de la matriz I/O: filtrado, detalle completo/fallido, persistencia tras reinicio real del Host sobre `/data` y validación de contrato OpenAPI de `RunResponse`.

**Acceptance Criteria:**
- Given Ejecuciones terminadas (exitosas, fallidas o canceladas), when el usuario abre el Historial en el Panel, then puede filtrarlas y ver su Estado, `FailureReason`, Spec, Veredictos con hallazgos, enlace al PR, duración y coste/tokens (cuando exista) (FR-22).
- Given un volumen `/data` persistente con Ejecuciones guardadas, when el Host se reinicia sobre el mismo `/data`, then `GET /api/runs` y `GET /api/runs/{id}` devuelven el historial completo sin pérdida de datos.

## Implementation Notes
- Añadido `fetchRunDetail` y `isSafeHttpUrl` en `panel/src/api/client.ts`, utilidades puras en `panel/src/history/runMetrics.ts` y vista `panel/src/components/RunHistoryView.tsx` integrada en `panel/src/App.tsx` con pestañas **En vivo** / **Historial**.
- Añadido `tests/CodeNetix.Host.Tests/RunHistoryPersistenceTests.cs` verificando persistencia completa tras reinicio real del Host con `/data` en disco y validación de `RunResponse` en `OpenApiContractTests.cs`.
- Verificación: `npm --prefix panel run check:api` OK, `npm --prefix panel test` (31/31 Vitest), `npm --prefix panel run build` (0 errores, `wwwroot/` actualizado), `dotnet test CodeNetix.slnx` (926 correctos, 1 omitido Docker, 0 errores, 0 warnings).

## Spec Change Log

## Review Triage Log
- **Capa 1 (`blind-hunter`), Capa 2 (`edge-case-hunter`), Capa 3 (`verification-gap`) — Iteración 1:**
  - `[PATCH][HIGH]` (`verification-gap` #1, #2, #3, #4, #5, `edge-case-hunter` #6): Ampliar `panel/src/__tests__/history.test.ts` con tests de `fetchRunDetail` (`200`, `401 → UnauthorizedError`, `ProblemDetails`, `AbortSignal`), renderizado de `RunHistoryDetail` vía `renderToStaticMarkup` (Fila 2 con URL válida y `javascript:alert(1)` sin `<a>`, Fila 3 con `Failed(CloneFailed)` y campos `null`/`[]`), `extractLatestRiskClassification`, búsqueda con `#shortId` y tokens negativos/`NaN`. Completar `OpenApiContractTests.cs` (`FailedTestCases` no vacío, `ReviewFinding.Line = null`, segundo `RunResponse` fallido, quitar `using` sin uso) y `RunHistoryPersistenceTests.cs` (verificar archivos `.db` en disco, `verdictA.Summary`, `verdictA.Attempts`, `PullRequest.CreatedAt`).
  - `[PATCH][MEDIUM]` (`blind-hunter` #1, #4, `edge-case-hunter` #5): Proteger `refreshHistoryRuns` en `App.tsx` con `AbortController` en `useRef` (abortando peticiones previas y al salir de la pestaña/desmontar) y actualizar `RunSummary` existente en `handleOpenLiveFromHistory`.
  - `[PATCH][MEDIUM]` (`blind-hunter` #2, `edge-case-hunter` #1, #3, #4): Añadir `detailReloadTick` en `RunHistoryView.tsx` para recargar también el detalle al pulsar «Actualizar», añadir botón «Reintentar» en `detailState.kind === 'error'`, evitar destello de 1 render validando `detail.runId === selectedHistoryRunId`, usar `finding.line != null` y claves `${idx}-${item}`.
  - `[PATCH][MEDIUM]` (`blind-hunter` #3, `edge-case-hunter` #2): Soportar búsqueda con prefijo `#` en `filterHistoryRuns` (`#a1b2c3d4` encuentra `shortId`, `#42` encuentra `objectKey`, `#` solo no empareja todo).
  - `[PATCH][LOW]` (`blind-hunter` #5, #6, #7): Mejorar ARIA (`role="tabpanel"`, `aria-controls`, `aria-labelledby`, `aria-current="true"` en `.run-item`) y pulir CSS (`.btn` con `text-decoration: none`, `overflow-wrap: anywhere` en bloques `code`, borde de `.history-sidebar` en móvil).
