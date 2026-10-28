---
title: 'Historia 1.9: Rechazo temprano con aviso en la issue'
type: 'feature'
created: '2026-10-27'
status: 'done'
route: 'dispatch'
baseline_commit: '288794c6a394748838d1e07264967122cc113cc9'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-1-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Si la issue no es automatizable (falta información, el cambio es demasiado grande o queda fuera de .NET), el Analista solo puede devolver una Spec o fallar por esquema. El autor no recibe aviso y espera un PR que nunca llegará (FR-10, AD-8).

**Approach:**
- La salida del Analista pasa a ser `spec` o `not_automatable` (con una categoría y una explicación).
- El orquestador aplica `Fail(NotAutomatable)` sin programar nada.
- Tras el commit, el módulo GitHub publica un único comentario en la issue, idempotente por `(runId, kind)` en `gh_notifications`.

## Boundaries & Constraints

**Always:**
- **Salida del Analista (`schemas/analysis.v1.json`):** `oneOf` con dos formas.
  - `{ "outcome": "spec", "spec": <Spec v1> }`.
  - `{ "outcome": "not_automatable", "category": "missing_information" | "too_large" | "out_of_scope", "explanation": <1–1000 caracteres> }`.

  Se mantienen el mismo decorador de reintentos y la misma validación.
  - `AnalysisResult` añade `NotAutomatable(category, explanation, attempts, model)`.
  - El *system prompt* explica cuándo rechazar.
- **Orquestador:**
  - `NotAutomatable` → `Fail(NotAutomatable, "<categoría>: <explicación>")`, en la misma transacción que un `RunEvent` `AnalysisRejected { category, attempts, model }`. No se persiste ninguna Spec ni se llama a `StartCoding`.
  - Después del commit se llama a `IIssueNotifier.NotifyAsync(runId, owner, repo, number, NotificationKind.NotAutomatable, body)`.
  - Su resultado se registra con el sumidero externo como `IssueCommented { kind, commentUrl }` o `NotificationFailed { kind, error }`.
  - Un fallo al notificar no cambia el Estado terminal.
- **Módulo GitHub:**
  - `GitHubDbContext` (prefijo `gh`, historial `__ef_migrations_gh`, `Order` después de `run`).
  - Tabla `gh_notifications`: `id`, `run_id`, `kind`, `status` (`Pending`, `Posted` o `Failed`), `comment_id`, `comment_url`, `error`, `created_at` y `updated_at` (ticks UTC), con UNIQUE (`run_id`, `kind`).
  - `NotifyAsync`:
    1. Inserta `Pending`. Si ya existe en `Posted`, devuelve el comentario existente sin publicar.
    2. Busca en los comentarios de la issue el marcador oculto `<!-- codenetix:run=<runId>;kind=<kind> -->`, como mucho en las 3 últimas páginas de 100.
    3. Si lo encuentra, lo adopta y marca `Posted`.
    4. Si no, publica con `POST /repos/{o}/{r}/issues/{n}/comments` y marca `Posted`, o `Failed` con el error redactado.
  - Una segunda llamada concurrente con la misma clave ve la violación UNIQUE, relee la fila y no publica.
- **Cuerpo del comentario (en español):**
  - Título "CodeNetix no puede automatizar esta issue".
  - Categoría traducida: "falta información", "cambio demasiado grande" o "fuera del alcance (.NET)".
  - La explicación del modelo saneada:
    - sin HTML;
    - `@` → `@​` (con espacio de ancho cero), para no mencionar;
    - enlaces convertidos en texto;
    - como mucho 1000 caracteres.
  - El `shortId` de la Ejecución y el marcador oculto.

**Never:**
- Publicar dos comentarios para el mismo `(runId, kind)`.
- Publicar antes del commit de la transición.
- Texto de la issue citado en el comentario.
- El token en la BD, los `RunEvent` o los logs.
- Etiquetas, cerrar la issue u otras notificaciones (Épica 3+).
- Reintentos en segundo plano de las notificaciones fallidas (reconciliación 5.3).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| No automatizable | El modelo devuelve `not_automatable/too_large` | `Failed(NotAutomatable)`, `AnalysisRejected`, un comentario e `IssueCommented` | — |
| Automatizable | `outcome: spec` | Igual que en la 1.8 (`run_specs`, `Failed(StageError, "Programación no disponible")`) | Sin comentario |
| Doble publicación | `NotifyAsync` dos veces (en serie y en paralelo) | Un solo `POST` y una fila `Posted` | — |
| Caída tras publicar | Comentario publicado y fila en `Pending` | El reintento encuentra el marcador y no vuelve a publicar | — |
| GitHub falla | El `POST` devuelve 403 o 5xx | Fila `Failed` y `NotificationFailed`; el Estado sigue en `Failed(NotAutomatable)` | Error redactado |
| Mención o HTML | La explicación contiene `@admin <img src=x>` | El comentario no menciona a nadie ni lleva HTML | — |
| Salida inválida | `outcome: not_automatable` sin `category` | Cuenta como inválida (reintento) | `SchemaInvalid` si se agotan |

