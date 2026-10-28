# Epic 2 Context: Código y tests en un Sandbox seguro

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Ejecutar el trabajo de programación y verificación dentro de un entorno efímero y estrictamente aislado: el código del repositorio y el Programador (Claude Code) corren únicamente dentro de un contenedor Docker desechable por Ejecución, sin acceso directo a internet, sin `.git` y sin ver jamás las keys reales de los Proveedores ni el token de GitHub. Todo el tráfico saliente del Sandbox pasa por el Egress Gateway del host (proxy inverso hacia el Proveedor del Programador con sustitución de key ficticia, y proxy `CONNECT` hacia los feeds de NuGet permitidos), y cualquier cambio debe compilar, superar los tests existentes e incluir al menos un test nuevo antes de poder pasar a Revisión.

## Stories

- Story 2.1: Sandbox efímero por Ejecución
- Story 2.2: Red aislada y Egress Gateway
- Story 2.3: El Programador implementa la Spec
- Story 2.4: Tests obligatorios con reintentos
- Story 2.5: Prueba de fuego de seguridad

## Requirements & Constraints

- **Aislamiento del Sandbox:** contenedor efímero por Ejecución con imagen `codenetix-sandbox` (SDK .NET 10 + Node 24 LTS, usuario no root `1000:1000`), volumen con nombre `codenetix-ws-<runId>` montado en `/workspace` sin `.git`, y volumen `codenetix-tools` montado en solo lectura en `/tools` (salvo durante la provisión inicial del CLI). Nunca se usan *bind mounts* de rutas del contenedor host para mantener portabilidad en Linux, macOS y Windows (Docker Desktop).
- **Ciclo de vida y parada:** el Sandbox se crea tras el clonado y se destruye siempre en el `finally` de la Ejecución (contenedor y volumen de trabajo), sea cual sea el Estado final. Al cancelarse el token de la Ejecución, el contenedor se detiene en un máximo de 10 s.
- **Red aislada y Egress Gateway:** el Sandbox se conecta únicamente a la red Docker `internal` `codenetix-sandbox` (sin salida a internet), en la que el contenedor `codenetix` es alcanzable con el alias `gateway`.
- **Protección de secretos en el Sandbox:** dentro del Sandbox no existen ni el token de GitHub ni las keys reales de los Proveedores. Para hablar con el Proveedor del Programador, el Sandbox recibe `ANTHROPIC_BASE_URL=http://gateway:8080/llm` y una key ficticia única por Ejecución (`ANTHROPIC_API_KEY=cnx-run-<runId>-<nonce>`). El Gateway verifica la key ficticia activa de la Ejecución, la sustituye por la key real del Proveedor, retransmite respuestas SSE sin almacenarlas en buffer y registra el consumo (`usage`) asociado al `runId`.
- **Salida a NuGet y bloqueo por defecto:** el Gateway expone un proxy HTTP `CONNECT` en `gateway:8081` que solo permite túneles hacia los hosts de feeds de NuGet configurados (sin inspeccionar el contenido TLS). Cualquier otro destino o petición se rechaza y se registra como `RunEvent` de conexión bloqueada. La interfaz del Gateway no expone ningún otro endpoint.
- **Programador (`ICodingAgent`):** `ClaudeCodeAgent` ejecuta dentro del Sandbox `claude --bare -p <prompt> --output-format json --permission-mode acceptEdits --allowedTools "Bash,Read,Edit,Write" --permission-prompts none`. `--bare` es obligatorio para ignorar hooks o servidores MCP del repositorio clonado. El CLI `@anthropic-ai/claude-code` (versión fijada `2.1.311`) no se incluye en la imagen pública; se instala la primera vez en el volumen `codenetix-tools` a través del Gateway habilitando temporalmente el registro de npm solo durante ese paso.
- **Datos no confiables y observabilidad:** la Spec y el texto del Evento se entregan al Programador delimitados como datos (`UntrustedText`). La salida del proceso se emite como `RunEvent` `OutputAppended` mediante `IRunEventSink` (que aplica `ISecretRedactor` y el límite de 5 MB por Etapa).
- **Tests obligatorios y reintentos:** tras cada intento del Programador, `ITestRunner` ejecuta `dotnet build` y `dotnet test` dentro del Sandbox y genera un `TestReport` estructurado comparando contra una ejecución base tomada al crear el Sandbox. Para aprobar el intento, el código debe compilar, pasar los tests y aportar al menos un test nuevo. Si el Programador falla, no deja cambios o el `TestReport` no cumple, se reintenta pasando el fallo como feedback hasta el máximo de intentos de la `EffectiveConfig` (3 por defecto); al agotarlos, la Ejecución termina en `Fallida(TestsFailed)`.
- **Puente provisional hacia la Épica 3:** en la historia 2.3 desaparece el puente de la 1.8 ("Programación no disponible") para que la Ejecución pase de `Analizando` a `Programando`; en la 2.4, cuando el `TestReport` es válido, la Ejecución termina provisionalmente en `Fallida(StageError)` con motivo "Revisión no disponible" y el `TestReport` consultable por la API hasta que la Épica 3 conecte a los Revisores.
- **Prueba de fuego de seguridad:** la suite de integración con Docker debe incluir un test automatizado con un repositorio hostil que intente leer `ANTHROPIC_API_KEY`, `.git/config` y el token de GitHub y abrir una conexión externa arbitraria, verificando que solo ve la key ficticia, no halla credenciales y la salida externa queda bloqueada con su `RunEvent`.

