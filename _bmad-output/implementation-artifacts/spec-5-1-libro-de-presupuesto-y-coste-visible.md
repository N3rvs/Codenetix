---
title: 'Historia 5.1: Libro de presupuesto y coste visible'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: '04bf73e46abc768232520278787ac1fd3b0fa463'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-5-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Aunque CodeNetix ya registra eventos `TokensConsumed` en el Gateway del Programador, no existe un libro de presupuesto persistente (`run_budget`), los agentes Analista y Revisores no registran su consumo a través de un middleware obligatorio de `IChatClient`, no hay catálogo de precios por modelo y el coste real no aparece en la API, en el Panel ni en el cuerpo del PR.

**Approach:** Definir `IBudgetLedger`, `ModelPricing`, `BudgetChargeRequest`, `BudgetChargeResult` y `RunCostSummary` en `CodeNetix.Contracts`; añadir la tabla de precios por modelo en la Configuración de instancia (con catálogo por defecto documentado); crear la tabla `run_budget` (migración EF Core en `CodeNetix.Pipeline`) e implementar `BudgetLedger` que anota cada cargo en `run_budget` y emite `TokensConsumed` atómicamente (AD-15); cablear `LlmReverseProxy` (Programador) y un `DelegatingChatClient` obligatorio en `CodeNetix.Agents` (Analista y Revisores) hacia `IBudgetLedger`; y mostrar el coste estimado —o `"no estimable"` si algún modelo carece de precio conocido— en la API (`GET /api/runs`, `GET /api/runs/{id}`), en el Panel (`panel/`) y en el cuerpo del PR (`PullRequestBodyBuilder`).

## Boundaries & Constraints

**Always:**
- **Contratos y aislamiento modular (AD-1, AD-15):**
  - `CodeNetix.Contracts/Budget.cs` define `ModelPricing(decimal InputPerMillionUsd, decimal OutputPerMillionUsd)`, `BudgetChargeRequest`, `BudgetChargeOutcome` (`Recorded`, `Exhausted`), `BudgetChargeResult`, `RunCostSummary(decimal? EstimatedCostUsd, bool IsEstimable, bool HasUnpricedModels, int TotalInputTokens, int TotalOutputTokens, decimal? BudgetLimitUsd)` e `IBudgetLedger` (`ChargeAsync`, `GetRunCostAsync`).
  - `CodeNetix.Pipeline` posee la tabla `run_budget` (migración EF Core real generada en `CodeNetix.Pipeline/Data/Migrations/`, actualizando el contador de migraciones `__ef_migrations_run` en `SubmitManualEventTests.cs` de 6 a 7) e implementa `BudgetLedger`.
  - `BudgetLedger.ChargeAsync` persiste el `RunBudgetRecord` en `run_budget` y añade el `RunEvent` `TokensConsumed` en el **mismo `SaveChangesAsync`** + `SaveAndPublishAsync` (AD-3, AD-15).
- **Tabla de precios por modelo (AD-15):**
  - Catálogo por defecto documentado (`ModelPricingCatalog`) con precios USD por millón de tokens (entrada/salida) para los modelos estándar (Anthropic `claude-*`, OpenAI `gpt-*`/`o3`/`o4-mini`), con coincidencia exacta insensible a mayúsculas y resolución de sufijos de fecha (`-YYYYMMDD`).
  - Configuración de instancia (`CODENETIX__Pricing__<Modelo>__InputPerMillionUsd` / `OutputPerMillionUsd`) que permite añadir o sobreescribir precios; validada en `InstanceOptionsValidator` (valores `< 0` → error de configuración).
  - Un modelo sin precio conocido (incluidos los locales como Ollama/vLLM/LM Studio) registra `CostUsd = null`, `IsPriced = false` y hace que `RunCostSummary` tenga `IsEstimable = false`, `HasUnpricedModels = true` y `EstimatedCostUsd = null`, mostrándose como **"no estimable"** en API/Panel/PR.
