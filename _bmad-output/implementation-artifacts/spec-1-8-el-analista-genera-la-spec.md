---
title: 'Historia 1.8: El Analista genera la Spec'
type: 'feature'
created: '2026-10-27'
status: 'done'
route: 'dispatch'
baseline_commit: '20dcd8976b27f872021431c00205706840f87f25'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-1-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** La Ejecución llega a `Analyzing` y falla. Nadie convierte la issue en una `Spec` técnica, así que no hay nada que programar (FR-9, AD-5, AD-16 paso 3).

**Approach:**
- GitHub lee la issue.
- Agents añade el Analista (`IReasoningAgent` sobre `IChatClient` de Microsoft.Extensions.AI), con salida estructurada validada contra el esquema `Spec` de Contracts y un decorador común de reintentos (con alternativa de JSON en el prompt).
- Pipeline persiste la Spec en `run_specs`, la expone en `GET /api/runs/{id}` y, mientras no exista el Programador, cierra la Ejecución con `Failed(StageError, "Programación no disponible")`.

## Boundaries & Constraints

**Always:**
- **Contracts:**
  - `Spec` (record inmutable) con estos campos:
    - `problem`, `probableCause` y `proposedChange` (texto no vacío);
    - `acceptanceCriteria` (array de 1 a 20 cadenas no vacías);
    - `affectedFiles` (array de 0 a 50 rutas relativas, sin `..` ni rutas absolutas).
  - Su esquema JSON está en `schemas/spec.v1.json` (2020-12, `additionalProperties: false`), incrustado y usado tanto en la petición estructurada como en la validación.
  - `IReasoningAgent<TInput, TOutput>` (o un `IAnalyst.AnalyzeAsync(AnalysisInput, ct) → AnalysisResult`).
  - `AnalysisInput` contiene: `IssueTitle` y `IssueBody` como `UntrustedText`, la lista de archivos del repositorio (como mucho 2000 rutas, con indicación de truncado), `EffectiveConfig` y `RunId`.
  - `AnalysisResult` es `Ok(Spec, attempts, model)` o `SchemaInvalid(lastError, attempts)` o `ProviderError(detail)`.
  - `IIssueReader.GetAsync(owner, repo, number)` → `{ Title, Body }` (GitHub).
- **Proveedores (Host, configuración de instancia):**
  - Todos se tratan como compatibles con OpenAI (`Microsoft.Extensions.AI.OpenAI`).
  - `CODENETIX__Providers__<Nombre>__BaseUrl`: por defecto `https://api.anthropic.com/v1/` para `Anthropic` y `https://api.openai.com/v1/` para `OpenAI`; obligatorio para cualquier otro nombre.
  - `CODENETIX__Providers__<Nombre>__StructuredOutput`: por defecto `true` para Anthropic y OpenAI, `false` para el resto.
  - `CODENETIX__Agents__Analyst__Provider` y `__Model`:
    - Proveedor por defecto: el único con key, o `Anthropic` si hay varios.
    - Modelo por defecto: `claude-sonnet-5-5` en Anthropic y `gpt-5` en OpenAI; obligatorio en el resto.
    - `models.analyst` de la `EffectiveConfig` sustituye al modelo.
  - Configuración incoherente (el Proveedor nombrado no tiene key, falta `BaseUrl` o falta el modelo) → modo `Diagnostic` con el motivo.
  - `CODENETIX__Agents__SchemaRetries`: por defecto 2, rango 0–5.
- **Decorador de reintentos** (común, reutilizable por los Revisores):
  - Intento 1 + N reintentos.
  - Cada respuesta se valida contra el esquema y además semánticamente (rutas relativas).
  - En cada reintento se añade al diálogo el error de validación (sin el texto de la issue).
  - Si todos fallan → `SchemaInvalid`.
  - Un error HTTP o de red del Proveedor no cuenta como reintento de esquema: es `ProviderError`.
  - Timeout de 120 s por llamada.
