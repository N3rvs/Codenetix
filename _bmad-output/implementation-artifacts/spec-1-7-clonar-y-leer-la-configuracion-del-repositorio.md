---
title: 'Historia 1.7: Clonar y leer la configuración del repositorio'
type: 'feature'
created: '2026-10-27'
status: 'done'
route: 'dispatch'
baseline_commit: '7c598dd8c904154bbd538adad28e26bafe9877e5'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-1-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Una Ejecución no toca el repositorio. No se clona, no se fija el `BaseSha` y no se lee `codenetix.yml`, así que no hay Rutas protegidas, Revisores ni límites (FR-3, AD-8, AD-10, AD-16 paso 1).

**Approach:**
- El orquestador ejecuta el paso 1 de AD-16.
- El módulo GitHub clona con credenciales efímeras, fija el `BaseSha`, comprueba que no quedan credenciales y lee `codenetix.yml` desde ese commit.
- Pipeline valida el archivo contra el esquema público `schemas/codenetix.v1.json`, calcula la `EffectiveConfig` (el repositorio solo puede restringir) y la guarda como instantánea en la Ejecución.
- El puente de 1.6 avanza a `Analyzing` y falla ahí hasta la 1.8.

## Boundaries & Constraints

**Always:**
- **Esquema `schemas/codenetix.v1.json`** (JSON Schema 2020-12, `additionalProperties: false`). Claves:
  - `version` (const 1, obligatoria);
  - `protected_paths` (array de globs, únicos);
  - `reviewers` (array único, `minItems` 1, de `security`/`quality`/`tests`);
  - `max_review_rounds` (entero 1–5);
  - `max_coding_attempts` (entero 1–5);
  - `budget_usd` (número > 0);
  - `models` (objeto opcional con `analyst`, `coder` y `reviewers`, cadenas).

  El esquema se incrusta como recurso en Pipeline y se valida con JsonSchema.Net (YAML → JSON con YamlDotNet).
- **Valores por defecto (FR-3):**
  - Revisores `security`, `quality`, `tests`; Rutas protegidas `**/Migrations/**`, `**/*.csproj`, `.github/**`.
  - Límites de la instancia en `CODENETIX__Limits__*`: `MaxReviewRounds` = 2, `MaxCodingAttempts` = 3, `BudgetUsd` = nulo (sin límite).
- **`EffectiveConfig`** (Contracts, inmutable):
  - Límites: el mínimo entre la instancia y el repositorio.
  - Rutas protegidas: la unión de las de por defecto con las del repositorio; nunca se quitan las de por defecto.
  - Revisores: los del repositorio o, si no los define, los de por defecto.
  - Modelos: los del repositorio o nulos.
  - `Source`: `file` o `defaults`.
  - Se persiste en `run_runs.effective_config_json` junto a `base_sha` (nueva migración `run`), en la misma transacción que la transición.
- **Clonado (módulo GitHub)**, con `IRepositoryWorkspace.PrepareAsync(runId, owner, repo, ct)` → `{ WorkspacePath, BaseSha, RepoConfigYaml? }`:
  - `git clone --depth 1 --no-tags` de la rama por defecto, en `<DataDirectory>/workspaces/<shortId>`.
  - Credenciales solo por las variables de entorno del proceso `GIT_CONFIG_COUNT`/`GIT_CONFIG_KEY_0=http.extraHeader`/`GIT_CONFIG_VALUE_0=Authorization: Basic <base64(x-access-token:TOKEN)>`. Nunca en los argumentos, en la URL ni en `.git/config`.
  - `BaseSha` = `git rev-parse HEAD`.
  - Comprobación posterior: `git config --list --local` sin `extraheader` y la URL `origin` sin userinfo. Si falla, se borra el árbol y se informa del error.
  - `codenetix.yml` se lee con `git show <BaseSha>:codenetix.yml`; si no existe, el resultado es nulo.
  - La URL base del clon se configura en `GitHubOptions.CloneBaseUrl` (por defecto `https://github.com`), lo que permite repositorios locales en los tests.
  - El timeout del clon es de 120 s.