- **Middleware obligatorio en `IChatClient` y Gateway (AD-15):**
  - `CodeNetix.Agents` implementa `BudgetTrackingChatClient` (`DelegatingChatClient` de `Microsoft.Extensions.AI`) aplicado obligatoriamente a las llamadas de `Analyst` y `ReviewerAgent`, invocando `IBudgetLedger.ChargeAsync` con los tokens (`Usage.InputTokenCount` / `Usage.OutputTokenCount`) y `StageId` cuando aplique.
  - `CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs` registra el consumo del Programador llamando a `IBudgetLedger.ChargeAsync` (en lugar de emitir `TokensConsumed` directamente por `IRunEventSink`), evitando duplicar `TokensConsumed`.
- **Coste visible en API, Panel y cuerpo del PR:**
  - `RunSummary` y `RunResponse` incluyen `RunCostSummary? Cost` (poblado por `RunReader` / `GetRunEndpoint` / `ListRunsEndpoint`).
  - `PullRequestBodyBuilder.BuildBody` recibe `RunCostSummary?` (desde `RunOrchestrator.PublishPullRequestAsync`): muestra `$X.XXXX USD` cuando `IsEstimable`, `"no estimable"` cuando `HasUnpricedModels`, y `"no disponible"` si no hubo cargos.
  - Regenerar `panel/openapi/v1.json` y `panel/src/api/generated.ts` (AD-14), actualizar `formatCostDisplay` y mostrar el coste en **Historial** (lista y detalle) y en la cabecera de **En vivo** (mostrando `"No estimable"` cuando `hasUnpricedModels`, `$X.XXXX USD` cuando es estimable y `"No disponible"` sin cargos). Compilar `panel/` hacia `src/CodeNetix.Host/wwwroot/`.

