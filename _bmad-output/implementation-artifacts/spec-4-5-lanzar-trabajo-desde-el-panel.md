---
title: 'Historia 4.5: Lanzar trabajo desde el Panel'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: 'c9c5aa14e4dca4a3d09e5df9ad8cf5c3d7a4c760'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-4-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Para lanzar una nueva Ejecución sobre una issue de GitHub el desarrollador todavía tiene que abrir una terminal y ejecutar `docker exec codenetix codenetix fix <url>`, sin poder pegar la URL directamente desde el Panel web autenticado.

**Approach:** Publicar en OpenAPI los contratos de `POST /api/events/manual` (`SubmitManualEventRequest`, `SubmitManualEventResponse`) y `GET /api/events/{eventId}` (`EventResponse`), regenerar `panel/src/api/generated.ts` (AD-14), añadir un formulario/diálogo **«Lanzar trabajo»** accesible desde la cabecera del Panel autenticado que envíe `POST /api/events/manual` con un `requestId` GUID nuevo (AD-4), muestre exactamente el mismo error devuelto por el servidor que el CLI ante URLs inválidas/no admitidas, espere al `runId` mediante `GET /api/events/{eventId}` y navegue automáticamente a la nueva Ejecución en la vista **En vivo**.

## Boundaries & Constraints

**Always:**
- **Contratos OpenAPI y tipos generados (AD-14):**
  - Añadir metadatos `.Accepts<SubmitManualEventRequest>("application/json")` y `.Produces<...>()` / `.ProducesProblem(...)` en `SubmitManualEventEndpoint.cs` y `GetEventEndpoint.cs` sin alterar su comportamiento HTTP (`201`/`200`/`400`/`501`/`502`/`503` y `200`/`404`).
  - Actualizar `panel/openapi/v1.json` desde `/openapi/v1.json` real, regenerar `panel/src/api/generated.ts` y validar `SubmitManualEventResponse` y `EventResponse` en `OpenApiContractTests.cs`.
- **Formulario «Lanzar trabajo» en el Panel (`panel/`):**
  - Botón **«Lanzar trabajo»** visible en la cabecera del Panel autenticado que abre un diálogo accesible (`role="dialog"`, `aria-modal="true"`, cierre con `Escape` cuando no está enviando).
  - Genera un `requestId` GUID nuevo (`generateRequestId()`, usando `crypto.randomUUID()` con respaldo RFC 4122 v4 sobre `crypto.getRandomValues`) para cada lanzamiento nuevo (AD-4).
  - Envía la URL recortada directamente a `POST /api/events/manual` con `Authorization: Bearer <admin-token>` — **sin sustituir la validación del servidor por un error distinto en cliente**, de modo que cualquier URL inválida o no admitida (p. ej. URL mal formada, PR en lugar de issue, Sentry `501`, error GitHub `502`, modo diagnóstico `503`) muestre exactamente el mismo mensaje (`ProblemDetails.detail`) que `codenetix fix`.
  - Si el Evento responde `Accepted`, sondea `GET /api/events/{eventId}` (con `AbortController` y timeout acotado) hasta recibir `runId` (o `status === 'Ignored'`). Al obtener `runId`, recarga la lista de Ejecuciones, cambia a la pestaña **En vivo**, selecciona la nueva Ejecución y cierra el diálogo.
  - Si el Evento responde `Ignored` (p. ej. actor no autorizado u objeto ya ocupado), muestra claramente el motivo (`reason`) devuelto por el servidor.
  - Si la API responde `401 Unauthorized`, invoca `onLogout(reason)`.
  - Compilar `panel/` hacia `src/CodeNetix.Host/wwwroot/`.
- **Pruebas:**
  - `tests/CodeNetix.Host.Tests/LaunchFromPanelTests.cs`: flujo completo `POST /api/events/manual` (GUID nuevo) → `GET /api/events/{eventId}` con `runId` → `GET /api/runs/{runId}`; verificación explícita de que una URL inválida devuelve en `POST /api/events/manual` el **mismo mensaje de error** que imprime `FixMode.RunAsync`; Sentry `501`; sin token `401`.
  - `panel/src/__tests__/launch.test.ts`: pruebas Vitest de `generateRequestId`, `submitManualEvent` (`201`, `200`, `400` exacto, `501`, `401 → UnauthorizedError`), `waitForEventRun` (polling hasta `runId`, parada en `Ignored`, cancelación `AbortSignal`, timeout) y renderizado de `LaunchWorkModal`.

