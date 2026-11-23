---
title: 'Historia 8.1: Configuración de claves desde el Panel'
type: 'feature'
created: '2026-11-23'
status: 'done'
baseline_commit: 'cbe860030f126984b337640e53a823244ca14d6c'
route: 'dispatch'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/planning-artifacts/architecture/architecture-Codenetix-2026-10-26/ARCHITECTURE-SPINE.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-1-2-configurar-la-instancia-y-modo-diagnostico.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-1-3-acceso-seguro-al-panel-y-la-api.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Para usar CodeNetix hay que pasar el token de GitHub y la key del Proveedor como variables de entorno (`CODENETIX__GitHub__Token`, `CODENETIX__Providers__Anthropic__ApiKey`…) **antes** de arrancar el contenedor. Un usuario que no es DevOps se queda en modo `Diagnostic` con un mensaje que le pide "define la variable y reinicia", y para cambiar una key caducada tiene que recrear el contenedor.

**Approach:** En `serve`, el Panel ofrece una pantalla "Configuración" (automática en `Diagnostic`, y luego desde un botón de la cabecera) donde se pegan el token de GitHub, la key de Anthropic y, opcionalmente, la de OpenAI. Host las **valida contra el servicio real** (`GET /user` de GitHub, `GET <BaseUrl>/models` del Proveedor), las guarda **cifradas** (AES-256-GCM) en el directorio de datos y las aplica **en caliente**: invalida las opciones que dependen de ellas, actualiza el Egress Gateway y el redactor, y reevalúa la salud, de modo que `Diagnostic` pasa a `Ready` sin reiniciar el proceso. El entorno y el YAML siguen teniendo prioridad: una clave definida ahí aparece en el Panel como "configurada por variable de entorno" (o por el archivo de instancia) y es de solo lectura.

## Boundaries & Constraints

**Always:**
- **Precedencia (AD-10):** entorno y `codenetix.instance.yml` > Panel. Una clave con valor (no vacío) en la configuración nunca se sobrescribe ni se oculta con la del Panel; el `PUT` responde `409` si se intenta cambiar. La clave del Panel solo rellena huecos.
- **Claves gestionables desde el Panel:** `GitHubToken` (`CODENETIX:GitHub:Token`), `AnthropicApiKey` (`CODENETIX:Providers:Anthropic:ApiKey`) y `OpenAIApiKey` (`CODENETIX:Providers:OpenAI:ApiKey`). URL base, modelos y Proveedores locales siguen en entorno/YAML (la validación usa `CODENETIX__Providers__<Nombre>__BaseUrl` si está definida).
- **Almacenamiento:** `<DataDirectory>/panel-secrets.enc` (sobre JSON `version`, `nonce`, `tag`, `ciphertext`; AES-256-GCM con datos asociados fijos) y la clave de cifrado en `<DataDirectory>/panel-secrets.key` (32 bytes aleatorios). Ambos con permisos 0600 fuera de Windows desde su creación, escritura atómica (temporal + `Move`). Nunca en SQLite, `RunEvent`, logs ni respuestas.
- **API (`:5000`, `admin-token`, AD-17):** `GET /api/settings/secrets` y `PUT /api/settings/secrets`. Solo devuelven por clave: configurada o no, origen (`None`, `Environment`, `ConfigFile`, `Panel`), si es editable, el nombre de la variable y una pista enmascarada (`…` + últimos 4 caracteres, solo con 12 o más caracteres). Además el estado de salud y sus motivos, un aviso si el archivo guardado no se pudo leer y, tras un `PUT`, el resultado de cada comprobación.
- **Validación antes de guardar (todo o nada):** formato (8–1024 caracteres, solo ASCII imprimible 0x21–0x7E; si no, "la clave contiene caracteres no válidos (¿espacios o caracteres invisibles al copiarla?)") → `400`, y solo un valor con formato válido entra en el redactor; origen en entorno/YAML → `409`; comprobación remota en paralelo; si alguna falla → `422` con un mensaje en español por clave (`errors`) y **no se guarda nada**. Tampoco se guarda si la combinación nueva introduce un motivo de `Diagnostic` que antes no existía (p. ej. dos Proveedores con key y ninguno es Anthropic) → `422`.
  - GitHub: `GET {ApiUrl}/user` con `Authorization: Bearer`; informa del login y, si GitHub envía `X-OAuth-Scopes` (token clásico), de los permisos; avisa si falta `repo`. Token fine-grained: recuerda los permisos necesarios. Un único plazo cubre la petición y la lectura del cuerpo (acotado a 64 KiB).
  - Anthropic: `GET {BaseUrl}/models` con `x-api-key`, `Authorization: Bearer` y `anthropic-version` (como el Gateway); OpenAI: con `Authorization: Bearer`.
  - Mensajes: token/key rechazado (401/403), límite de peticiones (429), URL incorrecta (404), servicio caído (otros), sin red, sin respuesta en 10 s. Sin redirecciones (el secreto no sale del host configurado). Nunca se cita el cuerpo de la respuesta ni el secreto.