**Never:**
- No emitir `TokensConsumed` duplicado (ni en `LlmReverseProxy` ni en `BudgetTrackingChatClient`: solo `BudgetLedger.ChargeAsync` emite `TokensConsumed`).
- No estimar ni inventar un coste `$0.00` para modelos locales o desconocidos: deben mostrarse explícitamente como `"no estimable"`.
- No bloquear ni abortar la Ejecución en Story 5.1 cuando se alcance `budget_usd` (el corte automático `Exhausted` / `402` / `BudgetExceeded` corresponde a la Historia 5.2).
- No desincronizar `panel/openapi/v1.json` ni `panel/src/api/generated.ts`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Consumo del Programador vía Gateway (`LlmReverseProxy`) | Respuesta SSE/JSON con `model = "claude-sonnet-5-5"` y `usage` (1000 in, 500 out) | `IBudgetLedger.ChargeAsync` inserta fila en `run_budget` con `CostUsd` calculado y emite exactamente 1 `TokensConsumed` en `run_events` | Si `usage` es 0/ausente, no crea cargo vacío |
| Consumo de Analista y Revisores vía `IChatClient` | `Analyst.AnalyzeAsync` y `ReviewerAgent.ReviewAsync` con `ChatResponse.Usage` | `BudgetTrackingChatClient` invoca `IBudgetLedger.ChargeAsync` con rol, proveedor, modelo, `stageId` y tokens; `run_budget` + `TokensConsumed` reflejan todos los agentes | Si el proveedor no devuelve `Usage`, no falla la llamada |
| Modelo sin precio conocido (local o custom) | Consumo registrado con `model = "qwen2.5-coder:32b"` (sin entrada en catálogo ni config) | `run_budget` guarda `CostUsd = null`, `IsPriced = false`; `RunCostSummary.IsEstimable == false`, `HasUnpricedModels == true`; Panel y cuerpo del PR muestran `"no estimable"` | Nunca muestra `$0.00` ni `NaN` |
| Override de precio en Configuración de instancia | `CODENETIX__Pricing__mi-modelo__InputPerMillionUsd=2.5`, `OutputPerMillionUsd=10` | `BudgetLedger` usa el precio configurado para calcular `CostUsd`; precio negativo falla validación en `InstanceOptionsValidator` | Validación en arranque (`Diagnostic`) |
| Visibilidad en API, Panel y cuerpo del PR | Ejecución con cargos en `run_budget` | `GET /api/runs` y `GET /api/runs/{id}` devuelven `cost` (`RunCostSummary`); cuerpo del PR incluye coste en `## Coste`; Panel muestra coste o `"No estimable"` | Sin cargos → `"No disponible"` |
| Contrato OpenAPI y migración EF Core | `/openapi/v1.json` + migración `AddRunBudget` | `OpenApiContractTests` y `check:api` pasan sin drift; `SubmitManualEventTests` verifica 7 migraciones en `__ef_migrations_run` | `dotnet test` + `panel` build y tests con 0 errores y 0 warnings |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/Budget.cs` (nuevo) y `Runs.cs` -- tipos `ModelPricing`, `BudgetChargeRequest`, `BudgetChargeOutcome`, `BudgetChargeResult`, `RunCostSummary`, `IBudgetLedger`, y propiedad opcional `RunCostSummary? Cost` en `RunSummary`.
- `src/CodeNetix.Pipeline/Domain/RunBudgetRecord.cs` (nuevo), `Data/PipelineDbContext.cs`, `Data/Migrations/*AddRunBudget*` (nuevo), `Features/Budget/ModelPricingCatalog.cs` (nuevo), `Features/Budget/BudgetLedger.cs` (nuevo), `Features/GetRun/RunReader.cs`, `Features/RunPipeline/PullRequestBodyBuilder.cs`, `Features/RunPipeline/RunOrchestrator.cs`, `PipelineServiceCollectionExtensions.cs` -- tabla `run_budget`, catálogo de precios, `BudgetLedger`, coste en lecturas y cuerpo del PR.
- `src/CodeNetix.Agents/ChatClients/BudgetTrackingChatClient.cs` (nuevo), `Features/AnalyzeIssue/Analyst.cs`, `Features/ReviewDiff/ReviewerAgent.cs` -- middleware obligatorio de `IChatClient` hacia `IBudgetLedger`.
- `src/CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs` -- registrar consumo del Programador mediante `IBudgetLedger.ChargeAsync`.
- `src/CodeNetix.Host/Configuration/InstanceOptions.cs`, `InstanceOptionsValidator.cs`, `ServeMode.cs`, `Features/GetRun/GetRunEndpoint.cs` -- enlace y validación de `CODENETIX__Pricing__*` hacia `PricingOptions` y exposición de `Cost` en `RunResponse`.
- `panel/openapi/v1.json`, `panel/src/api/generated.ts`, `panel/src/history/runMetrics.ts`, `panel/src/components/RunHistoryView.tsx`, `panel/src/App.tsx`, `panel/src/__tests__/history.test.ts` -- regeneración OpenAPI/TS, visualización del coste / `"No estimable"` en Panel y rebuild a `src/CodeNetix.Host/wwwroot/`.
- `tests/CodeNetix.Host.Tests/BudgetLedgerTests.cs` (nuevo), `OpenApiContractTests.cs`, `SubmitManualEventTests.cs` -- cobertura de las 6 filas de la matriz I/O.

## Tasks & Acceptance

**Execution:**
- [x] `CodeNetix.Contracts/Budget.cs`, `CodeNetix.Pipeline/` (`run_budget` + migración EF Core `AddRunBudget`, `ModelPricingCatalog`, `BudgetLedger`, `RunReader`, `PullRequestBodyBuilder`, `RunOrchestrator`) -- implementar libro de presupuesto, catálogo de precios y coste en lecturas/PR -- cumple AD-1 y AD-15.
- [x] `CodeNetix.Agents/` (`BudgetTrackingChatClient`, `Analyst`, `ReviewerAgent`), `CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs` y `CodeNetix.Host/` (`InstanceOptions`, `InstanceOptionsValidator`, `ServeMode`, `GetRunEndpoint`) -- cablear middleware obligatorio `IChatClient`, Gateway y configuración `CODENETIX__Pricing__*` -- cumple AD-15.
- [x] `panel/` (`openapi/v1.json`, `src/api/generated.ts`, `runMetrics.ts`, `RunHistoryView.tsx`, `App.tsx`, `src/CodeNetix.Host/wwwroot/`) -- regenerar tipos OpenAPI y mostrar coste estimado o `"No estimable"` en el Panel -- cumple AD-14.
- [x] `tests/CodeNetix.Host.Tests/BudgetLedgerTests.cs`, `OpenApiContractTests.cs`, `SubmitManualEventTests.cs` y `panel/src/__tests__/history.test.ts` -- cubrir las 6 filas de la matriz I/O.

**Acceptance Criteria:**
- Given la tabla de precios por modelo en la Configuración de instancia (con valores por defecto documentados), when el Gateway (Programador) o el middleware obligatorio de `IChatClient` (Analista y Revisores) registran un consumo, then `IBudgetLedger.ChargeAsync` lo anota en `run_budget` y emite `TokensConsumed` (AD-15).
- Given una Ejecución con consumos registrados, when se consulta la API (`GET /api/runs`, `GET /api/runs/{id}`), el Panel o el cuerpo del PR generado, then se muestra el coste estimado en USD, y si intervino cualquier modelo sin precio conocido (incluidos los locales) se muestra explícitamente como `"no estimable"`.

## Implementation Notes
- Creada tabla `run_budget` con migración EF Core `AddRunBudget` (7 migraciones en `__ef_migrations_run`), `ModelPricingCatalog`, `BudgetLedger` (cargo + `TokensConsumed` atómicos en el mismo `SaveAndPublishAsync`), `BudgetTrackingChatClient` (middleware obligatorio en `Analyst` y `ReviewerAgent`) e integración en `LlmReverseProxy`.
- Expuesto `RunCostSummary? Cost` en `RunSummary` y `RunResponse`, regenerados `panel/openapi/v1.json` y `panel/src/api/generated.ts` (43 esquemas), y mostrado el coste / `"No estimable"` en API, Panel y cuerpo del PR (`PullRequestBodyBuilder`).
- Verificación: `npm --prefix panel run check:api` OK, `npm --prefix panel test` (73/73 Vitest), `npm --prefix panel run build` (0 errores, `wwwroot/` actualizado), `dotnet test CodeNetix.slnx` (950/950, 0 errores, 0 omitidos, 0 warnings).

## Spec Change Log

## Review Triage Log
- **Capa 1 (`blind-hunter`), Capa 2 (`edge-case-hunter`), Capa 3 (`verification-gap`) — Iteración 1:**
  - `[PATCH][HIGH]` (`blind-hunter` #1, #2, `edge-case-hunter` #1, #3): En `BudgetTrackingChatClient.cs`, usar `CancellationToken.None` y aislar excepciones de `ChargeAsync` (con `LogWarning`) para no descartar respuestas LLM válidas ni clasificarlas como `ProviderFailed`; envolver `GetStreamingResponseAsync` en `try/finally` usando `Math.Max` por contador de tokens.
  - `[PATCH][HIGH]` (`blind-hunter` #3, `edge-case-hunter` #2): En `panel/src/App.tsx`, coalescer/debounce (~300 ms) con `AbortController` el refresco de coste en `TokensConsumed` e invocarlo también tras `catchUpEvents` cuando recupere `TokensConsumed`.
  - `[PATCH][MEDIUM]` (`blind-hunter` #4, #5, #6, #7, `edge-case-hunter` #4, #5, #6, #7): Acumular tokens en `long` (saturando a `int.MaxValue`) y usar `CancellationToken.None` en el `SummarizeForRunAsync` post-commit de `BudgetLedger`; soportar prefijos `anthropic/`/`openai/` y `-latest` en `ModelPricingCatalog`; exigir ambos precios (`decimal?`) en `ModelPricingInstanceOptions`/`InstanceOptionsValidator`; omitir cargos `0/0` en `LlmReverseProxy`.
  - `[PATCH][HIGH]` (`verification-gap` #1–#7): Ampliar `BudgetLedgerTests.cs` (cuerpo PR generado por `RunOrchestrator` con coste estimado y mixto no estimable, mezcla modelo con precio + sin precio en el mismo run, Gateway SSE y `usage` 0/ausente, `Usage == null` en `Analyst`/`ReviewerAgent`, `GetStreamingResponseAsync`, arranque `Diagnostic` con precio negativo, `ModelPricingCatalog.TryResolve`, run sin cargos en API y concurrencia de `ChargeAsync`), `OpenApiContractTests.cs` (`RunCostSummary` con nulos) y `panel/src/__tests__/history.test.ts` (`formatCostSubtext` y lista de `RunHistoryView`).
