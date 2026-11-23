---
title: 'Historia 3.4: Clasificación de riesgo determinista'
type: 'feature'
created: '2026-11-21'
status: 'done'
route: 'dispatch'
baseline_commit: 'dcf1e0da7a5ad074cb2a1275a5856906b7b4d5e1'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-3-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Los Pull Requests abiertos por CodeNetix todavía no distinguen entre cambios de bajo riesgo y cambios sensibles (que tocan rutas protegidas, tienen un diff grande o requirieron varias rondas de revisión), dificultando que el tech lead priorice su revisión manual.

**Approach:** Implementar `MergePolicy.Classify(PolicyInput)` en `CodeNetix.Pipeline` como una función pura y determinista sin E/S, reloj, aleatoriedad ni LLM (AD-9, NFR-6) que clasifica cada cambio como `Minor` o `NeedsAttention` (siempre abriendo PR en la v1), integrarla en `RunOrchestrator` al entrar en `Deciding` emitiendo `RiskClassified`, y reflejar la clasificación y las rutas protegidas implicadas en el título y el cuerpo del PR (FR-18).

## Boundaries & Constraints

**Always:**
- **Función pura y determinista (`MergePolicy.Classify`, AD-9, NFR-6):**
  - Vive en `CodeNetix.Pipeline` (`MergePolicy`), sin E/S, sin reloj (`DateTime`/`TimeProvider`), sin `Random`, sin red y sin LLM: la misma entrada produce siempre la misma clasificación y los mismos motivos.
  - `PolicyInput`: `ChangedFiles` (`IReadOnlyList<string>`), `DiffLines` (`int`), `ProtectedPaths` (`IReadOnlyList<string>`), `Verdicts` (`IReadOnlyList<VerdictDecision>`), `Round` (`int`), `MaxMinorDiffLines` (`int`, por defecto `400`).
  - `PolicyResult`: `RiskClass` (`Minor` | `NeedsAttention`), `Action` (`PolicyAction.CreatePullRequest` siempre en la v1), `MatchedProtectedPaths` (`IReadOnlyList<string>` ordenadas y sin duplicados) y `Reasons` (`IReadOnlyList<string>`).
