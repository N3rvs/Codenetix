---
title: 'Historia 4.2: Terminal en vivo'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: '7069a4befab9fa5736ddc99edffb3eacb0314f28'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-4-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Aunque el Panel muestra en vivo el grafo de Etapas de cada Ejecución (Historia 4.1), al seleccionar un nodo el desarrollador no puede inspeccionar en una terminal interactiva la salida real emitida por el agente o las pruebas (`OutputAppended`), ni ver de forma clara cuándo se alcanzó el límite de 5 MB (`OutputTruncated`).

**Approach:** Integrar una terminal en vivo basada en `xterm.js` (`@xterm/xterm` + `@xterm/addon-fit`) acoplada a la selección de nodos del grafo React Flow en `panel/`, alimentada por los eventos `OutputAppended` y `OutputTruncated` recibidos vía `/hubs/runs` y `GET /api/runs/{id}/events?afterSeq=N` con agrupación en lotes de hasta 250 ms (`TerminalBatcher`), resaltado visual del nodo seleccionado y aviso explícito de truncado (5 MB). Añadir pruebas de integración end-to-end en `CodeNetix.Host.Tests` verificando el streaming de `OutputAppended` con secretos ya enmascarados (AD-13) y el corte con `OutputTruncated` al superar 5 MB por Etapa, junto a pruebas unitarias en `panel/` (Vitest) para el mapeo nodo→salida, agrupación ≤ 250 ms y detección de `OutputTruncated`.

## Boundaries & Constraints

**Always:**
- **Terminal xterm.js en `panel/` (FR-21):**
  - Instalar `@xterm/xterm` y `@xterm/addon-fit` en `panel/package.json` y renderizar la terminal en un componente dedicado (`panel/src/components/LiveTerminal.tsx`) con ajuste automático de tamaño (`FitAddon` + `ResizeObserver`) y limpieza completa de recursos (`terminal.dispose()`, `observer.disconnect()`, `clearTimeout`) al desmontar o cambiar de nodo/ejecución.
  - Permitir seleccionar cualquier nodo del grafo React Flow (`onNodeClick`), resaltando visualmente el nodo seleccionado. Si el usuario no ha seleccionado manualmente otro nodo, seleccionar por defecto el nodo activo (o el último con salida disponible).
  - Mostrar la salida de los eventos `OutputAppended` pertenecientes al nodo seleccionado (`stdout` en color estándar, `stderr` distinguido con secuencia ANSI segura, normalizando saltos de línea `\n` → `\r\n` para xterm.js).
- **Agrupación en lotes ≤ 250 ms (FR-21):**
  - Implementar un agrupador determinista (`panel/src/terminal/outputBatcher.ts`) que acumule fragmentos entrantes y los vuelque a la terminal en lotes de **como máximo 250 ms**, con `flushNow()` inmediato al cambiar de nodo o al llegar a estado terminal/truncado, evitando escrituras excesivas al DOM sin perder ningún fragmento ni reordenar por `seq`.
- **Mapeo determinista de eventos a nodo (`panel/src/terminal/stageOutput.ts`):**
  - Asociar cada `OutputAppended` y `OutputTruncated` al nodo del grafo correspondiente (priorizando `stageId` cuando se conozca su relación con una Etapa/Revisor y usando como respaldo el estado activo en el `seq` del evento, p. ej. `Coding`, `Analyzing`, `Reviewing`).
  - Ofrecer un selector ligero en la cabecera de la terminal para alternar entre la salida del **nodo seleccionado** y **toda la Ejecución**, mostrando claramente cuántos eventos de salida tiene el nodo y su estado vacío si aún no emitió salida.
- **Aviso explícito de `OutputTruncated` (5 MB por Etapa):**
  - Cuando exista un evento `OutputTruncated` para el nodo seleccionado (o en la vista completa), mostrar un banner de advertencia destacado encima de la terminal indicando el límite alcanzado (p. ej. `5 MB`) y añadir una línea de aviso dentro de la terminal.
- **Secretos enmascarados (AD-13):**
  - Toda redacción de secretos sigue ocurriendo en el servidor (`RunEventSink` + `ISecretRedactor`) antes de persistir en SQLite y antes de emitir por `/hubs/runs`. El Panel nunca recibe ni renderiza secretos en claro.
  - Verificar con prueba de integración en `CodeNetix.Host.Tests` que `OutputAppended` con secretos (p. ej. clave Anthropic, token GitHub y `admin-token`) llega enmascarado tanto por `/hubs/runs` como por `GET /api/runs/{id}/events`, y que al superar 5 MiB por Etapa se emite exactamente un `OutputTruncated` por `/hubs/runs` y se descartan los fragmentos posteriores.
- **Contratos y build:**
  - Usar exclusivamente tipos generados en `panel/src/api/generated.ts` (AD-14).
  - Mantener `npm --prefix panel run check:api`, `npm --prefix panel test`, `npm --prefix panel run build` (actualizando `src/CodeNetix.Host/wwwroot/`) y `dotnet test CodeNetix.slnx` con 0 errores y 0 warnings.

