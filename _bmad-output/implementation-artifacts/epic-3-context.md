# Epic 3 Context: Revisión independiente y PR

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Evaluar de forma independiente los cambios verificados por el Sandbox mediante un comité de Revisores especializados (seguridad, calidad y tests) que nunca ven el razonamiento ni la salida del Programador, iterar en Rondas de corrección cuando solicitan cambios, clasificar determinísticamente el riesgo del cambio y, ante aprobación unánime, crear un único commit limpio y abrir un Pull Request trazable con notificación final en la issue de origen.

## Stories

- Story 3.1: Revisores independientes emiten Veredictos
- Story 3.2: Rondas de revisión
- Story 3.3: PR con traza completa
- Story 3.4: Clasificación de riesgo determinista
- Story 3.5: Aviso en la issue al terminar

## Requirements & Constraints

- **Independencia de Revisores:** Cada Revisor activo según la `EffectiveConfig` (por defecto `security`, `quality`, `tests`) corre en el host sin ejecutar código y recibe **únicamente** la `Spec`, el `Diff` calculado contra `BaseSha` y el `TestReport` — jamás el historial ni la salida del Programador.
- **Proveedores heterogéneos:** Cada Revisor puede usar un Proveedor o modelo distinto compatible con OpenAI (incluidos modelos locales como Ollama o LM Studio). Si el modelo no admite `response_format: json_schema`, el esquema viaja en el prompt y se valida igualmente.
- **Veredicto estructurado:** Cada Revisor emite un `Verdict` validado contra `schemas/verdict.v1.json` con decisión (`Approved`, `ChangesRequested`, `Rejected`), resumen y hallazgos con archivo, línea y descripción. Una respuesta que siga siendo inválida tras agotar los reintentos de esquema cuenta como `ChangesRequested`.
- **Rondas de revisión:** Si al menos un Revisor devuelve `ChangesRequested` y quedan Rondas (`EffectiveConfig.MaxReviewRounds`, 2 por defecto), la Ejecución vuelve a `Coding` pasando los hallazgos como feedback y repite compilación, tests y revisión. Cualquier `Rejected` termina en `Failed(ReviewRejected)`; agotar Rondas sin unanimidad termina en `Failed(ReviewRoundsExhausted)`.
- **PR con traza completa:** Con aprobación unánime, la Ejecución entra en `Deciding`, el módulo GitHub crea un único commit sobre `BaseSha`, hace push a la rama `codenetix/<runId>` y abre un PR etiquetado `codenetix` incluyendo enlace al Evento, Spec, resumen de Veredictos con hallazgos, `TestReport`, clase de riesgo y coste (cuando exista).
- **Guardián Git:** Un único guardián en el módulo GitHub rechaza y registra cualquier intento de push a la rama por defecto o de merge automático.
- **Clasificación de riesgo pura:** `MergePolicy.Classify(PolicyInput) → RiskClass` (`Minor` o `NeedsAttention`) es una función pura y determinista sin E/S, reloj, aleatoriedad ni LLM. En la v1 el destino es siempre abrir PR, marcando en título y cuerpo las rutas implicadas cuando es `NeedsAttention`.
- **Notificación idempotente:** Al llegar a estado terminal (`PullRequestCreated`, `Failed`, `Cancelled`) en una Ejecución originada en GitHub, se publica un único comentario idempotente por `(runId, kind)` registrado en `gh_notifications`.

## Technical Decisions

- **Fronteras de módulos (AD-1, AD-8):** `CodeNetix.GitHub` es el único dueño de operaciones git y GitHub (sincronizar árbol desde el Sandbox, `ComputeDiff` contra `BaseSha`, commit único, push a `codenetix/<runId>`, abrir PR, comentar, `gh_pull_requests`, `gh_notifications`). `CodeNetix.Agents` implementa los Revisores sobre `IChatClient` + `SchemaRetryDecorator`. `CodeNetix.Pipeline` es dueño de `run_stages`, `run_verdicts`, `MergePolicy` y la orquestación (AD-16).
- **Etapas y persistencia (AD-11):** `run_stages` registra `(stageId, runId, kind ∈ {Analysis, Coding, Testing, Review, Decision}, round, reviewer?)` con una Etapa `Review` propia por Revisor y Ronda. `run_verdicts` persiste los veredictos como fuente de verdad; los `RunEvent` son su proyección observable.
- **Contratos cerrados (AD-5):** `ReviewInput` expone exclusivamente `Spec`, `Diff`, `TestReport`, `ReviewerKind`, `Round` y configuración de modelo/rutas protegidas; el diseño de tipos impide filtrar la salida del Programador.

## Cross-Story Dependencies

- **2.4 → 3.1:** La 3.1 sustituye el puente provisional `"Revisión no disponible"` tras un `TestReport` válido por `ComputeDiff` + transición a `Reviewing` + ejecución paralela de Revisores.
- **3.1 → 3.2:** La 3.2 toma los Veredictos persistidos en `run_verdicts` para aplicar la lógica de Rondas (`Reviewing → Coding`), `ReviewRejected` y `ReviewRoundsExhausted`.
- **3.2 → 3.3 + 3.4:** La 3.3 crea el commit único y el PR ante unanimidad; la 3.4 añade `MergePolicy.Classify` y enriquece título/cuerpo del PR con `NeedsAttention` y rutas implicadas.
- **3.3 → 3.5:** La 3.5 unifica la notificación final en la issue de origen para todos los estados terminales (reutilizando `IIssueNotifier` y `gh_notifications` de la 1.9).