- **Reglas de clasificación (FR-18):**
  - Es `NeedsAttention` si se cumple **cualquiera** de las siguientes condiciones:
    1. Alguna ruta en `ChangedFiles` coincide con algún patrón glob de `ProtectedPaths` (soportando `**`, `*`, `?` y normalizando `/` y `\`, p. ej. `**/Migrations/**`, `**/*.csproj`, `.github/**`).
    2. `DiffLines > MaxMinorDiffLines` (umbral por defecto: `400` líneas cambiadas, contando líneas `+` y `-` del diff unificado sin cabeceras `+++`/`---`).
    3. `Round != 1` o no todos los `Verdicts` son `Approved` (o la lista de veredictos está vacía): no hubo unanimidad en la primera Ronda.
  - Es `Minor` únicamente cuando **no** toca ninguna ruta protegida, `DiffLines <= MaxMinorDiffLines`, `Round == 1` y todos los veredictos son `Approved`.
- **Integración en PR y observabilidad (FR-18):**
  - En `RunOrchestrator`, antes de publicar el PR en `Deciding`, se invoca `MergePolicy.Classify` con los archivos cambiados y el conteo de líneas del `DiffResult` de la ronda aprobada, las `ProtectedPaths` congeladas en `EffectiveConfig` (AD-10), los veredictos de esa ronda y el número de ronda.
  - Se emite `RiskClassified(RiskClass, DiffLines, MatchedProtectedPaths)` en `RunEvent` (con `stageId` de la etapa `Decision`).
  - Cuando es `NeedsAttention`, el título del PR lleva el prefijo `[Requiere atención] ` y el cuerpo del PR incluye una sección `## Clasificación de riesgo` indicando `NeedsAttention`, los motivos concretos y las rutas protegidas implicadas. Cuando es `Minor`, mantiene el título estándar e indica `Minor` en el cuerpo.
  - En la v1 el destino es **siempre** abrir un PR, sea `Minor` o `NeedsAttention`.

**Never:**
- No usar LLM, sistema de archivos, red, reloj ni aleatoriedad dentro de `MergePolicy` ni en el matcher de globs.
- No hacer auto-merge para cambios `Minor` en la v1.
- No leer `ProtectedPaths` del árbol de trabajo modificado (usar siempre `EffectiveConfig.ProtectedPaths` congelada desde `BaseSha`, AD-10).
- No requerir migraciones EF Core.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Cambio menor limpio | `src/App.cs`, `DiffLines = 40`, sin rutas protegidas, `Round = 1`, 3 `Approved` | `RiskClass.Minor`, `MatchedProtectedPaths = []`, `Action = CreatePullRequest`; título sin prefijo, cuerpo indica `Minor` | N/A |
| Ruta protegida por defecto (`.csproj` / `Migrations` / `.github`) | `ChangedFiles` incluye `src/App/App.csproj` o `src/Data/Migrations/001.cs` o `.github/workflows/ci.yml` | `RiskClass.NeedsAttention`, `MatchedProtectedPaths` contiene esas rutas; título con `[Requiere atención]`, cuerpo lista rutas implicadas | N/A |
| Ruta protegida personalizada de `codenetix.yml` | `ProtectedPaths` incluye `src/Security/**`, diff toca `src/Security/Auth.cs` | `RiskClass.NeedsAttention`, `MatchedProtectedPaths = ["src/Security/Auth.cs"]` | N/A |
| Umbral de líneas del diff (borde exacto) | `DiffLines = 400` vs `DiffLines = 401` (`MaxMinorDiffLines = 400`) | `400` → `Minor`; `401` → `NeedsAttention` con motivo de tamaño del diff | N/A |
| Unanimidad en Ronda 2 (no en primera ronda) | `Round = 2`, 3 `Approved`, sin rutas protegidas, `DiffLines = 20` | `RiskClass.NeedsAttention` con motivo de no unanimidad en Ronda 1 | N/A |
| Múltiples factores combinados | Ruta protegida + `DiffLines = 500` + `Round = 2` | `RiskClass.NeedsAttention` listando todos los motivos y rutas implicadas; abre PR igual | N/A |
| Determinismo repetible | Misma `PolicyInput` invocada múltiples veces | Devuelve exactamente el mismo `PolicyResult` | `ArgumentNullException` si `input` es nulo |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/MergePolicy.cs` (nuevo) y `Runs.cs` -- define `RiskClass` (`Minor`, `NeedsAttention`), `PolicyAction` (`CreatePullRequest`), `PolicyInput`, `PolicyResult` y evento polimórfico `RiskClassified`.
- `src/CodeNetix.Pipeline/Features/DecideMerge/MergePolicy.cs` (nuevo) y `GlobMatcher.cs` (nuevo) -- implementación pura y determinista de `MergePolicy.Classify(PolicyInput)` + `MergePolicy.CountDiffLines(string unifiedDiff)` + matcher glob sin E/S (`**`, `*`, `?`).
- `src/CodeNetix.Pipeline/Features/RunPipeline/PullRequestBodyBuilder.cs` y `RunOrchestrator.cs` -- pasa `PolicyResult` a `BuildTitle` (prefijo `[Requiere atención] ` en `NeedsAttention`) y `BuildBody` (sección `## Clasificación de riesgo` con motivos y rutas implicadas), y emite `RiskClassified` en `Deciding`.
- `src/CodeNetix.Host/FixMode.cs` -- formatea `RiskClassified` en `codenetix fix --follow`.
- `tests/CodeNetix.Host.Tests/MergePolicyTests.cs` (nuevo) -- tests de tabla (`[Theory]`) para `MergePolicy.Classify`, `GlobMatcher`, `CountDiffLines`, determinismo y pruebas E2E en `RunOrchestrator` verificando título, cuerpo y evento `RiskClassified` para `Minor` y `NeedsAttention`.

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Contracts/MergePolicy.cs` y `Runs.cs` -- definir `RiskClass`, `PolicyAction`, `PolicyInput`, `PolicyResult` y el evento `RiskClassified` -- contratos puros compartidos (AD-1, AD-9).
- [x] `src/CodeNetix.Pipeline/Features/DecideMerge/MergePolicy.cs` y `GlobMatcher.cs` -- implementar `MergePolicy.Classify` y `CountDiffLines` sin E/S, reloj, aleatoriedad ni LLM -- cumple AD-9, FR-18 y NFR-6.
- [x] `src/CodeNetix.Pipeline/Features/RunPipeline/PullRequestBodyBuilder.cs`, `RunOrchestrator.cs` y `src/CodeNetix.Host/FixMode.cs` -- evaluar `MergePolicy.Classify` en `Deciding`, emitir `RiskClassified` y reflejar `NeedsAttention` + rutas implicadas en título y cuerpo del PR -- cumple FR-18 y AD-16.
- [x] `tests/CodeNetix.Host.Tests/MergePolicyTests.cs` y ajustes de tests existentes (secuencia de eventos en `FixFollowTests` / `RunExecutionTests` si aplica) -- tests de tabla cubriendo las 7 filas de la matriz I/O y pruebas E2E de PR `Minor` vs `NeedsAttention`.

**Acceptance Criteria:**
- Given un `PolicyInput` (rutas cambiadas, líneas del diff, Rutas protegidas, Veredictos, Ronda), when se llama a `MergePolicy.Classify`, then devuelve `Minor` o `NeedsAttention` de forma pura y determinista sin E/S, reloj, aleatoriedad ni LLM, verificado con tests de tabla sobre rutas protegidas, umbral del diff y unanimidad en Ronda 1.
- Given un cambio clasificado como `NeedsAttention`, when se crea el PR, then el título lleva `[Requiere atención]` y el cuerpo indica la clasificación, los motivos y las rutas protegidas implicadas, y el destino en la v1 es siempre un PR.

## Implementation Notes

- **Contratos:** Creado [MergePolicy.cs](../../src/CodeNetix.Contracts/MergePolicy.cs) (`RiskClass`, `PolicyAction`, `PolicyInput`, `PolicyResult`) y evento `RiskClassified` en [Runs.cs](../../src/CodeNetix.Contracts/Runs.cs).
- **Política pura (`CodeNetix.Pipeline`):** Creados [GlobMatcher.cs](../../src/CodeNetix.Pipeline/Features/DecideMerge/GlobMatcher.cs) (DP por segmentos sin regex ni E/S) y [MergePolicy.cs](../../src/CodeNetix.Pipeline/Features/DecideMerge/MergePolicy.cs) (`Classify` y `CountDiffLines`).
- **Orquestación, PR y CLI:** Integrado en [RunOrchestrator.cs](../../src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs), [PullRequestBodyBuilder.cs](../../src/CodeNetix.Pipeline/Features/RunPipeline/PullRequestBodyBuilder.cs) y [FixMode.cs](../../src/CodeNetix.Host/FixMode.cs).
- **Verificación:** Creado [MergePolicyTests.cs](../../tests/CodeNetix.Host.Tests/MergePolicyTests.cs) con tests de tabla y E2E; `dotnet test --nologo` pasó **885/885 pruebas, 0 errores, 0 omitidas**.

## Spec Change Log

## Review Triage Log

| ID | Capa | Veredicto | Ruta | Evidencia / Justificación |
|---|---|---|---|---|
| BH-1 / EC-2 | blind-hunter, edge-case-hunter | `medium` | patch | `CountDiffLines` omitía líneas dentro de hunks que empezaban por `-- ` o `++ ` (comentarios SQL/Markdown). Se rastrea `inHunk` mediante cabeceras `@@ ` y `diff --git `. |
| BH-2 / EC-1 | blind-hunter, edge-case-hunter | `high` | patch | `GlobMatcher.NormalizePath` no resolvía `/./` ni segmentos `.` / `..` interiores. Se normalizan los segmentos con una pila determinista. |
| BH-3 | blind-hunter | `high` | patch | Renombrar un archivo protegido en `git diff --name-only` sin `--no-renames` solo reportaba la ruta nueva. Se añade `--no-renames` en `RepositoryWorkspace.ComputeDiffAsync`. |
| BH-4 / EC-4 | blind-hunter, edge-case-hunter | `medium` | patch | `RunOrchestrator.PublishPullRequestAsync` pasaba `config.ProtectedPaths` sin comprobar `IsDefault`. Se añade guarda hacia `EffectiveConfig.DefaultProtectedPaths`. |
| BH-5 / EC-3 | blind-hunter, edge-case-hunter | `medium` | patch | `MergePolicy.Classify` validará `ThrowIfNegative` sobre `DiffLines` y `MaxMinorDiffLines`. |
| BH-6 | blind-hunter | `low` | patch | Emitir motivos independientes cuando `Round != 1` coincide con veredictos vacíos o no unánimes. |
| BH-7 / EC-5 | blind-hunter, edge-case-hunter | `low` | patch | En `PullRequestBodyBuilder`, comprobar listas nulas (`is { Count: > 0 }`), rama explícita `RiskClass.Minor` y acotar la lista de rutas protegidas mostradas a 50. |
| BH-8 | blind-hunter | `low` | defer | La spec fijó `IReadOnlyList<string>` en `PolicyResult`; se mantiene el contrato público sin cambios innecesarios. |
| BH-9 / BH-10 / VG-1 | blind-hunter, verification-gap | `medium` | patch | Añadidos tests en `FixFollowTests.cs` (`RiskClassified` con `matchedProtectedPaths`) y `MergePolicyTests.cs` (AD-10 modificando `codenetix.yml` en sandbox, `--no-renames` en archivo protegido renombrado, `CountDiffLines` con `-- ` en hunk y `MaxMinorDiffLines` personalizado). |