</frozen-after-approval>

## Code Map

- `schemas/spec.v1.json` y `src/CodeNetix.Contracts/Spec.cs` y `Analysis.cs` (1.8) -- `AnalysisResult`; añadir `analysis.v1.json` como envoltorio que referencia la Spec.
- `src/CodeNetix.Agents/` -- el Analista, el decorador de reintentos y el *system prompt* (1.8).
- `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` -- el paso de análisis (1.8).
- `src/CodeNetix.GitHub/` -- `GitHubApiClient` e `IssueReader`; añadir `GitHubDbContext` (patrón `IModuleDatabase` de Ingestion y Pipeline) e `IssueNotifier`.
- `src/CodeNetix.Contracts/Runs.cs` -- unión `RunEvent`.
- `tests/CodeNetix.Host.Tests/` -- `FakeGitHub` (añadir comentarios: listar y crear) y el cliente de chat falso con guion.

## Tasks & Acceptance

**Execution:**
- [x] `schemas/analysis.v1.json` y `src/CodeNetix.Contracts/` -- el envoltorio de análisis, `NotAutomatable`, `IIssueNotifier`, `NotificationKind` y los nuevos `RunEvent`.
- [x] `src/CodeNetix.Agents/` -- el envoltorio en la petición y la validación, y el prompt de rechazo.
- [x] `src/CodeNetix.GitHub/` -- `GitHubDbContext` con su migración, `IssueNotifier` y el saneado del cuerpo.
- [x] `src/CodeNetix.Pipeline/` -- la rama `NotAutomatable` y la notificación tras el commit.
- [x] `tests/` -- un test por fila de la matriz y tests de tabla del saneado.

**Acceptance Criteria:**
- Given que el Analista marca la issue como no automatizable, when el orquestador aplica el resultado, then la Ejecución queda en `Failed(NotAutomatable)` sin Spec ni programación, y la issue tiene exactamente un comentario con el motivo.
- Given que el mismo comentario se intenta publicar dos veces, when GitHub comprueba `gh_notifications`, then solo existe un comentario.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde.

## Implementation Notes