- **Aplicación en caliente:** valores con formato válido → `ISecretRedactor` (antes de cualquier E/S) → comprobación → guardar → invalidar las cachés de `InstanceOptions`, `GitHubOptions` y `AgentsOptions` → recalcular el `Coder` del Egress Gateway → si la instancia estaba en `Diagnostic`, reevaluar salud (configuración + Docker + motivos de arranque que exigen reinicio) y publicar `Ready` o `Diagnostic`, con plazo propio de 30 s y sin el token de la petición (si el cliente se va tras guardar, la reevaluación termina igual). Con `Ready`, el `Dispatcher`, el sondeo y los webhooks despiertan sin reiniciar. Una rotación en `Ready` no vuelve a comprobar Docker ni degrada la instancia.
- **Nada captura los secretos al arrancar:** en `serve`, `IOptions<InstanceOptions|GitHubOptions|AgentsOptions>` leen el valor actual del monitor; `GitHubApiClient` lee el token en cada petición; `GitHubIdentity` cachea el login por token; el Gateway lee el `Coder` en cada petición; el redactor admite secretos nuevos. Las claves nuevas se aplican **al momento, también a las Ejecuciones en curso** (sus siguientes llamadas a GitHub, al Proveedor y al Gateway); el Panel lo dice y recomienda rotar cuando no haya ninguna en marcha.
- **Panel:** en `Diagnostic`, tras el `admin-token`, la pantalla de configuración sustituye al aviso "define las variables y reinicia" **solo si** falta una clave (`keysMissing`) y algún campo es editable; si no, se mantiene esa guía con los motivos. En la pantalla inicial, "Ir al Panel" en cuanto el estado es `Ready`; en `Ready`, botón "Configuración" junto a "Cambiar token" (modal). Campos `password`, ayuda breve en español con enlaces (token fine-grained de GitHub con Contents, Issues y Pull requests en lectura y escritura y Metadata en lectura; consola de Anthropic; claves de OpenAI), botón "Probar y guardar", errores por campo y estado de éxito. Las claves de entorno se muestran deshabilitadas con su variable.
- **Contrato (AD-14):** OpenAPI exportado a `panel/openapi/v1.json` y `panel/src/api/generated.ts` regenerado con `npm run generate:api`.

**Ask First:**
- Gestionar desde el Panel otros secretos (webhooks, Sentry, `AccessToken`) o la URL/modelo de Proveedores.
- Guardar sin validar.