**Never:**
- No crear Ejecuciones directamente desde el endpoint `POST /api/events/manual` (sigue siendo responsabilidad exclusiva del `Dispatcher`, AD-4).
- No inventar mensajes de validación de URL en cliente que diverjan de los errores del servidor / CLI.
- No reutilizar un mismo `requestId` entre dos envíos distintos de URLs diferentes.
- No desincronizar `panel/openapi/v1.json` ni `panel/src/api/generated.ts`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| URL de issue válida desde el Panel | Usuario pega `https://github.com/owner/repo/issues/42` y confirma | Envía `POST /api/events/manual` con `requestId` GUID nuevo → `201 Accepted` → espera `runId` vía `GET /api/events/{eventId}` → navega a **En vivo** con la nueva Ejecución seleccionada | Si `runId` tarda más del timeout, cierra/informa claramente y refresca la lista sin perder el Evento |
| URL inválida o no admitida | `no-es-una-url` o URL de PR `/pull/1` o Sentry | Servidor responde `400`/`501` con `ProblemDetails.detail`; el Panel muestra exactamente el mismo texto de error que `codenetix fix` | Permanece en el diálogo para corregir la URL sin perder el texto introducido |
| Evento `Ignored` (sin permiso u objeto ocupado) | `POST /api/events/manual` o `GET /api/events/{eventId}` devuelve `status: "Ignored"` | Muestra aviso claro con `reason` devuelto por el servidor (p. ej. `no autorizado` o `objeto ocupado`) | No intenta navegar a un `runId` inexistente |
| Token inválido o expirado (`401`) | `POST /api/events/manual` o `GET /api/events/{eventId}` devuelve `401` | Invoca `onLogout(reason)` y vuelve a `TokenGate` | Limpia abort controllers pendientes |
| Cierre o cambio de diálogo durante sondeo | Usuario cierra el diálogo o desmonta mientras espera `runId` | `AbortController` cancela el sondeo inmediatamente sin actualizar estado tras cerrar | Sin errores de estado en componente desmontado |
| Contrato OpenAPI actualizado | `/openapi/v1.json` vs `panel/openapi/v1.json` vs `panel/src/api/generated.ts` | `SubmitManualEventResponse` y `EventResponse` validan contra OpenAPI; `check:api` sin drift | `OpenApiContractTests` falla si se modifica el contrato sin regenerar |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Host/Features/SubmitManualEvent/SubmitManualEventEndpoint.cs` y `src/CodeNetix.Host/Features/GetEvent/GetEventEndpoint.cs` -- añadir metadatos OpenAPI (`.Accepts`, `.Produces`, `.ProducesProblem`).
- `panel/openapi/v1.json` y `panel/src/api/generated.ts` -- actualizar snapshot OpenAPI y regenerar tipos TypeScript (`SubmitManualEventRequest`, `SubmitManualEventResponse`, `EventResponse`).
- `panel/src/api/client.ts` -- añadir `generateRequestId`, `submitManualEvent`, `fetchEvent` y `waitForEventRun`.
- `panel/src/components/LaunchWorkModal.tsx` (nuevo), `panel/src/App.tsx`, `panel/src/styles.css` -- botón **«Lanzar trabajo»** en la cabecera, diálogo de URL con `requestId` nuevo, errores idénticos al CLI, espera de `runId` y navegación a la nueva Ejecución en **En vivo**, más rebuild a `src/CodeNetix.Host/wwwroot/`.
- `tests/CodeNetix.Host.Tests/LaunchFromPanelTests.cs` (nuevo), `OpenApiContractTests.cs` y `panel/src/__tests__/launch.test.ts` (nuevo) -- cobertura completa de las 6 filas de la matriz I/O.

## Tasks & Acceptance

**Execution:**
- [x] `SubmitManualEventEndpoint.cs`, `GetEventEndpoint.cs`, `panel/openapi/v1.json`, `panel/src/api/generated.ts` y `OpenApiContractTests.cs` -- publicar esquemas OpenAPI de `/api/events/manual` y `/api/events/{eventId}`, regenerar `generated.ts` y validar ambos DTOs en la prueba de contrato -- cumple AD-14.
- [x] `panel/src/api/client.ts`, `panel/src/components/LaunchWorkModal.tsx`, `panel/src/App.tsx`, `panel/src/styles.css` y `src/CodeNetix.Host/wwwroot/` -- implementar lanzamiento desde el Panel con `requestId` nuevo, errores idénticos al CLI, espera de `runId`, navegación a **En vivo** y compilación a `wwwroot/` -- cumple FR-4 y AD-4.
- [x] `tests/CodeNetix.Host.Tests/LaunchFromPanelTests.cs` y `panel/src/__tests__/launch.test.ts` -- cubrir las 6 filas de la matriz I/O (incluida paridad exacta de error entre `POST /api/events/manual` y `FixMode` CLI).

**Acceptance Criteria:**
- Given el Panel autenticado, when el usuario pega una URL válida de issue de GitHub y confirma, then el Panel llama a `POST /api/events/manual` con un `requestId` GUID nuevo (AD-4), espera el `runId` y navega a la nueva Ejecución en la vista **En vivo** (FR-4).
- Given el Panel autenticado, when el usuario introduce una URL no válida o no admitida y confirma, then el Panel muestra el mismo mensaje de error que el CLI (`codenetix fix`).

## Implementation Notes
- Publicados los esquemas OpenAPI de `/api/events/manual` (con `AddDocumentTransformer` en `ServeMode.cs` para `SubmitManualEventRequest` sin alterar el `400 ProblemDetails` en `text/plain`) y `/api/events/{eventId}`, regenerados `panel/openapi/v1.json` y `panel/src/api/generated.ts` (42 esquemas).
- Añadidos `generateRequestId`, `submitManualEvent`, `fetchEvent`, `waitForEventRun` en `panel/src/api/client.ts` y diálogo accesible `panel/src/components/LaunchWorkModal.tsx` integrado en `panel/src/App.tsx`.
- Verificación: `npm --prefix panel run check:api` OK, `npm --prefix panel test` (61/61 Vitest), `npm --prefix panel run build` (0 errores, `wwwroot/` actualizado), `dotnet test CodeNetix.slnx` (943/943, 0 errores, 0 omitidos, 0 warnings).

## Spec Change Log

## Review Triage Log
- **Capa 1 (`blind-hunter`), Capa 2 (`edge-case-hunter`), Capa 3 (`verification-gap`) — Iteración 1:**
  - `[PATCH][HIGH]` (`edge-case-hunter` #1, #2, #6, `blind-hunter` #2, #3, #4, #6): Endurecer `LaunchWorkModal.tsx`: bloquear cierre (`Escape`/backdrop/botones) en `phase === 'submitting'` y refrescar `runs` al cerrar en `'waiting'`, usar `onMouseDown` en backdrop, añadir guarda síncrona `abortRef.current !== null` contra doble submit, limpiar estado en `!open` sin prop `initialError`, y no borrar aviso de Evento ya aceptado tras timeout/error de sondeo.
  - `[PATCH][HIGH]` (`blind-hunter` #1, #5, #7, `edge-case-hunter` #4): Añadir `AbortController` (`runsAbortRef`) en `refreshRuns` de `App.tsx`, recuperar `preferredRunId` vía `fetchRunDetail` si no aparece en el primer listado, mostrar carga en lugar de `empty-workspace` mientras `loadingRuns` resuelve `selectedRunId`, y memorizar `onClose`/`onRefreshRuns` con `useCallback`.
  - `[PATCH][MEDIUM]` (`edge-case-hunter` #3, #7): Hacer `waitForEventRun` tolerante a fallos transitorios de red/5xx hasta `deadline` (relanzando `401`/`4xx`/`AbortError`), usar `.trim()` en `readProblemDetail`, y añadir `white-space: pre-line` en `.error-banner`.
  - `[PATCH][HIGH]` (`verification-gap` #1–#5, `edge-case-hunter` #5, `blind-hunter` #8): Proteger `document.Paths`/`pathItem` en `ServeMode.cs` y corregir comentario en `SubmitManualEventEndpoint.cs`; validar `SubmitManualEventRequest` (`requestBody`) en `OpenApiContractTests.cs`; añadir paridad `501` Sentry contra `FixMode.RunAsync` y caso `Ignored` en `LaunchFromPanelTests.cs`; extraer/probar `executeLaunchWork` + `401`/aborto previo/reintento transitorio en `panel/src/__tests__/launch.test.ts`.