## Technical Decisions

- **Fronteras de módulos (AD-1):** `CodeNetix.Sandbox` gestiona contenedores, volúmenes, la red aislada, el Egress Gateway y `ITestRunner`; `CodeNetix.Agents` implementa `ClaudeCodeAgent` (`ICodingAgent`); `CodeNetix.Pipeline` orquesta los pasos, persiste las Etapas (`run_stages`) y es dueño de `PipelineRun` e `IRunEventSink`. Ningún módulo de negocio referencia a otro; todos se comunican por `CodeNetix.Contracts` y `CodeNetix.Host` compone la DI y los listeners.
- **Egress Gateway (AD-7):** vive en el proceso del host con dos puertos dedicados separados de `:5000` y `:5001`:
  - `:8080` — proxy inverso HTTP con `Yarp.ReverseProxy` (`2.3.0`) limitado exclusivamente a la ruta `/llm/**`. Valida el token efímero `cnx-run-<runId>-<nonce>` (en `x-api-key` o `Authorization: Bearer`), inyecta la credencial real del Proveedor del Programador, desactiva el buffering de respuesta para streaming SSE y extrae los contadores de `usage` al finalizar para reportarlos asociados al `runId`.
  - `:8081` — proxy `CONNECT` minimalista que valida el `host:port` solicitado contra la lista blanca de feeds de NuGet configurados (por defecto `api.nuget.org:443`) y, cuando se autoriza explícitamente durante la provisión de herramientas, el registro de npm. Cualquier destino no autorizado recibe respuesta de rechazo y emite un `RunEvent` vía `IRunEventSink`.
- **Red Docker (AD-6, AD-7):** red `codenetix-sandbox` creada con `Internal: true` y etiqueta `codenetix.managed=true`. Cuando el propio host corre en un contenedor en esa red, responde bajo el alias `gateway`; los contenedores Sandbox se crean en la red `codenetix-sandbox` (sustituyendo el `NetworkMode: none` provisional de la 2.1) y reciben las variables de entorno de proxy (`HTTPS_PROXY`/`HTTP_PROXY` apuntando a `http://gateway:8081` y `ANTHROPIC_BASE_URL=http://gateway:8080/llm`).
- **Secuencia del orquestador (AD-16):** 1) Clonar y resolver `EffectiveConfig`; 2) `ISandboxManager.CreateForRunAsync`; 3) Analista genera `Spec`; 4) bucle de intentos de Programación (`ICodingAgent.RunAsync` → `ITestRunner.RunAsync`); 8) liberar Sandbox en el `finally`.

## Cross-Story Dependencies

- **2.1 → 2.2:** la 2.1 creó el cliente HTTP de Docker, `SandboxManager`, `SandboxSession` y la imagen `codenetix-sandbox` con `NetworkMode: none`. La 2.2 añade la red Docker `internal` `codenetix-sandbox`, conecta los Sandboxes a ella y levanta el Egress Gateway (`:8080/llm` con YARP y `:8081` `CONNECT` para NuGet) con las keys ficticias por Ejecución y el `RunEvent` de bloqueo.
- **2.2 → 2.3:** la 2.3 (`ClaudeCodeAgent`) necesita que el Sandbox ya tenga salida hacia `http://gateway:8080/llm` con `cnx-run-<runId>-<nonce>` y que la provisión de `@anthropic-ai/claude-code` en `codenetix-tools` pueda descargar el paquete a través del Gateway.
- **2.3 → 2.4:** la 2.4 envuelve la llamada al Programador en el bucle de intentos con `ITestRunner` (ejecución base al crear el Sandbox + validación de compilación, tests en verde y al menos un test nuevo tras cada intento).
- **2.1 + 2.2 + 2.4 → 2.5:** la prueba de fuego de seguridad (2.5) ejercita de extremo a punta el Sandbox, el Gateway y `ITestRunner` con un test malicioso dentro del repositorio objetivo.
- **Con la Épica 5 (AD-15):** el registro de `usage` que captura el Gateway en `:8080/llm` en la 2.2 se conectará con `IBudgetLedger.Charge` y el corte `402 codenetix:budget_exhausted` en las historias 5.1 y 5.2.