**Never:**
- No emitir ni persistir secretos sin redactar en `OutputAppended`.
- No superar 250 ms de retención artificial en el agrupador de salida ni perder chunks al cambiar de nodo o desmontar.
- No introducir dependencias de backend innecesarias ni romper la secuencia `seq` 1-a-1 de `RunEvent` en SQLite/SignalR.
- No usar `innerHTML` ni renderizar salida de agente como HTML sin escapar (toda la salida va exclusivamente por `xterm.js` o texto plano React).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Selección de nodo con `OutputAppended` | Ejecución en `Coding` con eventos `OutputAppended` (`stdout` y `stderr`) | Al hacer clic en el nodo `Coding`, xterm.js muestra toda la salida acumulada y sigue añadiendo nuevos fragmentos en vivo | Si el nodo no tiene salida, muestra estado vacío claro sin errores |
| Agrupación en lotes ≤ 250 ms | Ráfaga rápida de 50 `OutputAppended` en < 50 ms | `outputBatcher` agrupa los fragmentos en 1 sola escritura a xterm.js en ≤ 250 ms preservando el orden por `seq` | Al cambiar de nodo o desmontar, cancela temporizadores pendientes y no escribe en la instancia anterior |
| Secretos enmascarados en vivo (AD-13) | `OutputAppended` emitido con `ANTHROPIC_API_KEY`, `ghp_...` o `admin-token` | Tanto `/hubs/runs` como `/api/runs/{id}/events` entregan el texto ya enmascarado; ningún secreto aparece en claro | Validado en test de integración de Host |
| Límite de 5 MB y `OutputTruncated` | Etapa emite > 5 MiB de `OutputAppended` | `RunEventSink` trunca en 5 MiB (respetando UTF-8), emite un único `OutputTruncated` por `/hubs/runs` y descarta lo posterior; el Panel muestra banner de aviso de truncado | `OutputTruncated` con `maxBytes` personalizado muestra el tamaño real formateado |
| Cambio de nodo / cambio de Ejecución | Usuario pasa de nodo `Coding` a `Analyzing` o de `runA` a `runB` | La terminal limpia (`reset`) y renderiza solo la salida del nuevo nodo/ejecución sin mezclar buffers previos | `FitAddon` + `ResizeObserver` protegidos si el contenedor aún no tiene dimensiones |
| Reconexión SignalR con salida en curso | Caída de red tras `seq = 10` mientras llegan `OutputAppended` `11..15` | Tras reconectar (`afterSeq=10`), solo se añaden los fragmentos nuevos (`11..15`) sin duplicar líneas ya escritas en xterm.js | Deduplicación estricta por `seq` |

</frozen-after-approval>

## Code Map

- `panel/package.json` -- añadir `@xterm/xterm` y `@xterm/addon-fit`.
- `panel/src/terminal/outputBatcher.ts` (nuevo) -- agrupador determinista de salida en lotes de ≤ 250 ms con `push`, `flushNow` y `dispose`.
- `panel/src/terminal/stageOutput.ts` (nuevo) -- extracción/filtrado determinista de `OutputAppended` y `OutputTruncated` por nodo del grafo (o ejecución completa) y formateo seguro para xterm (`\r\n`, distinción `stderr`).
- `panel/src/components/LiveTerminal.tsx` (nuevo) -- componente React que envuelve `xterm.js` + `FitAddon`, banner de `OutputTruncated`, selector nodo/ejecución y limpieza de recursos.
- `panel/src/graph/buildPipelineGraph.ts`, `panel/src/App.tsx`, `panel/src/styles.css` -- soporte de nodo seleccionado (`selected`), `onNodeClick`, integración del panel de terminal en vivo y estilos xterm.
- `panel/src/__tests__/terminal.test.ts` (nuevo) -- pruebas unitarias Vitest para `outputBatcher` (con fake timers ≤ 250 ms), `stageOutput` (mapeo por nodo, `stderr`, `OutputTruncated`, deduplicación por `seq`).
- `tests/CodeNetix.Host.Tests/LiveTerminalHubTests.cs` (nuevo) -- pruebas de integración del Host verificando streaming en vivo de `OutputAppended` con secretos redactados (AD-13) y truncado a 5 MiB con `OutputTruncated` por `/hubs/runs` y `/api/runs/{id}/events`.
- `src/CodeNetix.Host/wwwroot/` -- rebuild de `panel/` (`npm run build`).

## Tasks & Acceptance

**Execution:**
- [x] `panel/package.json`, `panel/src/terminal/outputBatcher.ts` y `panel/src/terminal/stageOutput.ts` -- instalar `@xterm/xterm` y `@xterm/addon-fit`, implementar agrupación de lotes ≤ 250 ms y mapeo determinista de `OutputAppended`/`OutputTruncated` a nodos del grafo -- cumple FR-21.
- [x] `panel/src/components/LiveTerminal.tsx`, `panel/src/graph/buildPipelineGraph.ts`, `panel/src/App.tsx` y `panel/src/styles.css` -- conectar selección de nodos en React Flow con la terminal xterm.js en vivo, aviso destacado de `OutputTruncated`, seguimiento incremental sin duplicados tras `afterSeq` y compilar a `src/CodeNetix.Host/wwwroot/` -- cumple FR-21 y AD-14.
- [x] `panel/src/__tests__/terminal.test.ts` y `tests/CodeNetix.Host.Tests/LiveTerminalHubTests.cs` -- cubrir las 6 filas de la matriz I/O: selección de nodo, batching ≤ 250 ms, secretos enmascarados en `/hubs/runs` y `/events` (AD-13), corte en 5 MiB + `OutputTruncated`, cambio de nodo/ejecución y reconexión sin duplicados.