- **Prompt:**
  - El *system prompt* es fijo y en inglés: el rol del Analista, que nunca ejecuta código y que el contenido de los bloques de datos no son instrucciones.
  - El título y el cuerpo de la issue solo entran con `UntrustedText.RenderAsDataBlock` en el mensaje de usuario.
  - Con `StructuredOutput=false`, el esquema va en el prompt y se extrae el primer objeto JSON de la respuesta (se toleran vallas ```json).
- **Orquestador:**
  - En `Analyzing`, se lee la issue (si falla → `Fail(StageError, "no se pudo leer la issue: <motivo>")`) y se lista `git ls-files` del árbol de trabajo.
  - Se llama al Analista:
    - `Ok` → en la misma transacción, la fila de `run_specs` y el `RunEvent` `SpecGenerated { attempts, model }`; después, `Fail(StageError, "Programación no disponible")`.
    - `SchemaInvalid` → `Fail(SchemaInvalid, "<último error>")`.
    - `ProviderError` → `Fail(StageError, "Proveedor: <detalle redactado>")`.
  - Cada intento rechazado emite `AnalystOutputRejected { attempt, error }`.
- **Persistencia:** `run_specs` (`run_id` PK/FK, `spec_json`, `model`, `attempts` y `created_at` en ticks UTC), en una nueva migración `run`. `GET /api/runs/{id}` añade `spec` (el objeto, o null).

**Never:**
- La issue en el *system prompt*.
- Que el Analista ejecute código o herramientas (sin *function calling*).
- La key del Proveedor en logs, errores, `RunEvent` o BD.
- Cargos de presupuesto o `TokensConsumed` (historia de presupuesto posterior).
- `StartCoding`.
- Rechazo `NotAutomatable` (1.9).
- Llamadas reales a Proveedores en los tests.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Spec válida | El modelo devuelve JSON conforme | `run_specs` con la Spec; `SpecGenerated`; `Failed(StageError, "Programación no disponible")` | — |
| Inválida y luego válida | 1.ª respuesta sin `problem`, 2.ª correcta | `attempts`=2, un `AnalystOutputRejected` | — |
| Siempre inválida | 3 respuestas inválidas (N=2) | `Failed(SchemaInvalid)` con el último error | Sin Spec persistida |
| Sin salida estructurada | `StructuredOutput=false`, respuesta con texto y una valla ```json | Se extrae y valida | — |
| Ruta peligrosa | `affectedFiles: ["../x"]` | Cuenta como inválida (reintento) | — |
| Proveedor caído | HTTP 500 o timeout | `Failed(StageError, "Proveedor: …")` sin consumir reintentos | Key redactada |
| Inyección | Issue con "ignore previous instructions" | Solo aparece dentro del bloque de datos con nonce | — |
| Issue ilegible | GitHub 404 o 5xx | `Failed(StageError, "no se pudo leer la issue: …")` | — |
| Config incoherente | `Agents__Analyst__Provider=Foo` sin key | `Diagnostic` con el motivo | — |
| API | `GET /api/runs/<id>` tras una Spec | Incluye `spec` | Sin Spec → `spec: null` |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Contracts/UntrustedText.cs` -- `RenderAsDataBlock(label)` (1.5).
- `src/CodeNetix.Contracts/RepositoryConfig.cs` -- `EffectiveConfig`, `AgentModels`, `GitHubObjectKey` (1.7).
- `src/CodeNetix.Agents/` -- proyecto vacío; solo referencia Contracts. Aquí van el Analista, el decorador de reintentos y la fábrica de `IChatClient`.
- `src/CodeNetix.GitHub/GitHubApiClient.cs` -- cliente con redirección y errores (1.5); añadir `IssueReader` (`GET /repos/{o}/{r}/issues/{n}`).
- `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` -- sustituir el paso "Análisis no disponible todavía".
- `src/CodeNetix.Host/Configuration/InstanceOptions.cs` y `InstanceOptionsValidator.cs` -- `ProviderInstanceOptions` (`ApiKey`, `BaseUrl`); añadir `StructuredOutput` y `Agents`.
- `src/CodeNetix.Host/Features/GetRun/` -- añadir `spec`.
- `tests/CodeNetix.Host.Tests/` -- `CodeNetixFactory` (GitHub falso y repositorio local por defecto); añadir un `IChatClient` falso con guion de respuestas y un servidor falso compatible con OpenAI para una prueba del cableado HTTP real.

## Tasks & Acceptance

**Execution:**
- [x] `schemas/spec.v1.json` y `src/CodeNetix.Contracts/` -- `Spec`, contratos del Analista e `IIssueReader`.
- [x] `src/CodeNetix.GitHub/` -- `IssueReader`.
- [x] `src/CodeNetix.Agents/` -- fábrica de `IChatClient` por Proveedor, decorador de reintentos con validación, Analista y prompt.
- [x] `src/CodeNetix.Pipeline/` -- `run_specs` y su migración, nuevos `RunEvent` y el paso del orquestador.
- [x] `src/CodeNetix.Host/` -- opciones `Agents`, `StructuredOutput`, `BaseUrl` por defecto, validación (modo `Diagnostic`), composición y `spec` en la API.
- [x] `tests/` -- un test por fila de la matriz; test del decorador con un cliente falso; un test HTTP contra un servidor falso compatible con OpenAI (con y sin salida estructurada) que verifique que la issue no está en el mensaje de sistema y que la key va solo en `Authorization`.

**Acceptance Criteria:**
- Given una Ejecución con su `EffectiveConfig` y un Proveedor que responde una Spec válida, when entra en `Analyzing`, then la Spec está en `run_specs`, `GET /api/runs/<id>` la devuelve y la Ejecución termina en `Failed(StageError, "Programación no disponible")`.
- Given un modelo que devuelve 3 salidas inválidas, when el decorador agota los 2 reintentos, then la Ejecución termina en `Failed(SchemaInvalid)`.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde.

## Implementation Notes

- **Paquetes:** `Microsoft.Extensions.AI` 10.11.0 y `Microsoft.Extensions.AI.OpenAI` 10.11.0 (la versión que fija la arquitectura; las dos salen de la misma publicación) y `OpenAI` 2.14.0 (la que exige `Microsoft.Extensions.AI.OpenAI` 10.11.0; publicada el 15/09/2026). **Desviación:** 10.11.0 se publicó el 14/10/2026, 13 días antes de hoy: le falta un día para las dos semanas. No hay otra versión estable igual a la de la arquitectura y bajar a 10.10.x rompería la pareja exacta, así que se mantiene 10.11.0. Agents añade además `JsonSchema.Net` 9.4.1 (ya central), `Microsoft.Extensions.Options`, `Logging.Abstractions` y `DependencyInjection.Abstractions`. No se usa Agent Framework.
- **Contracts:**
  - `Spec` (record con `ImmutableArray`).
  - `SpecSchema.Json`: recurso incrustado `CodeNetix.Contracts.spec.v1.json` desde `schemas/spec.v1.json`; un test comprueba que es idéntico al archivo.
  - `SpecRules.Validate` e `IsSafeRelativePath`: texto no vacío, de 1 a 20 criterios y de 0 a 50 rutas relativas. Una ruta no puede empezar por `/`, `\` ni `~`, ni contener `:` (unidad o esquema), segmentos `..` o caracteres de control.
  - `IAnalyst.AnalyzeAsync(AnalysisInput) → AnalysisResult` (`Ok`, `SchemaInvalid` o `ProviderError`, como records anidados). `AnalysisInput.LimitFiles` recorta a 2000 rutas e indica si ha truncado.
  - `IIssueReader.GetAsync → GitHubIssue(Title, Body)`, los dos como `UntrustedText`.
  - Nuevos `RunEvent`: `AnalystOutputRejected { attempt, error }` y `SpecGenerated { attempts, model }`. Nuevo `IRunReader.GetSpecAsync`.
- **Decisión: `IRepositoryWorkspace.ListFilesAsync(runId)`.** El orquestador necesita `git ls-files` y solo el módulo GitHub ejecuta git (AD-8), así que el método se añade al contrato del árbol de trabajo. Ejecuta `git ls-files -z` sin credenciales y con un timeout de 30 s. Si el árbol no existe, la Ejecución acaba en `Failed(StageError, "no se pudo listar el repositorio: …")`.
- **GitHub (`IssueReader`):** `GET /repos/{o}/{r}/issues/{n}` con `GitHubApiClient`, que ya gestiona redirecciones, rate limit y errores.
  - 404/410 → "la issue no existe o el token no puede verla (HTTP 404)".
  - Otros errores → el motivo del cliente más `(HTTP n)`.
  - Un pull request se rechaza, y `body: null` se trata como cuerpo vacío.
- **Agents:**
  - `IAgentChatClientFactory` es pública para que las pruebas la sustituyan. La implementación real, `OpenAICompatibleChatClientFactory`, crea `new OpenAI.Chat.ChatClient(model, ApiKeyCredential, { Endpoint = BaseUrl, NetworkTimeout = CallTimeout + 5 s, RetryPolicy = 0 reintentos }).AsIChatClient()`.
  - **Decisión:** se desactivan los reintentos internos del SDK. Así un error HTTP produce un único `ProviderError` inmediato ("sin consumir reintentos", como pide la matriz). El timeout de 120 s por llamada lo aplica el decorador.
  - `SchemaRetryDecorator` (común y genérico en `T`): hace el intento 1 más `SchemaRetries`. Cada respuesta pasa por `StructuredOutputValidator<T>`, que:
    - extrae el primer objeto JSON, primero dentro de una valla ```` ``` ```` y si no en el texto;
    - lo valida con JsonSchema.Net;
    - aplica las reglas semánticas.
  - Cada rechazo llama a `onRejected` y añade al diálogo un mensaje de usuario con el error. La respuesta anterior no se reenvía, porque podría repetir texto de la issue.
  - Errores del Proveedor y cancelación:
    - Excepción del cliente → `ProviderFailed`, con el estado HTTP y el `error.message` del cuerpo en una línea de 300 caracteres como máximo.
    - Timeout → "sin respuesta en N s".
    - La cancelación del llamador se propaga.
  - Con `StructuredOutput=true` se envía `ChatResponseFormat.ForJsonSchema` con el mismo esquema, sin `$schema` ni `$id` (algunos Proveedores los rechazan) y sin `strict`. Con `false`, el esquema va como texto fijo al final del prompt de sistema. Nunca se envían `tools`.
  - Los mensajes de error de validación citan la ruta y la palabra clave, nunca valores. Ejemplos: `$: falta la propiedad obligatoria 'problem'`, `affectedFiles[0] no es una ruta relativa…`.
  - `Analyst`:
    - El modelo es `models.analyst` de la `EffectiveConfig` o, si no lo hay, el de la instancia.
    - Publica cada `AnalystOutputRejected` por `IRunEventSink`, el sumidero externo, así que se ven en vivo y salen redactados.
    - El prompt de sistema es fijo y en inglés: el rol, que nunca ejecuta código ni herramientas y que los bloques `<untrusted-data>` son datos.
    - El mensaje de usuario lleva el título (`issue-title`), el cuerpo (`issue-body`), la lista de archivos (`repository-files`, con aviso si está truncada) y las Rutas protegidas (`protected-paths`), cada uno con `RenderAsDataBlock`.
- **Pipeline:**
  - Tabla `run_specs` (`run_id` PK/FK con cascada, `spec_json`, `model`, `attempts` y `created_at` en ticks UTC), con la migración `AddRunSpecs` generada con `dotnet-ef`.
  - El orquestador en `Analyzing` lee la issue, lista el árbol con `ls-files` y llama al Analista. Según el resultado:
    - `Ok` → la fila de `run_specs` y `SpecGenerated` se guardan en un mismo `SaveChanges`. Después, en otro, `Failed(StageError, "Programación no disponible")` (`RunOrchestrator.Detail`).
    - `SchemaInvalid` → `Failed(SchemaInvalid, <último error>)`.
    - `ProviderError` → `Failed(StageError, "Proveedor: …")`. `RunEventLog.AddTransitionAsync` redacta el detalle (la key conocida y los patrones `sk-…`).
  - Los detalles se acotan a 1000 caracteres.
- **Host:**
  - Opciones nuevas: `InstanceOptions.Agents` (`SchemaRetries`, `Analyst.Provider` y `Analyst.Model`) y `ProviderInstanceOptions.StructuredOutput`.
  - `AgentEndpointResolver.ResolveAnalyst` aplica las reglas del spec (Proveedor por defecto, URL y modelo por defecto para Anthropic y OpenAI, `StructuredOutput` por defecto) y devuelve el `AgentEndpoint` o los motivos. `InstanceOptionsValidator` añade esos motivos, más `SchemaRetries` fuera de 0–5. `ServeMode` rellena `AgentsOptions` con el mismo resolutor.
  - **Decisión:** si varios Proveedores tienen key y ninguno se llama `Anthropic`, el motivo pide `CODENETIX__Agents__Analyst__Provider`. Una `BaseUrl` que no es una URL http(s) absoluta, o que lleva credenciales o query, pasa a modo `Diagnostic`.
  - `AgentEndpoint.ToString()` no incluye la key.
  - `GET /api/runs/{id}` añade `spec` (el objeto o `null`) antes de `events`.
  - `codenetix fix --follow` imprime `[n] AnalystOutputRejected intento N: …` y `[n] SpecGenerated <modelo> (N intento(s))`.
- **Tests (606 en total tras la implementación; 628 tras los parches de la revisión 1):**
  - Infraestructura: por defecto, `CodeNetixFactory` registra un `FakeChatClientFactory` con un `FakeChatClient` de guion, que sin guion responde una Spec válida. Con `UseRealChatClient` se usa la fábrica real. `FakeGitHub` sirve `GET /repos/{o}/{r}/issues/{n}`.
  - `AnalystTests`:
    - una prueba por fila de la matriz;
    - el modelo del repositorio, la issue sin cuerpo y las reglas por defecto de los Proveedores;
    - dos pruebas HTTP reales contra `FakeOpenAIServer`, con y sin salida estructurada, que comprueban que la issue no está en el mensaje de sistema, que la key solo va en `Authorization` y el `response_format`;
    - un HTTP 500 que da `ProviderError` con una sola petición.
  - `SchemaRetryDecoratorTests`: el decorador con un cliente falso, la extracción de JSON, las rutas seguras y el esquema incrustado.
  - Los tests de 1.6, 1.7 y `--follow` se han actualizado al nuevo recorrido de 7 eventos y a 3 migraciones `run`.
- **Riesgos:**
  1. No se ha probado contra Proveedores reales. El endpoint compatible con OpenAI de Anthropic puede ignorar `response_format`; el decorador y la extracción tolerante lo cubren. Algún Proveedor podría rechazar palabras clave del esquema (`minItems`, `maxItems`); en ese caso basta con `StructuredOutput=false`.
  2. Una Ejecución que el apagado deja en `Analyzing` no se reanuda, porque el Dispatcher solo reanuda `Received`. Se resuelve con la reconciliación de la Épica 5.
  3. Sin los reintentos del SDK, un 429 o un 5xx transitorio del Proveedor hace fallar la Ejecución.

### Parches de la revisión 1

- **(1, MEDIUM-HIGH) Nombres elegidos por el modelo en los errores:** `StructuredOutputValidator` ya no cita el nombre de una propiedad no admitida. Si el error llega en el objeto padre, se cita ese objeto; si llega en la propiedad (el esquema `false` de `additionalProperties`), se quita su segmento. Ejemplo: `$: propiedad no admitida`. Además:
  - Cada segmento de ruta y cada nombre de `required` se sanea a `[A-Za-z0-9_]{1,40}`, `[n]` o `?`.
  - Los mensajes ya no copian el texto de JsonSchema.Net: `type` solo dice el tipo esperado de una lista cerrada, y una palabra clave desconocida se cita saneada.
  - Todo error (también los semánticos) pasa por `Sanitize`, que quita los caracteres de control y acota a 500 caracteres.
- **(2, MEDIUM) Extracción de JSON:** si el texto completo, sin espacios, es un objeto JSON, se usa tal cual; así se acepta un JSON con saltos de línea y ```` ``` ```` dentro de una cadena. Si no, se busca primero en las vallas y después en el texto completo, y solo se acepta un candidato que `JsonDocument.Parse` admite. Un candidato inválido o sin cerrar se descarta y se prueba desde el siguiente `{`. Se prueban como mucho 64 posiciones por texto para que el recorrido no sea cuadrático.
- **(3, MEDIUM) Errores que no son del Proveedor:** en `Analyst`, el `catch` (`ArgumentException`, `InvalidOperationException` o `UriFormatException` → `ProviderError`) envuelve solo `clientFactory.Create(...)`. Un error del sumidero o de EF se propaga, y el Dispatcher cierra la Ejecución como error interno; ya no aparece como "Proveedor: …".
- **(4, LOW) Cancelación envuelta:** antes de convertir una excepción del cliente en `ProviderFailed`, el decorador hace `cancellationToken.ThrowIfCancellationRequested()`.
- **(5, LOW) Rutas:** `IsSafeRelativePath` rechaza:
  - una ruta con espacios al principio o al final;
  - cualquier segmento que quede vacío tras `TrimEnd('.', ' ')` (`.`, `..`, `.. `, `...`, `a//b`);
  - cualquier segmento `.git` (sin distinguir mayúsculas).
- **(6, LOW) Tamaños:**
  - `spec.v1.json` añade `maxLength`: 16000 en los textos, 2000 por criterio y 300 por ruta (error: `es demasiado largo`).
  - Cada llamada lleva `MaxOutputTokens = 8192`.
  - Una respuesta de más de 200 000 caracteres cuenta como intento inválido sin analizarla.
  - Riesgo: algún servidor compatible con OpenAI antiguo podría no reconocer `max_completion_tokens`.
- **(9, MEDIUM) Test de redacción:** una key sin prefijo conocido (`plainprovidersecret123456`) que el Proveedor devuelve en `{"error":{"message":"bad key <key>"}}` con HTTP 401. El test comprueba que la key no aparece en `failure_detail`, `run_events` ni `GET /api/runs`, y que sí viaja en `Authorization`. Esta key la redacta `SecretRedactor`, porque conoce los valores `ApiKey` de la configuración.
- **(10) Tests de 1 y 2:**
  - Extracción y validación:
    - una respuesta con JSON indentado y ```` ``` ```` dentro de una cadena se acepta;
    - una propiedad extra con un nombre inyectado, o con caracteres de control, da `$: propiedad no admitida`;
    - en la tabla del extractor: candidatos inválidos o sin cerrar seguidos de uno válido, y una valla con JSON roto.
  - Prueba de punta a punta: el nombre inyectado no aparece ni en el mensaje de reintento ni en `AnalystOutputRejected` ni en `GET /api/runs`.
  - También hay tests de `Sanitize`, de los tamaños máximos, de `MaxOutputTokens` y de las rutas nuevas.
  - Total: 628 tests en verde.

## Spec Change Log

## Review Triage Log

Revisión 1 (revisor combinado, 11 hallazgos):

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | medium-high | Nombres de propiedad elegidos por el modelo llegan sin saneado al mensaje de reintento, `RunEvent` y terminal | patch |
| 2 | medium | El extractor JSON busca vallas antes que el texto completo y rompe Specs válidas con ``` en un valor | patch |
| 3 | medium | El `catch` del Analista convierte errores internos del sumidero en "Proveedor: …" | patch |
| 4 | low | Cancelación por apagado reportada como fallo del Proveedor | patch (1 línea) |
| 5 | low | `IsSafeRelativePath` acepta espacios iniciales/finales y segmentos `.. ` | patch (pequeño) |
| 6 | low | Sin límites de tamaño en la Spec ni en la respuesta | patch (`maxLength` + tope) |
| 7 | low | `BaseUrl` `http://` no loopback envía la key en claro | defer |
| 8 | low | El truncado a 2000 favorece rutas que ordenan antes | defer |
| 9 | medium | Redacción de la key solo probada con keys `sk-` (las enmascara el patrón) | patch |
| 10 | low | Sin tests de 1 y 2 | patch (junto a 1 y 2) |
| 11 | low | Truncado y fallo de listado sin test | reject |