- **Orquestador** (sustituye el puente de 1.6):
  - `Received` → preparar → error de clonado o de credenciales: `Fail(CloneFailed, motivo sin el token)`.
  - YAML o esquema inválidos: `Fail(InvalidRepoConfig, "codenetix.yml: <ruta JSON>: <error>; …")`, sin llamar a ningún agente.
  - Si va bien: guarda la configuración y `StartAnalysis()` → `Analyzing` → `Fail(StageError, "Análisis no disponible todavía")`.
  - Eventos nuevos en la unión `RunEvent`: `RepositoryPrepared { baseSha }` y `RepoConfigResolved { source }`.
  - El árbol de trabajo se borra al llegar a un estado terminal (en un `finally`).
- **Imagen:** instalar `git` y `ca-certificates` en la etapa de runtime.

**Never:** el token en logs, argumentos, errores, `RunEvent` o BD; leer `codenetix.yml` del árbol de trabajo en vez de desde el `BaseSha`; registrar `codenetix.yml` en la DI; que el repositorio amplíe un límite o quite una Ruta protegida por defecto; el Sandbox (Épica 2).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Sin `codenetix.yml` | Repositorio sin el archivo | `Source=defaults`, los 3 Revisores y las 3 Rutas | — |
| Archivo válido | `reviewers: [security]`, `max_review_rounds: 1` | Revisores `[security]`, rondas 1 | — |
| Ampliar un límite | `max_review_rounds: 5` con la instancia en 2 | Efectivo 2 | — |
| Añadir rutas | `protected_paths: ["src/Auth/**"]` | Las 3 de por defecto más `src/Auth/**` | — |
| YAML mal formado | Tabulador o sintaxis inválida | `Failed(InvalidRepoConfig)` con línea y columna | Ningún agente |
| Esquema inválido | `reviewers: [hacker]` o una clave desconocida | `Failed(InvalidRepoConfig)` con la ruta JSON del error | — |
| `version` ausente o 2 | — | `Failed(InvalidRepoConfig)` | — |
| Clon fallido | Repositorio inexistente o red caída | `Failed(CloneFailed)` con el motivo, sin el token | Árbol borrado |
| Credenciales | Tras el clon | `.git/config` sin `extraheader` y `origin` sin userinfo | Si las hay → `CloneFailed` |
| Lectura desde el `BaseSha` | Archivo modificado en el árbol tras el clon | Se usa la versión del commit | — |
| Instantánea | Ejecución que llega a `Analyzing` | `effective_config_json` y `base_sha` persistidos | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Pipeline/` -- orquestador puente de 1.6 (sustituirlo), `PipelineRun`, `PipelineDbContext` con migración `run` y el sumidero; patrón de ticks UTC.
- `src/CodeNetix.GitHub/` -- `GitHubOptions` (`Token`, `ApiUrl`) y el cliente de 1.5; añadir `CloneBaseUrl` y `RepositoryWorkspace`.
- `src/CodeNetix.Contracts/` -- unión `RunEvent`, `FailureReason` (`CloneFailed`, `InvalidRepoConfig`) y `EventEnvelope.ObjectKey` (`github:<owner>/<repo>#<n>`).
- `src/CodeNetix.Host/Configuration/InstanceOptions.cs` -- añadir `Limits`.
- `src/CodeNetix.Persistence/PersistenceOptions.cs` -- `DataDirectory` de los árboles de trabajo.
- `docker/codenetix.Dockerfile` -- etapa de runtime sobre `aspnet:10.0`, que no trae `git`.
- `tests/` -- repositorios git locales creados en un directorio temporal (`git init`, commit con o sin `codenetix.yml`), con `CloneBaseUrl` apuntando a ellos.

## Tasks & Acceptance