**Never:**
- Devolver, registrar o persistir en claro un secreto; incluirlo en `RunEvent`, errores o ProblemDetails.
- Exponer estos endpoints en `:5001` o `:8080`, o sin `admin-token`.
- Sobrescribir o "sombrear" una clave del entorno/YAML con la del Panel.
- Guardar una clave que no ha pasado la validación.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Primer arranque | Sin claves | `Diagnostic`; `GET` → tres claves `None`, editables | — |
| Guardar válidas | `PUT {gitHubToken, anthropicApiKey}` válidos | `200`, `Ready`, origen `Panel`, pista `…wxyz`; `/api/health` `Ready`; nuevas Ejecuciones usan las claves | — |
| Token inválido | GitHub `/user` → 401 | `422`, `errors.gitHubToken` "GitHub rechaza el token…"; nada guardado; sigue `Diagnostic` | — |
| Una válida, otra no | GitHub ok, Anthropic 401 | `422` con `errors.anthropicApiKey`; **nada** guardado (tampoco GitHub) | — |
| Sin red | Host inalcanzable | `422` "No se puede conectar con …" | — |
| Formato | Vacío, espacios internos, < 8 caracteres | `400` | — |
| Clave de entorno | `CODENETIX__GitHub__Token` definido | `GET` → `Environment`, no editable, pista; `PUT gitHubToken` → `409`; nada guardado | — |
| Rotación | Key A guardada, `PUT` key B | `200`; el Gateway inyecta B en la siguiente petición; A y B redactadas | — |
| Rotación de GitHub | Token A → B (otra cuenta) | Las llamadas a GitHub usan B y el login se vuelve a consultar | — |
| Empeora la config | Proveedor local con key + `PUT openAIApiKey` | `422` con el motivo nuevo; nada guardado; sigue `Ready` | — |
| Sin token | `GET`/`PUT` sin `Authorization` o con token erróneo | `401` | — |
| Puerto de webhooks | `/api/settings/secrets` en `:5001` u `:8080` | `404` | — |
| Secretos fuera | Cualquier respuesta, log o archivo | El secreto no aparece; el archivo no contiene el secreto ni su base64 | — |
| Reinicio | Claves guardadas, nuevo proceso | Se descifran y la instancia arranca `Ready` | — |
| Archivo ilegible | Clave de cifrado borrada o archivo manipulado | Arranca sin las claves del Panel, aviso en el log y en `GET` (`notice`); volver a guardarlas lo arregla | No bloquea la instancia |
| Arrancando | `PUT` en `Starting` | `503` | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Host/Configuration/PanelSecretStore.cs` (nuevo) -- almacén cifrado, `PanelSecrets`, `PanelSecretId`, origen de cada clave y superposición sobre las opciones.
- `src/CodeNetix.Host/Configuration/LiveOptions.cs` (nuevo) -- `IOptions<T>` respaldado por `IOptionsMonitor<T>`.
- `src/CodeNetix.Host/Configuration/SecretValidator.cs` (nuevo) -- comprobación remota de GitHub y del Proveedor.
- `src/CodeNetix.Host/Configuration/PanelSecretsService.cs` (nuevo) -- validar, guardar, aplicar en caliente y reevaluar salud.
- `src/CodeNetix.Host/Configuration/PanelSecretsComposition.cs` (nuevo) -- registro solo en `serve`.
- `src/CodeNetix.Host/Features/ConfigureSecrets/ConfigureSecretsEndpoint.cs` (nuevo) -- `GET`/`PUT /api/settings/secrets`.
- `src/CodeNetix.Host/Configuration/InstanceHealth.cs`, `StartupReconciler.cs` -- motivos de arranque persistentes y reevaluación.
- `src/CodeNetix.Host/Configuration/InstanceOptionsValidator.cs`, `AgentEndpointResolver.cs` -- textos y URL base reutilizable.
- `src/CodeNetix.Host/Secrets/SecretRedactor.cs` -- `AddSecrets`.
- `src/CodeNetix.Host/CodeNetixComposition.cs`, `ServeMode.cs` -- composición.
- `src/CodeNetix.GitHub/GitHubApiClient.cs`, `GitHubIdentity.cs` -- token actual por petición y login por token.
- `panel/src/components/SecretsSettings.tsx` (nuevo), `panel/src/api/client.ts`, `panel/src/App.tsx`, `panel/src/styles.css` -- pantalla, modal y botón.
- `panel/openapi/v1.json`, `panel/src/api/generated.ts` -- contrato.
- `tests/CodeNetix.Host.Tests/PanelSecretsTests.cs` (nuevo), `FakeGitHub.cs`, `FakeOpenAIServer.cs`; `panel/src/__tests__/secrets.test.ts` (nuevo).
- `README.md`, `_bmad-output/planning-artifacts/epics.md`.

## Tasks & Acceptance

**Execution:**
- [x] Host -- almacén cifrado, validador, servicio, endpoints, composición, salud.
- [x] GitHub -- token y login no capturados al arrancar.
- [x] Panel -- pantalla de configuración, botón, cliente, contrato.
- [x] Pruebas -- integración (una por fila) y Vitest.
- [x] README y épicas.

**Acceptance Criteria:**
- Given una instancia en `Diagnostic` por falta de claves, when el usuario pega un token de GitHub y una key de Anthropic válidos y pulsa "Probar y guardar", then la instancia pasa a `Ready` sin reiniciar y el Panel muestra el Panel en vivo.
- Given una clave inválida, when se guarda, then se rechaza con un mensaje claro en español y no se guarda nada.
- Given una clave definida por variable de entorno, when se abre la Configuración, then aparece como "configurada por variable de entorno", de solo lectura, y el `PUT` responde `409`.
- Given una key rotada desde el Panel, when empieza una Ejecución nueva, then el Gateway y las llamadas a GitHub usan la clave nueva.
- Given cualquier respuesta de la API, log o el archivo guardado, then el secreto nunca aparece en claro.

## Design Notes

- **Conflicto con AD-11 ("ningún secreto de terceros se persiste") y AD-10 (configuración = entorno + YAML):** la necesidad del usuario exige persistir. Opción más segura que la cumple: persistir **solo** las tres claves del Panel, cifradas con AES-256-GCM (autenticado: detecta manipulación) en un archivo propio fuera de SQLite, nunca en tablas ni eventos, con el entorno/YAML con prioridad absoluta. Se documenta como desviación acotada de AD-11.
- **Por qué AES-GCM con un archivo de clave y no Data Protection:** formato propio de un solo archivo, sin caducidad ni rotación implícita de claves (Data Protection está pensado para cargas de vida corta), sin el aviso "No XML encryptor configured" en los logs y fácil de verificar en pruebas. Mismo modelo de amenaza que `admin.token`: protege frente a copias del archivo de claves sin la clave (copias de seguridad parciales, volcados, `cat` accidental), **no** frente a quien ya puede leer todo `/data` (que también tiene el `admin-token` y la base de datos). Riesgo documentado.
- **Sin "guardar sin validar":** una key que no se puede comprobar tampoco servirá para trabajar, y el entorno sigue disponible como alternativa. Ask First.
- **Aplicación en caliente sin tocar los módulos:** en `serve` Host registra `IOptions<T>` respaldado por `IOptionsMonitor<T>` para `InstanceOptions`, `GitHubOptions` y `AgentsOptions` (los módulos siguen pidiendo `IOptions<T>` y leen `.Value` en cada uso) e invalida sus cachés en orden tras guardar. `SandboxOptions` no se invalida (su instancia del monitor guarda el puerto real del proxy `CONNECT`): solo se reemplaza su `Coder`. Solo cambian dos clases de GitHub que copiaban el valor en el constructor o cacheaban el login para siempre.
- **No empeorar `Ready`:** la reevaluación completa (Docker incluido) solo ocurre desde `Diagnostic`; los motivos que exigen reiniciar (Ejecuciones no reconciliadas) se conservan.
- **Archivo ilegible = aviso, no `Diagnostic`:** si fuera bloqueante y las claves vinieran del entorno, no habría forma de salir desde el Panel.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde (dos veces seguidas).
- `npm --prefix panel run check:api`, `npm --prefix panel test` y `npm --prefix panel run build` -- expected: en verde.
- `docker build -f docker/codenetix.Dockerfile -t codenetix .` -- expected: imagen construida.

## Implementation Notes

- **Host:** `PanelSecretStore` (AES-256-GCM, carga perezosa y sin logger: el redactor depende de él y el registro del redactor), `PanelSecretCatalog` (clave de configuración, variable, origen por el proveedor de configuración que gana, superposición y pista), `SecretValidator` (`HttpClient` propio con `SocketsHttpHandler`, sin redirecciones, 10 s), `PanelSecretsService` (cerrojo, todo o nada, "no empeorar la configuración", guardado, invalidación y salud) y `PanelSecretsComposition.AddPanelSecrets` (solo `serve`). `StartupReconciler` registra los motivos de arranque persistentes en `InstanceHealth.StartupReasons` y avisa si el archivo guardado es ilegible. `SecretRedactor.AddSecrets` sustituye la tabla entera (lectura sin bloqueo). `AgentEndpointResolver.ResolveBaseUrl` se reutiliza en la validación. `CodeNetixComposition.ResolveSandboxCoder` compartido por la composición y la aplicación en caliente. Mensajes de `Diagnostic` de claves que faltan: mencionan también el Panel.
- **GitHub:** `GitHubApiClient` lee `IOptions<GitHubOptions>.Value` en cada petición; `GitHubIdentity` cachea el login junto al token con el que se obtuvo.
- **Endpoints:** `GET`/`PUT /api/settings/secrets` (`Features/ConfigureSecrets/`). Errores RFC 9457 con extensión `errors` por campo (`gitHubToken`, `anthropicApiKey`, `openAIApiKey`).
- **Panel:** `SecretsSettings` (modo `setup` a pantalla completa en `Diagnostic` y `modal` desde "Configuración"), `TokenGate` con los motivos de `Diagnostic`, `fetchSecretsSettings`/`updateSecrets`/`SecretsUpdateError` en `client.ts`, estilos nuevos con los tokens existentes. En la pantalla inicial, tras guardar con éxito, se muestra el resultado de cada comprobación y "Ir al Panel".
- **Tests:** `PanelSecretsTests` (25 tests / 35 casos tras la revisión 1, una o más por fila de la matriz más el almacén); `FakeGitHub.OAuthScopes`, `FakeOpenAIServer` con `GET /v1/models` y `ModelsStatus`, `FakeSandboxHealthCheck.Delay`/`Calls`. Vitest: `secrets.test.ts` (13 tests).
- **Verificación (revisión 1):** `dotnet build CodeNetix.slnx` 0 advertencias; `dotnet test --solution CodeNetix.slnx` en verde dos veces seguidas (1276: 1275 + 1 omitido en la primera —prueba de Docker que se omite si Docker no está disponible en ese momento— y 1276/1276 en la segunda); `npm test` 92/92, `npm run build` y `npm run check:api` en verde. `docker build` en verde antes de la revisión (no se ha repetido).
- **Desviaciones:** AD-11 (persistencia acotada y cifrada de tres claves, ver Design Notes); solo Anthropic y OpenAI desde el Panel (Proveedores con URL propia siguen en entorno/YAML); sin "guardar sin validar"; borrar una clave guardada desde el Panel no está incluido (se sustituye pegando otra).
- **Riesgos / pendientes:**
  - La clave de cifrado vive en el mismo volumen que el archivo cifrado (como `admin.token`): no protege frente a quien lee todo `/data`. Mejora posible: aceptar la clave maestra por variable de entorno o secreto de Docker.
  - No probado contra GitHub ni Anthropic reales: `GET {BaseUrl}/models` con `x-api-key` y `anthropic-version` según su documentación; un Proveedor compatible que no implemente `/models` daría `404` y no se podría guardar desde el Panel (el entorno sigue disponible).
  - La rotación en `Ready` no vuelve a comprobar Docker. Las claves nuevas se aplican también a las Ejecuciones en curso (el Gateway lee el `Coder` en cada petición y GitHub el token en cada llamada): el Panel recomienda rotar sin Ejecuciones en marcha.
  - `IOptions<T>` "vivo" solo para `InstanceOptions`, `GitHubOptions` y `AgentsOptions` en `serve`; `SandboxOptions` solo actualiza `Coder` en su sitio.

## Spec Change Log

- Revisión 1: el formato admite solo ASCII imprimible y decide qué entra en el redactor; la reevaluación de salud tras guardar usa su propio plazo; las claves nuevas se aplican también a las Ejecuciones en curso (antes se decía lo contrario); la pantalla de configuración solo aparece si falta una clave editable (`keysMissing` nuevo en la respuesta); Anthropic se valida con `x-api-key` y `Authorization: Bearer`.

## Review Triage Log

Revisión 1:

| # | Sev. | Hallazgo | Decisión | Qué se hizo |
|---|------|----------|----------|-------------|
| M1 | medium | Los valores enviados entraban en el redactor antes de validarlos: "main" acabaría enmascarado en todas las respuestas y logs | patch | `PanelSecretsService`: formato primero (8–1024, solo ASCII 0x21–0x7E; si no, `400` "La clave contiene caracteres no válidos (¿espacios o caracteres invisibles al copiarla?)") y solo después `AddSecrets`. Test: `PUT gitHubToken="main"` → `400`, un 404 posterior con `/api/main-branch-status` no se enmascara; un valor con U+200B → `400` y tampoco se registra. Casos nuevos en la tabla de formato (U+200B, `ñ`, tabulador) |
| M2 | medium | La reevaluación de salud tras guardar usaba el token de la petición: si el cliente se iba, las claves quedaban guardadas y la instancia en `Diagnostic` | patch | `CancellationToken` propio con plazo de 30 s (`HealthCheckTimeout`); al vencer se registra un aviso y el estado no cambia |
| M2-T | medium | Sin test de la cancelación tras guardar | patch | `FakeSandboxHealthCheck.Delay`/`Calls`; test que cancela el `PUT` cuando el archivo ya está guardado y espera `Ready`. Comprobado que falla con el código anterior |
| M3 | medium | El texto del Panel decía que las Ejecuciones en curso terminan con las claves anteriores, y no es así | patch | `ROTATION_NOTICE` en `SecretsSettings.tsx` (diálogo y nota): se aplican al momento, también a las Ejecuciones en marcha; recomienda rotar sin ninguna en curso. Spec (Always, Riesgos) alineada. Test Vitest |
| T1 | medium | Sin test de redirección de GitHub | patch | Test con dos servidores: `/user` → `302` al segundo; `422` "redirección (HTTP 302)", el segundo no recibe nada y no se guarda |
| T2 | medium | Sin test de timeout | patch | `SecretValidator` de 1 s inyectado; Proveedor que no responde y GitHub que deja el cuerpo a medias → `422` "… no responde (sin respuesta en 1 s)" en ~1 s |
| T3 | medium | Sin test de `429` | patch | GitHub y Proveedor con `429` → mensajes de rate limit; nada guardado |
| T4 | medium | Sin test de motivos de arranque/Docker tras guardar | patch | Docker caído al arrancar → guardar deja `Diagnostic` solo con ese motivo (`keysMissing=false`); después, con una Ejecución no reconciliada registrada (`RecordStartupReasons`), otra rotación sigue en `Diagnostic` con ese motivo |
| T5 | medium | Sin test de rotación en `Ready` con Docker caído | patch | Rotar con Docker caído → sigue `Ready`, no se vuelve a comprobar Docker y la key nueva está aplicada |
| T6 | medium | Sin test del aviso por `.enc` ilegible | patch | `.enc` corrupto antes de arrancar → `GET` en `Ready` con `notice` que nombra el archivo |
| La | low | El plazo de `/user` no cubría la lectura del cuerpo, y la lectura no estaba acotada | patch | Un único `CancellationTokenSource` para petición y cuerpo; lectura por bloques hasta 64 KiB; `IOException` → "no se puede conectar". Cubierto por T2 |
| Lb | low | La pantalla de configuración aparecía en cualquier `Diagnostic` | patch | `keysMissing` en `SecretsSettingsResponse` (OpenAPI y `generated.ts` regenerados); `shouldOfferSetup` = `keysMissing` y algún campo editable; si no, la guía "define las variables y reinicia" con los motivos. La decisión la toma `SecretsSettings` (necesita la respuesta autenticada), que es lo que `App.tsx` pinta en `Diagnostic`. Tests Vitest |
| Lc | low | "Ir al Panel" solo tras guardar en esa pantalla | patch | Se muestra en cuanto la respuesta de guardado o el estado cargado es `Ready`. Test Vitest |
| Ld | low | `autoComplete="off"` en los campos de clave | patch | `autoComplete="new-password"`. Test Vitest |
| Le | low | Anthropic se validaba solo con `x-api-key`, distinto del Gateway | patch | `x-api-key` + `Authorization: Bearer` + `anthropic-version`; el test de guardado comprueba ambas cabeceras |
