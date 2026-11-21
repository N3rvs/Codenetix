# Epic 4 Context: Panel en vivo

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Ofrecer un Panel web en tiempo real (React + Vite + TypeScript servido por el propio Host en `:5000`) que haga visible y gobernable cada Ejecución: mostrar la cadena de montaje nodo a nodo con React Flow en menos de 1 s vía SignalR (`/hubs/runs`), inspeccionar la salida en directo con xterm.js, consultar el historial persistido en `/data`, cancelar Ejecuciones en curso y lanzar trabajo pegando una URL de issue sin necesidad de usar la terminal.

## Stories

- Story 4.1: Cadena de montaje en vivo
- Story 4.2: Terminal en vivo
- Story 4.3: Historial de Ejecuciones
- Story 4.4: Cancelar una Ejecución
- Story 4.5: Lanzar trabajo desde el Panel

## Requirements & Constraints

- **Autenticación del Panel (AD-17):** La primera vez que se abre el Panel, solicita el `admin-token` y lo guarda en el navegador (`localStorage`). Todas las llamadas `/api/**` usan `Authorization: Bearer <admin-token>`. En `/hubs/**`, `AdminTokenMiddleware` acepta además el parámetro de consulta `access_token` (estándar de SignalR WebSocket/SSE en navegador) sin exponerlo nunca en logs.
- **Cadena de montaje en vivo (FR-20):** El Panel se suscribe al hub SignalR `/hubs/runs`, muestra un grafo React Flow con las Etapas (`Received`, `Analyzing`, `Coding`, `Testing`, `Review` con un nodo por cada Revisor activo, `Deciding`, `PullRequestCreated`/`Failed`/`Cancelled`) y resalta el nodo activo. Cada cambio de Estado se refleja en menos de 1 s sin recargar.
- **Reanudación sin pérdida (AD-3):** Los eventos se publican al hub únicamente después del commit en SQLite. Al reconectar, el Panel reanuda con `GET /api/runs/<runId>/events?afterSeq=N` y fusiona por `seq` sin duplicados ni huecos.
- **Contratos generados desde OpenAPI (AD-14):** El Host publica el documento OpenAPI con la unión discriminada polimórfica de `RunEvent`; el Panel importa únicamente tipos TypeScript generados desde ese OpenAPI y una prueba de contrato valida un ejemplo de cada tipo de `RunEvent` contra el esquema.
- **Terminal en vivo (FR-21):** Al seleccionar un nodo que emite `OutputAppended`, el Panel muestra su salida en xterm.js con lotes de hasta 250 ms, secretos ya enmascarados (AD-13) y aviso explícito ante `OutputTruncated` (5 MB por Etapa).
- **Historial persistido (FR-22):** Lista y detalle de Ejecuciones pasadas (Estado, `FailureReason`, Spec, Veredictos, enlace al PR, duración y coste cuando exista) conservado tras reinicio si persiste `/data`.
- **Cancelación segura (FR-23, AD-2):** Botón "Cancelar" disponible en `Received`, `Analyzing`, `Coding` y `Reviewing` (`IRunControl.RequestCancel` → Sandbox detenido en ≤ 10 s, sin PR y con aviso `RunCancelled` en la issue); en `Deciding` o estados terminales la API responde `409 Conflict` y el botón aparece deshabilitado.
- **Lanzar trabajo desde el Panel (FR-4, AD-4):** Formulario autenticado para pegar una URL de issue de GitHub que invoca `POST /api/events/manual` con un `requestId` nuevo y navega a la Ejecución, mostrando el mismo error que el CLI ante URLs inválidas.

## Technical Decisions

- **Fronteras de módulos (AD-1, AD-3):** `CodeNetix.Contracts` define `IRunEventPublisher` (puente desacoplado post-commit). `CodeNetix.Pipeline` publica cada `RunEvent` tras `SaveChangesAsync` a través de `IRunEventPublisher`. `CodeNetix.Host` hospeda `RunsHub` (`/hubs/runs`), `RunsHubEventPublisher`, el endpoint OpenAPI (`/openapi/v1.json`) y sirve el build estático de `panel/` desde `wwwroot/`.
- **Diseño visual del Panel:** Estética limpia, oscura orientada a desarrolladores ("observabilidad de ingeniería"): cabecera con estado de instancia y gestión de token, barra lateral/selector de Ejecuciones, lienzo principal React Flow con nodos claros por etapa (colores semánticos: pendiente, activo pulsante, completado, fallido, cancelado) y panel de inspección contextual.

## Cross-Story Dependencies

- **1.3 (deferred) → 4.1:** Habilitar `access_token` en query únicamente para `/hubs/**` en `AdminTokenMiddleware` (cerrando la entrada correspondiente en `deferred-work.md`).
- **4.1 → 4.2:** La 4.2 acopla la terminal xterm.js al nodo seleccionado del grafo construido en la 4.1 y añade agrupación de lotes de salida (250 ms).
- **4.1 → 4.3:** La 4.3 amplía el listado y la vista de detalle histórico sobre los endpoints `GET /api/runs` y `GET /api/runs/{id}`.
- **3.5 + 4.1 → 4.4:** La 4.4 añade `IRunControl.RequestCancel` + `POST /api/runs/{id}/cancel` e invoca `NotifyTerminalAsync` de la 3.5 para `RunCancelled`.
- **4.1 → 4.5:** La 4.5 añade el diálogo/formulario de lanzamiento manual (`POST /api/events/manual`) integrado en la cabecera del Panel.