**Execution:**
- [x] `schemas/codenetix.v1.json` -- el esquema público.
- [x] `src/CodeNetix.Contracts/` -- `EffectiveConfig`, `IRepositoryWorkspace` y los nuevos `RunEvent`.
- [x] `src/CodeNetix.GitHub/` -- `RepositoryWorkspace` (clon, `BaseSha`, comprobación de credenciales y lectura del YAML) y el borrado del árbol.
- [x] `src/CodeNetix.Pipeline/` -- `RepoConfigValidator`, `EffectiveConfigCalculator`, la migración y el orquestador.
- [x] `src/CodeNetix.Host/` -- `Limits` y la composición.
- [x] `docker/codenetix.Dockerfile` -- `git`.
- [x] `tests/` -- tests de tabla del validador y del cálculo; un test por fila de la matriz con repositorios locales; test de credenciales con un token falso (que no aparezca en `.git/config` ni en `RunEvent`).

**Acceptance Criteria:**
- Given una Ejecución de un repositorio sin `codenetix.yml`, when termina el paso 1, then queda en `Failed(StageError)` desde `Analyzing` con la `EffectiveConfig` por defecto persistida.
- Given un `codenetix.yml` inválido, when se valida, then la Ejecución pasa de `Received` a `Failed(InvalidRepoConfig)` y el detalle explica el error.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde.
- `docker build -f docker/codenetix.Dockerfile -t codenetix .` -- expected: imagen con `git` y tests de Linux en verde.

## Implementation Notes

