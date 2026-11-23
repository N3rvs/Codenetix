# Epic 5 Context: Control de coste y fiabilidad

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Garantizar control económico real y recuperación limpia ante reinicios antes del lanzamiento público: registrar cada consumo de tokens y su coste en un libro de presupuesto persistente (`run_budget`, AD-15) visible en API, Panel y cuerpo del PR; cortar automáticamente cualquier Ejecución al agotar `budget_usd` (FR-13, doble barrera Gateway `402` + `--max-budget-usd`); y asegurar que un reinicio del Host reconcilia Ejecuciones huérfanas (`Failed(HostRestarted)`) y contenedores/volúmenes de Sandbox antes de arrancar el Dispatcher (AD-18, FR-24).

## Stories

- Story 5.1: Libro de presupuesto y coste visible
- Story 5.2: Corte automático por presupuesto
- Story 5.3: Comportamiento tras un reinicio

## Requirements & Constraints

- **Libro de presupuesto (AD-15):** `IBudgetLedger.ChargeAsync` es el único punto de registro de consumo; persiste cada cargo en `run_budget` y emite `TokensConsumed` en la misma transacción SQLite. Tanto `LlmReverseProxy` (Programador) como un middleware obligatorio de `IChatClient` (Analista y Revisores) pasan por `IBudgetLedger`.
- **Precios por modelo:** La Configuración de instancia incluye tabla de precios por modelo (`CODENETIX__Pricing__*`) con valores por defecto documentados para los modelos comerciales habituales; los modelos sin precio conocido (incluidos los locales) se marcan como no estimables y se muestran explícitamente como `"no estimable"`.
- **Corte por presupuesto (FR-13, Story 5.2):** Al superar `budget_usd` de `EffectiveConfig`, `ChargeAsync` devuelve `Exhausted`, el Gateway responde `402` con `codenetix:budget_exhausted`, el adaptador/orquestador cierra la Ejecución como `Failed(BudgetExceeded)`, y Claude Code recibe además `--max-budget-usd <restante>`.
- **Reconciliación tras reinicio (AD-18, FR-24, Story 5.3):** `StartupReconciler` ejecuta estrictamente: migraciones → salud → Ejecuciones no terminales a `Failed(HostRestarted)` → limpieza de contenedores `codenetix.run` y volúmenes `codenetix-ws-*` → arranque del Dispatcher (los Eventos `Accepted` sin Ejecución se despachan después).

## Technical Decisions

- **Aislamiento modular (AD-1):** `IBudgetLedger`, `ModelPricing`, `BudgetChargeRequest`, `BudgetChargeResult` y `RunCostSummary` viven en `CodeNetix.Contracts`. `CodeNetix.Pipeline` posee la tabla `run_budget` (migración EF Core en `__ef_migrations_run`) e implementa `BudgetLedger`. `CodeNetix.Agents` aplica el middleware `DelegatingChatClient` sobre `IChatClient`. `CodeNetix.Sandbox` llama a `IBudgetLedger` desde `LlmReverseProxy`.
- **Contrato OpenAPI (AD-14):** `RunCostSummary` se expone en `RunSummary` y `RunResponse`, reflejado en `panel/openapi/v1.json` y `panel/src/api/generated.ts`.