- **Envoltorio y `$ref`:** `analysis.v1.json` referencia la Spec con `"$ref": "spec.v1.json"` (resuelto contra su `$id`). `StructuredOutputValidator` acepta esquemas referenciados: los registra en un `SchemaRegistry` local (sin red) y, para el Proveedor, envía un documento autocontenido (la Spec en `$defs/spec_v1`, `$ref` reescrito, sin `$schema`/`$id`). El prompt sin salida estructurada usa ese mismo esquema empaquetado. La raíz mantiene `"type": "object"` además del `oneOf`.
- **Errores del `oneOf`:** se descartan las ramas cuyo `const` (`outcome`) no coincide; si ninguna coincide, solo se informa `$.outcome: valor no admitido`. `const`/`enum` → "valor no admitido". Se mantiene todo el saneado de 1.8 (rutas `[A-Za-z0-9_]`, propiedades extra citadas por el padre, límites de longitud). Los errores de `SpecRules` llevan el prefijo `spec.` (p. ej. `spec.affectedFiles[0] …`). Explicación solo de espacios → inválida (`explanation está vacío`).
- **Categorías:** `NotAutomatableCategory` en Contracts, serializada en snake_case (`too_large`) en el `RunEvent` y en el detalle (`too_large: <explicación>`, truncado a 1000).
- **Cuerpo del comentario:** lo compone Pipeline (`NotAutomatableComment`: título, categoría traducida, explicación en cita recortada a 1000, `shortId`); el módulo GitHub trata el cuerpo entero como no confiable y lo sanea (`IssueCommentSanitizer`: sin control, sin comentarios/etiquetas HTML y `<` restante escapado, enlaces e imágenes → texto, definiciones de referencia eliminadas, `//` y `www.` rotos con U+200B (ver parches 1–4), `@` → `@`+U+200B, máx. 4000) y después añade el marcador. La plantilla sobrevive intacta al saneado (test). Los U+200B insertados pueden hacer que la explicación supere 1000 caracteres "en bruto", nunca en texto visible.
- **Idempotencia:** UNIQUE (`run_id`, `kind`) + un candado por clave dentro del proceso: la llamada concurrente espera, choca con UNIQUE, relee la fila `Posted` y no publica. Una fila existente en `Pending` (caída) o `Failed` (llamada explícita posterior) busca el marcador y, si no está, publica. El marcador se busca siempre (también con fila nueva), en las 3 últimas páginas. (Ver parches 5–7: páginas por `Link` y autor = bot.)
- **`GitHubApiClient.PostAsync`:** mismo tratamiento de redirecciones (mismo método y cuerpo), errores y rate limit que `GetAsync`; sin reintentos.
- **Orquestador:** `AnalysisRejected` + `Failed(NotAutomatable)` en el mismo `SaveChanges`; después `IIssueNotifier` y `IssueCommented`/`NotificationFailed` vía `IRunEventSink`. Cualquier excepción no de cancelación del notificador se registra como `NotificationFailed`; una cancelación deja la fila en `Pending` (la reconciliará la 5.3).
- **Tests:** `FakeChatClient` responde por defecto el envoltorio (`ValidAnalysisJson`); los tests del decorador genérico siguen usando la Spec desnuda (`new FakeChatClient(ValidSpecJson)`). `FakeGitHub` añade listar/crear comentarios y `comments` en la issue. Nueva clase `EarlyRejectionTests` (46 casos: matriz, sanitizer, cuerpo, esquema).
- **Revisión (parches):**
  1. Entidades HTML: tras quitar etiquetas, `&` → `&amp;` (antes que `<` → `&lt;`); `&#64;`, `&#x40;`, `&commat;` y `https&#58;//` quedan como texto literal.
  2. Enlaces residuales (corchetes anidados, texto multilínea): tras las regex de enlaces, todo `[`, `]` y `!` restante se escapa con `\`. `//` se rompe siempre (`/`+U+200B+`/`), lo que cubre `https://` y las URL relativas al protocolo (`//host`); sustituye a la regla anterior de `://`.
  3. `NotAutomatableComment.Build` normaliza `\r\n` y `\r` sueltos a `\n` antes de partir líneas: todas quedan dentro de la cita `> `.
  4. Referencias: U+200B tras `#` seguido de dígito (`#123`, `owner/repo#5`; también afecta a `&amp;#64;`, inocuo) y tras `GH-` seguido de dígito (sin distinguir mayúsculas). `owner/repo@sha` ya queda roto por la regla de `@`.
  5+6. Búsqueda del marcador: ya no se usa el contador `comments` de la issue. Se pide la página 1 (`per_page=100`), se lee `Link: rel="last"` (`GitHubResponse.Link`; sin él, la 1 es la última) y se recorren last, last-1 y last-2 (la página 1 solo se examina si está entre ellas). `FakeGitHub` emite `Link` como GitHub (solo con más de una página) y registra las páginas pedidas.
  7. Solo se adopta un comentario con marcador cuyo `user.login` coincide (sin distinguir mayúsculas) con `IGitHubIdentity`; la identidad se consulta solo si aparece un marcador. Un marcador falsificado por otro usuario se ignora y se publica el comentario real.
  8. `TextLimits.Truncate` (Contracts) recorta sin partir pares sustitutos; lo usan `NotAutomatableComment`, el sanitizer, `RunOrchestrator.Truncate` y `IssueNotifier.MarkFailedAsync`.
  12. Tests: nuevas filas de `SanitizerCases` (entidades, `[[a]](//…)`, `![[a]](//…)`, enlace multilínea, `#123`, `org/repo#5`, `GH-7`, `@sha`, `###` intacto, `!`), `Build` con `\r` suelto, recorte con emoji, `LastPage` y búsqueda con 450 comentarios (marcador en #430 → 0 POST, páginas 1 y 5; marcador en #50 → no se busca, 1 POST, páginas 1, 5, 4 y 3), marcador falsificado y login en mayúsculas.
- **Riesgo:** Proveedores con *strict* JSON schema (OpenAI `strict: true`) no admiten `oneOf` en la raíz; hoy no se activa `strict`, y sin salida estructurada se usa la alternativa en el prompt.

## Spec Change Log

## Review Triage Log

Revisión 1 (revisor combinado, 12 hallazgos):

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | high | Menciones vía entidades HTML (`&#64;admin`) | patch |
| 2 | high | Enlaces/imágenes con corchetes anidados, saltos de línea o `//host` | patch |
| 3 | medium | `\r` suelto escapa del bloque de cita y falsifica la plantilla | patch |
| 4 | medium | Referencias `#123`, `owner/repo#5`, `GH-7` sin neutralizar | patch |
| 5 | medium | Cálculo de "últimas 3 páginas" sin test real (152 comentarios = 2 páginas) | patch |
| 6 | medium | Recuento de comentarios desactualizado → duplicado | patch (`Link: rel="last"` o página extra) |
| 7 | medium | Marcador falsificable por cualquier autor | patch (solo `IGitHubIdentity`) |
| 8 | low | Truncado que parte un par sustituto | patch (pequeño) |
| 9 | low | 201 con cuerpo ilegible → `Failed` aunque exista | reject |
| 10 | low | Cancelación deja la fila `Pending` sin evento | defer (5.3) |
| 11 | low | Ramas de fallo sin test | reject |
| 12 | low | Tests del saneado solo de casos simples | patch (junto a 1–4) |