- **Paquetes:** `JsonSchema.Net` 9.4.1 (la 9.5.0 tenía dos días) y `YamlDotNet` 18.1.0 en Pipeline; `Microsoft.Extensions.Logging.Abstractions` 10.0.14 en GitHub (registro del clon y de fallos al borrar). El esquema se incrusta con `LogicalName` `CodeNetix.Pipeline.codenetix.v1.json`; un test comprueba que el recurso es idéntico a `schemas/codenetix.v1.json`.
- **Contracts (`RepositoryConfig.cs`):** `EffectiveConfig` (con `ImmutableArray`), `ReviewerKind` y `ConfigSource` (se serializan `security`/`file`/`defaults`), `AgentModels`, `PreparedRepository`, `IRepositoryWorkspace`, `RepositoryPreparationException`, `GitHubObjectKey.TryParse` (owner/repo desde `github:<owner>/<repo>#<n>`) y `RunShortId.Of` (GitHub nombra el árbol con el `shortId` sin referenciar Pipeline; `PipelineRun.ShortIdOf` delega en él).
- **Decisión — `IRepositoryWorkspace.DeleteAsync(runId)`:** el spec solo nombra `PrepareAsync`; el borrado del árbol en el `finally` del orquestador necesita un método del módulo GitHub. Es de mejor esfuerzo (nunca lanza; registra un aviso) y en Windows quita el atributo de solo lectura a los objetos de `.git` sin seguir enlaces simbólicos.
- **Decisión — borrado:** el orquestador borra el árbol si la Ejecución acaba terminal **o** si algo lanza (el Dispatcher la cerrará como `StageError`; una cancelación por apagado la deja en `Received` y la reanudación vuelve a clonar desde cero, porque `PrepareAsync` descarta un árbol previo).
- **Decisión — directorio de trabajo:** `GitHubOptions.WorkspacesDirectory`, que Host rellena con `PostConfigure` a `<DataDirectory>/workspaces` (GitHub no puede referenciar Persistence).
- **Clon:** `git clone --quiet --depth 1 --no-tags --single-branch -- <url> <ruta>`. El proceso `git` arranca con un entorno limpio: se quitan todas las variables `GIT_*` heredadas (p. ej. `GIT_TRACE_CURL` volcaría la cabecera), `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, `GIT_LFS_SKIP_SMUDGE=1` y, por `GIT_CONFIG_*`, `credential.helper=` vacío (ningún almacén de credenciales del sistema) más `http.extraHeader` solo en el clon. Los comandos posteriores (`rev-parse`, `config`, `remote`, `ls-tree`, `show`) no reciben la credencial; tienen timeout de 30 s. Los motivos de error son la última línea de `stderr`, con el token, su forma URL y el base64 de la cabecera sustituidos por `***`, y acotados a 300 caracteres (además, el sumidero los redacta).
- **Comprobación de credenciales:** además de `config --list --local` sin `extraheader` y `origin` sin userinfo, se busca el token literal en la salida y en `.git/config`. `CloneBaseUrl` admite `https`, `http` y `file` (o una ruta local, que `Uri` convierte en `file://`) y se rechaza con userinfo, query o fragmento.
- **`codenetix.yml`:** `git ls-tree <BaseSha> -- codenetix.yml` y, solo si es un blob, `git show <BaseSha>:codenetix.yml`; un directorio con ese nombre cuenta como "sin archivo".
- **Validador:** YAML → JSON con el modelo de representación de YamlDotNet y los escalares del esquema core de YAML 1.2 (sin comillas: `true`/`false`, `null`/`~`, enteros y decimales; con comillas: texto). Rechaza varios documentos, archivos de más de 64 KB y expansiones de alias de más de 10 000 nodos. Errores en español: `codenetix.yml: YAML no válido en la línea L, columna C: …` (los mensajes del analizador se traducen para tabuladores y claves repetidas; el resto conserva el texto inglés de YamlDotNet) y `codenetix.yml: $.reviewers[0]: valor no admitido…; …` (ruta JSON estilo `$.a[0]`, sin errores de aplicadores, máximo 5). YamlDotNet sitúa el error de tabulador en 1:1: se recalcula buscando el primer tabulador de la sangría. El detalle del fallo se acota a 1000 caracteres.
- **Persistencia:** migración `AddRepositoryConfig` (`base_sha`, `effective_config_json`, ambas `TEXT NULL`) generada con `dotnet-ef`. `PipelineRun.RecordBaseSha` y `FreezeConfiguration` solo valen en `Received` y una vez (si no, `InvalidRunTransitionException`). Con YAML inválido se persisten `base_sha` y `RepositoryPrepared` junto al fallo, sin instantánea. El éxito confirma `base_sha`, la instantánea, `RepositoryPrepared`, `RepoConfigResolved` y `StateChanged(Received → Analyzing)` en un único `SaveChanges`; el puente a `Failed(StageError)` va en otro.
- **Límites de la instancia:** `InstanceLimits` (Pipeline, enlazado por Host desde `CODENETIX:Limits`) y `LimitsInstanceOptions` en `InstanceOptions` para validar. Decisión: valores fuera de rango (`MaxReviewRounds`/`MaxCodingAttempts` < 1, `BudgetUsd` ≤ 0) son motivos de `Diagnostic`. Un `budget_usd` del repositorio sí limita una instancia sin límite (restringe).
- **Objeto no GitHub:** si la `ObjectKey` no es `github:…`, `Failed(StageError, "objeto no admitido: …")` (hoy no ocurre: solo hay Eventos de GitHub).
- **`codenetix fix --follow`:** imprime `[n] RepositoryPrepared <sha>` y `[n] RepoConfigResolved <source>`.
- **Tests:** `CodeNetixFactory` apunta por defecto `CloneBaseUrl` a `LocalGitRepos.Default` (repositorio `o/r` compartido por proceso), así ningún test clona de la red; los de 1.6 se han actualizado a los 6 eventos del nuevo recorrido. `FakeGitHub` registra cualquier otra ruta (`MapFallback`), lo que permite comprobar que en un clon HTTP la credencial solo viaja en `Authorization: Basic`. Nuevos: `RepoConfigTests` (tabla del validador, del cálculo y de los límites) y `RepositoryPreparationTests` (clon, credenciales, lectura desde el `BaseSha`, timeout y la matriz de punta a punta). Aplazado de 1.6 resuelto: éxito del sumidero externo (seq = máx + 1, redacción) y reintento por conflicto de `seq`.

### Parches de la revisión 1