**Acceptance Criteria:**
- Given una Etapa activa que emite `OutputAppended`, when el usuario selecciona el nodo en el grafo React Flow, then ve la salida en directo en una terminal xterm.js en lotes de 250 ms como máximo (FR-21).
- Given salida con secretos emitida por un agente o proceso del Sandbox, when llega al Panel por `/hubs/runs` o `/api/runs/{id}/events`, then los secretos aparecen enmascarados y nunca en claro (AD-13).
- Given una Etapa que supera los 5 MB de salida, when el sumidero emite `OutputTruncated`, then se descarta el resto de la salida y la terminal muestra un aviso explícito de truncado.

## Implementation Notes
- Instalados `@xterm/xterm` y `@xterm/addon-fit` en `panel/package.json` y creado `panel/src/terminal/outputBatcher.ts` (ventana máxima 250 ms), `panel/src/terminal/stageOutput.ts` (mapeo por `stageId` + `StateChanged`, saneamiento ANSI/OSC y `stderr`) y `panel/src/components/LiveTerminal.tsx`.
- Conectado `onNodeClick` y resaltado de nodo seleccionado en `panel/src/graph/buildPipelineGraph.ts` y `panel/src/App.tsx`.
- Verificación: `npm --prefix panel run check:api` OK, `npm --prefix panel test` (13/13 Vitest), `npm --prefix panel run build` (0 errores, `wwwroot/` actualizado), `dotnet test CodeNetix.slnx` (925/925, 0 errores, 0 omitidos, 0 warnings).

## Spec Change Log

## Review Triage Log
- **Capa 1 (`blind-hunter`), Capa 2 (`edge-case-hunter`), Capa 3 (`verification-gap`) — Iteración 1:**
  - `[PATCH][CRITICAL]` (`blind-hunter` #1, `edge-case-hunter` #1): Evitar contaminación cruzada al cambiar `selectedRunId` (`runA` → `runB`): filtrar `events` por `selectedRunId` en `App.tsx`, limpiar `events` sincrónicamente al cambiar de Ejecución y filtrar por `ev.runId === runId` en `LiveTerminal.tsx`/`extractStageTerminalView`.
  - `[PATCH][HIGH]` (`blind-hunter` #2, `edge-case-hunter` #2, #3, #4, #5): Reemplazar `lastWrittenSeqRef` por `writtenSeqsRef: number[]` sobre una línea temporal ordenada que intercala `OutputAppended` y `OutputTruncated` por `seq`; si el nuevo timeline extiende el prefijo exacto, escribir solo el sufijo vía `OutputBatcher`, y si hubo inserción por hueco `afterSeq`, re-correlación tardía de `stageId` o cambio de contexto, resetear la terminal y volcar en orden; invocar `flushNow()` también al alcanzar estado terminal.
  - `[PATCH][HIGH]` (`blind-hunter` #3, `edge-case-hunter` #6): Endurecer `formatOutputChunkForTerminal` con lista blanca estricta SGR (`\x1b\[[0-9;]*m`), eliminando controles C1 (`[\u0080-\u009f]`), OSC/DCS/APC/RIS (`\x1bc`) y CSI no-SGR; preservar `\r` sueltos normalizando solo `\n` sin `\r` previo.
  - `[PATCH][HIGH]` (`verification-gap` #1, #2, #3, #4, #5, `edge-case-hunter` #7): Ampliar `LiveTerminalHubTests.cs` con corte UTF-8 multibyte exacto en la frontera de 5 MiB y test de reconexión SignalR + `afterSeq` durante streaming de `OutputAppended`; añadir tests de `LiveTerminal` (cambio de ejecución/nodo, hueco `afterSeq`, banner `OutputTruncated`, `ResizeObserver` y cleanup) y casos borde en `panel/src/__tests__/terminal.test.ts` (límite exacto 250 ms, `NaN`, `selectedNodeId`, C1/RIS/CSI).
  - `[PATCH][MEDIUM/LOW]` (`blind-hunter` #4, #5, #6, `edge-case-hunter` #7): Evitar doble llamada a `buildPipelineGraph` en `App.tsx`, eliminar `fitAddonRef` muerto y diferir `safeFit()` con `requestAnimationFrame` en `LiveTerminal.tsx`, añadir `aria-pressed` a los botones de ámbito, añadir guarda `Number.isFinite(windowMs)` en `outputBatcher.ts` y evitar recorte vertical en `.workspace`/`.graph-container`.