- **(1, HIGH) Anidamiento y alias:** `YamlToJson` ya no usa `YamlStream` (modelo de representación recursivo): recorre de forma iterativa los eventos del `Parser` de YamlDotNet (máquina de estados con pila explícita) con una pila propia. Profundidad máxima 32 (`codenetix.yml: anidamiento demasiado profundo`, por debajo del límite 64 de System.Text.Json); un alias solo puede apuntar a un ancla ya cerrada, así `a: &a [*a]` da `alias '*a' sin ancla previa o recursivo` (sin ciclos); cada alias gasta del presupuesto de 10 000 nodos el tamaño de su ancla. Las claves repetidas y las que no son texto se detectan en el mismo recorrido. El paso JSON → esquema → `Deserialize` está en un `try/catch` (`JsonException`, `InvalidOperationException`, `FormatException`, `OverflowException`) que devuelve `InvalidRepoConfig`, nunca `StageError`.
- **(2, MEDIUM) Números:** un decimal sin parte fraccionaria se emite como entero (`version: 1.0` y `max_review_rounds: 2.0` valen como 1 y 2; `2.5` sigue siendo `tipo no válido`). Un número fuera del rango de `decimal` (`budget_usd: 1e30`) es `YAML no válido … número fuera de rango` (se elimina el respaldo `double`). Un escalar con etiqueta `!!str` es texto.
- **(3, MEDIUM) Tests:** filas para `version: 1.0`, `2.0`, `1e0`, `1e30`, enteros de 32 dígitos, profundidad 31/33/100/10 000 (en flujo y en mapas), `a: &a [*a]`, `a: &a {b: *a}`, alias sin ancla y claves no textuales; y anclas válidas que siguen funcionando.
- **(4, MEDIUM) Guardas de nombres:** `GitHubObjectKey.IsName` (ahora público) rechaza también un `-` inicial, y `RepositoryWorkspace` lo reutiliza. `TryParse` deja `owner`/`repo`/`number` vacíos si falla. Tests de tabla para `..`, `.`, vacío, `/`, `#`, espacio, `%2e`, `\`, `-` inicial, `--upload-pack=…`, sin número, `##`, número negativo y otras fuentes.
- **(5, LOW) Configuración del host:** el proceso `git` recibe `GIT_CONFIG_NOSYSTEM=1` y `GIT_CONFIG_GLOBAL` = `NUL` (Windows) o `/dev/null`, así que no aplican `hooksPath`, `insteadOf` ni `include` del sistema o del usuario. Riesgo: en Windows se pierde también el `http.sslBackend=schannel` del sistema y Git for Windows usa su propio paquete de CA; contra `https://github.com` debería funcionar, pero no está probado (los tests usan `file://` y HTTP local).
- **(6, LOW) Tras matar git:** la espera de `stdout`/`stderr` se acota a 5 s (`WaitAsync`) y se sigue aunque un proceso nieto retenga las tuberías.

## Spec Change Log

## Review Triage Log

Revisión 1 (revisor combinado, 13 hallazgos):

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | high | Anidamiento profundo o alias cíclico en `codenetix.yml` → excepción no capturada o desbordamiento de pila | patch |
| 2 | medium | `1.0`/`2.0`/`1e30` pasan el esquema y fallan al deserializar → `StageError` | patch |
| 3 | medium | Sin tests de 1 y 2 | patch |
| 4 | medium | Sin tests de los guardas de owner/repo (`TryParse`, `CloneUrlOf`) | patch |
| 5 | low | Ausencia del token en argumentos y limpieza de `GIT_*` sin test | reject |
| 6 | low | Comprobación de credenciales solo probada sobre `file://` | defer |
| 7 | low | La config global/sistema de git aplica (`core.hooksPath`, `insteadOf`) | patch (2 líneas) |
| 8 | low | `CloneBaseUrl` `http://` envía el token en claro | defer |
| 9 | low | Tras `Kill`, la espera de las tuberías puede colgarse | patch (1 línea) |
| 10 | low | Árboles de trabajo huérfanos tras una caída | defer (5.3 reconciliación) |
| 11 | low | `finally` del orquestador solo probado en caminos terminales | reject |
| 12 | low | Colisión de shortId entre Ejecuciones activas | reject (improbable; reintento en creación) |
| 13 | low | `Thread.Sleep` en `DeleteTree` | reject |
