---
title: 'Historia 2.2: Red aislada y Egress Gateway'
type: 'feature'
created: '2026-10-28'
status: 'done'
route: 'dispatch'
baseline_commit: '373b162220698f6e0e697c928e4285a80223ecc6'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-2-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** En la historia 2.1 el Sandbox arranca con `NetworkMode: none`. Para que el Programador (2.3) y `dotnet test` (2.4) funcionen sin exponer internet ni las keys reales al código del repositorio, el Sandbox debe estar en una red Docker `internal` y salir únicamente por el Egress Gateway del host (FR-27, NFR-1, NFR-2, AD-7).

**Approach:**
- El módulo Sandbox asegura la red Docker `internal` `codenetix-sandbox` (etiqueta `codenetix.managed=true`), une a ella el contenedor `codenetix` con el alias `gateway` (o mapea `gateway` a la IP del bridge cuando el host corre fuera de contenedor) y conecta cada Sandbox únicamente a esa red.
- Cada sesión de Sandbox registra en el Gateway una credencial efímera `cnx-run-<runId>-<nonce>` y recibe `ANTHROPIC_BASE_URL=http://gateway:8080/llm`, `ANTHROPIC_API_KEY=cnx-run-<runId>-<nonce>`, `HTTPS_PROXY`/`HTTP_PROXY=http://gateway:8081` y `NO_PROXY=gateway,localhost,127.0.0.1`, revocando la credencial al detenerse o liberarse.
- El Egress Gateway escucha en `:8080` (proxy inverso YARP en `/llm/**` que valida la key ficticia, la sustituye por la key real del Proveedor del Programador, retransmite SSE sin buffer y registra el `usage` asociado al `runId` como `RunEvent` `TokensConsumed`) y en `:8081` (proxy `CONNECT` que permite túneles TCP sin inspección hacia los hosts de NuGet configurados y rechaza cualquier otro destino emitiendo `RunEvent` `EgressBlocked`).

## Boundaries & Constraints

**Always:**
- **Contracts (`src/CodeNetix.Contracts/`):**
  - Nuevos `RunEvent` en la unión discriminada (`Runs.cs`):
    - `TokensConsumed(string Role, string Provider, string Model, int InputTokens, int OutputTokens)`
    - `EgressBlocked(string Target, string Reason)`
  - `ISandboxGateway` (o registro de sesión en Sandbox) mantiene las sesiones activas por `runId`, su token efímero `cnx-run-<runId>-<nonce>` y la IP del contenedor en `codenetix-sandbox`, y las da de baja en `StopAsync`/`DisposeAsync`.
- **Red aislada (`codenetix-sandbox`):**
  - Creada por `DockerClient` si no existe: `Name: "codenetix-sandbox"`, `Driver: "bridge"`, `Internal: true`, `Labels: { "codenetix.managed": "true" }`. Si ya existe pero `Internal` es `false`, la creación del Sandbox falla con motivo claro.
  - Si el proceso Host corre dentro de un contenedor Docker (detectado o configurado en `CODENETIX__Sandbox__HostContainer`), se conecta a `codenetix-sandbox` con el alias `gateway` (`POST /networks/codenetix-sandbox/connect`). Si el Host corre fuera de contenedor (p. ej. desarrollo o tests en el host), se añade `ExtraHosts: ["gateway:<ip-gateway-bridge>"]` al contenedor Sandbox a partir de `IPAM.Config` de la red.
  - El contenedor Sandbox se crea con `NetworkDisabled: false` y `HostConfig.NetworkMode = "codenetix-sandbox"`. No se une a ninguna otra red.
- **Entorno del contenedor Sandbox:**
  - `ANTHROPIC_BASE_URL=http://gateway:8080/llm`
  - `ANTHROPIC_API_KEY=cnx-run-<runId>-<nonce>` (nonce aleatorio criptográfico de 32 caracteres hexadecimales).
  - `HTTP_PROXY=http://gateway:8081`, `HTTPS_PROXY=http://gateway:8081`, `http_proxy=http://gateway:8081`, `https_proxy=http://gateway:8081`.
  - `NO_PROXY=gateway,localhost,127.0.0.1`, `no_proxy=gateway,localhost,127.0.0.1`.
  - `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`, `DISABLE_TELEMETRY=1`, `DISABLE_AUTOUPDATER=1`.
- **Gateway LLM (`:8080`, YARP `Yarp.ReverseProxy` 2.3.0):**
  - Solo acepta rutas bajo `/llm/{**rest}`; cualquier otra ruta en `:8080` devuelve `404` (`ProblemDetails`), y en `:5000`/`:5001` nunca se expone `/llm`.
  - Extrae el token `cnx-run-<runId>-<nonce>` de `x-api-key` o `Authorization: Bearer` (comparación en tiempo constante). Si falta, no corresponde a una sesión activa o la sesión ya se detuvo → `401`.
  - Sustituye la credencial ficticia por la key real del Proveedor del Programador (`CODENETIX__Agents__Coder__Provider`, por defecto el único con key o `Anthropic`), reenviando a la `BaseUrl` del Proveedor sin duplicar `/v1` si la base ya termina en `/v1/`.
  - Desactiva el buffering de respuesta (`IHttpResponseBodyFeature.DisableBuffering`) y retransmite chunks SSE inmediatamente al Sandbox mientras extrae `model` y `usage` (`input_tokens` / `prompt_tokens`, `output_tokens` / `completion_tokens`, incluidos `message_start` y `message_delta` en SSE o el JSON de respuesta) y emite `TokensConsumed` asociado al `runId` mediante `IRunEventSink`.
- **Proxy `CONNECT` para NuGet (`:8081`):**
  - Escucha en el puerto `8081` (configurable con `CODENETIX__Sandbox__ConnectProxyPort`, `0` para puerto efímero en pruebas).
  - Lista de hosts permitidos configurable en `CODENETIX__Sandbox__AllowedNuGetHosts` (por defecto `api.nuget.org:443`).
  - Si la petición es `CONNECT <host>:<port>` y está en la lista permitida: abre conexión TCP al destino, devuelve `HTTP/1.1 200 Connection Established\r\n\r\n` y copia ambos sentidos en crudo sin inspeccionar TLS.
  - Si el destino no está en la lista permitida o el método no es `CONNECT`: responde `403 Forbidden`, identifica el `runId` (por `Proxy-Authorization`, IP de origen del contenedor en `codenetix-sandbox` o única sesión activa) y emite `EgressBlocked(target, reason)` vía `IRunEventSink`.

**Never:**
- Montar o inyectar la key real del Proveedor o el token de GitHub en el Sandbox.
- Conectar el Sandbox a la red `bridge` por defecto o a una red con salida a internet (`Internal: false`).
- Almacenar en buffer completo el flujo SSE antes de entregarlo al Sandbox.
- Hacer MITM TLS en `:8081` ni permitir túneles `CONNECT` a puertos o dominios fuera de la lista permitida.
- Cortar por presupuesto con `402` (eso pertenece a `IBudgetLedger` en las historias 5.1 y 5.2).
- Invocar el CLI de Claude Code (2.3) o ejecutar `ITestRunner` (2.4).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Creación en red aislada | `CreateForRunAsync(runId)` | Red `codenetix-sandbox` (`Internal: true`), contenedor en esa red con `ANTHROPIC_BASE_URL=http://gateway:8080/llm` y `ANTHROPIC_API_KEY=cnx-run-<runId>-<nonce>` | Si la red existe con `Internal: false`, falla con `SandboxCreationException` |
| Petición LLM válida (SSE) | `POST http://gateway:8080/llm/v1/messages` con `x-api-key: cnx-run-<runId>-<nonce>` | El Proveedor recibe la key real (nunca la ficticia), los eventos SSE llegan al instante sin buffer y al terminar se registra `TokensConsumed` en `runId` | Si el Proveedor devuelve 4xx/5xx, se retransmite tal cual sin exponer la key real |
| Key ficticia inválida o sesión cerrada | Petición a `:8080/llm/...` sin key, con key inventada o tras `DisposeAsync` | `401 Unauthorized` sin contactar al Proveedor | — |
| Otra ruta en `:8080` | `GET http://gateway:8080/api/health` o `/` | `404 Not Found` (superficie mínima, AD-7) | — |
| `CONNECT` a NuGet permitido | `CONNECT api.nuget.org:443 HTTP/1.1` en `:8081` | `200 Connection Established` y túnel TCP bidireccional sin inspección TLS | Si el destino no conecta, `502 Bad Gateway` |
| `CONNECT` o HTTP a destino prohibido | `CONNECT example.com:443` o `GET http://example.com/` en `:8081` | `403 Forbidden` y se emite `RunEvent` `EgressBlocked` asociado al `runId` | — |
| Salida directa desde el Sandbox | Proceso en el Sandbox intenta abrir socket directo a IP externa (`1.1.1.1:443`) | Falla a nivel de red Docker (`internal: true`, sin ruta a internet) | — |

</frozen-after-approval>

## Code Map

- `Directory.Packages.props` y `src/CodeNetix.Sandbox/CodeNetix.Sandbox.csproj` -- añadir `Yarp.ReverseProxy` (`2.3.0`) y `FrameworkReference` a `Microsoft.AspNetCore.App` en `CodeNetix.Sandbox`.
- `src/CodeNetix.Contracts/Runs.cs` -- añadir `TokensConsumed` y `EgressBlocked` a `[JsonPolymorphic]` de `RunEvent`.
- `src/CodeNetix.Sandbox/Docker/DockerClient.cs` -- añadir operaciones de red de la API de Docker (`InspectNetworkAsync`, `CreateNetworkAsync`, `ConnectNetworkAsync`).
- `src/CodeNetix.Sandbox/SandboxOptions.cs` -- opciones de red (`NetworkName`, `HostContainer`), puertos del Gateway (`LlmGatewayPort = 8080`, `ConnectProxyPort = 8081`) y `AllowedNuGetHosts` (`["api.nuget.org:443"]`).
- `src/CodeNetix.Sandbox/SandboxManager.cs` y `SandboxSession.cs` -- asegurar `codenetix-sandbox` (`Internal: true`), conectar contenedor host o inyectar `ExtraHosts` `gateway:<ip>`, configurar `NetworkMode = "codenetix-sandbox"`, variables `ANTHROPIC_BASE_URL`, `ANTHROPIC_API_KEY=cnx-run-<runId>-<nonce>`, `HTTPS_PROXY`/`HTTP_PROXY`/`NO_PROXY`, y registrar/revocar la sesión en el Gateway.
- `src/CodeNetix.Sandbox/Gateway/` -- registro de sesiones activas (`SandboxGatewayRegistry`), proxy inverso YARP para `/llm/{**rest}` con extracción de `usage` en streaming SSE (`LlmReverseProxy`), y servidor `CONNECT` con allowlist y emisión de `EgressBlocked` (`NuGetConnectProxy`).
- `src/CodeNetix.Host/ServeMode.cs`, `ReceiveWebhookEndpoint.cs` (`ListenPorts`), `InstanceOptions.cs`, `AgentEndpointResolver.cs` -- escuchar `:8080` en Kestrel con guardia de puerto (solo `/llm/**` en `:8080`), resolver el endpoint del Proveedor `Coder`, y arrancar el proxy `CONNECT` en `:8081`.
- `tests/CodeNetix.Host.Tests/` -- actualizar `FakeDockerEngine` y `SandboxModuleTests` para la red `codenetix-sandbox` y variables del Gateway; crear `EgressGatewayTests` (sustitución de key con YARP, streaming SSE sin buffer, contabilidad `TokensConsumed`, rechazo 401/404, túnel `CONNECT` permitido a NuGet y bloqueo `403` + `EgressBlocked`) y ampliar `SandboxIntegrationTests` con verificación en Docker real.

## Tasks & Acceptance

**Execution:**
- [x] `Directory.Packages.props` y `src/CodeNetix.Sandbox/CodeNetix.Sandbox.csproj` -- registrar `Yarp.ReverseProxy` 2.3.0 y `Microsoft.AspNetCore.App` en Sandbox -- requerido por AD-7.
- [x] `src/CodeNetix.Contracts/Runs.cs` -- añadir los eventos `TokensConsumed` y `EgressBlocked` a la unión discriminada `RunEvent` -- requeridos para registrar el uso de tokens y los bloqueos de salida (AD-3, AD-7, FR-27).
- [x] `src/CodeNetix.Sandbox/Docker/DockerClient.cs` -- implementar inspección, creación (`Internal: true`) y conexión a la red `codenetix-sandbox` con el alias `gateway` -- necesario para aislar el contenedor en la red interna.
- [x] `src/CodeNetix.Sandbox/Gateway/` -- implementar `SandboxGatewayRegistry`, `LlmReverseProxy` (YARP + lector de `usage` SSE sin buffer) y `NuGetConnectProxy` (túnel `CONNECT` con lista blanca y emisión de `EgressBlocked`) -- núcleo del Egress Gateway (AD-7).
- [x] `src/CodeNetix.Sandbox/SandboxOptions.cs`, `SandboxManager.cs` y `SandboxSession.cs` -- cambiar `NetworkMode` a `codenetix-sandbox`, generar y revocar `cnx-run-<runId>-<nonce>` e inyectar `ANTHROPIC_BASE_URL`, `ANTHROPIC_API_KEY`, `HTTPS_PROXY` y `NO_PROXY` -- conecta cada Sandbox con el Gateway.
- [x] `src/CodeNetix.Host/` -- resolver el Proveedor del rol `Coder` en `AgentEndpointResolver`, aislar el puerto `:8080` para `/llm/**` en Kestrel y enlazar las opciones e inicio del Gateway en `ServeMode` -- integra el Gateway en el proceso Host sin exponer otros endpoints.
- [x] `tests/CodeNetix.Host.Tests/` -- actualizar `FakeDockerEngine`, `SandboxModuleTests` y `SandboxIntegrationTests`, y añadir `EgressGatewayTests` cubriendo toda la matriz I/O -- verifica unitaria e integralmente el aislamiento de red y el Gateway.
- [x] `README.md` -- actualizar la tabla de aislamiento y variables del Sandbox con la red `codenetix-sandbox` y el Egress Gateway -- mantiene la documentación sincronizada con el comportamiento real.

**Acceptance Criteria:**
- Given el host en marcha, when se crea un Sandbox con `CreateForRunAsync`, then el contenedor solo está unido a la red `internal` `codenetix-sandbox`, alcanza al host bajo el nombre `gateway` y tiene `ANTHROPIC_BASE_URL=http://gateway:8080/llm` y `ANTHROPIC_API_KEY=cnx-run-<runId>-<nonce>`.
- Given una petición desde el Sandbox a `http://gateway:8080/llm/v1/messages` con `cnx-run-<runId>-<nonce>`, when el Gateway (YARP) la reenvía al Proveedor, then sustituye la key ficticia por la real, retransmite el flujo SSE sin buffer y emite `TokensConsumed` asociado al `runId`.
- Given una petición `CONNECT` a `gateway:8081`, when el destino es un feed de NuGet configurado, then el túnel TCP se establece sin inspeccionar el contenido; y cuando es cualquier otro destino, se rechaza con `403` y se registra `EgressBlocked` en los `RunEvent` de la Ejecución.

## Implementation Notes

- Se incorporó `Yarp.ReverseProxy` (`2.3.0`) y `Microsoft.AspNetCore.App` en `CodeNetix.Sandbox`, eliminando referencias explícitas redundantes a `Microsoft.Extensions.*` para evitar advertencias `NU1510`.
- `SandboxManager` garantiza la red Docker `codenetix-sandbox` (`Internal: true`), conecta el contenedor host con alias `gateway` cuando `SandboxOptions.HostContainer` está definido o inyecta `ExtraHosts: ["gateway:<ip>"]` a partir del `Gateway` de IPAM en ejecución nativa, registra el token `cnx-run-<runId>-<nonce>` y la IP asignada en `ISandboxGateway` al arrancar y lo revoca siempre en `DisposeAsync`.
- `LlmReverseProxy` usa `IHttpForwarder` (YARP) en `/llm/{**rest}` (protegido por guardia de puerto en `:8080`), autentica tokens de sesión en tiempo constante, sustituye la credencial ficticia por la API key real (`x-api-key` y `Authorization: Bearer`), deduplica `/v1` si el `BaseUrl` del Proveedor ya termina en `/v1`, redacta cualquier aparición accidental de la API key real en cuerpos de error `4xx/5xx` y extrae `input_tokens`/`output_tokens` al vuelo desde streams SSE (`message_start`/`message_delta` y `usage` estilo OpenAI) o JSON para emitir `RunEvent.TokensConsumed`.
- `NuGetConnectProxy` escucha conexiones TCP `CONNECT` en `:8081`, valida `host:port` contra `AllowedNuGetHosts` (`["api.nuget.org:443"]` por defecto), abre túnel TCP directo sin inspección TLS cuando está permitido y responde `403 Forbidden` emitiendo `RunEvent.EgressBlocked` (atribuido al `runId` por IP del contenedor en `ISandboxGateway`) ante cualquier otro destino o método HTTP.

## Spec Change Log

## Review Triage Log

| ID | Layer | Location | Verdict | Route | Evidence |
|---|---|---|---|---|---|
| ECH-1 | edge-case-hunter | `src/CodeNetix.Host/ServeMode.cs:40` | `medium` | `patch` | `ServeMode.RunAsync` llama a `UseUrls(ListenPorts.Urls)` antes de cargar la configuración de instancia, fijando `:8080` aunque `CODENETIX__Sandbox__LlmGatewayPort` configure otro puerto. |
| ECH-2 | edge-case-hunter | `src/CodeNetix.Host/Configuration/InstanceOptionsValidator.cs:109-118` | `medium` | `patch` | `Validate` omite `ResolveCoder` cuando `Coder.Provider` no está definido explícitamente; si hay varios Proveedores sin `Anthropic` y `Analyst.Provider` sí está definido, la instancia arranca en `Ready` con `Coder = null`. |
| ECH-3 | edge-case-hunter | `src/CodeNetix.Host/Configuration/AgentEndpointResolver.cs:38-44` | `medium` | `patch` | `ResolveCoder` pasa un `fallbackModel` no nulo incluso cuando `Coder.Provider` nombra un Proveedor personalizado sin modelo por defecto, impidiendo que salte la validación de falta de `CODENETIX__Agents__Coder__Model`. |
| ECH-4 | edge-case-hunter | `src/CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs:259-275` | `medium` | `patch` | En `TryReadRequestModelAsync`, `request.ContentLength is <= 0 or > 512 * 1024` es `false` cuando `ContentLength` es `null` (transferencia chunked), copiando cuerpos chunked sin límite en memoria. |
| ECH-5 | edge-case-hunter | `src/CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs:402-411` | `medium` | `patch` | `LlmUsageTracker.InspectUsage` recalcula `_inputTokens = input + cacheCreate + cacheRead` en cada evento SSE con `usage`, sobrescribiendo con `0` los tokens de caché reportados en `message_start` cuando `message_delta` no los repite. |
| ECH-6 | edge-case-hunter | `src/CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs:331-334` | `low` | `patch` | `TransformResponseAsync` solo limpia `Content-Length` en códigos `>= 400`; limpiarlo siempre evita discrepancias de longitud en Kestrel al retransmitir o redactar respuestas `2xx`. |
| ECH-7 | edge-case-hunter | `src/CodeNetix.Sandbox/Gateway/NuGetConnectProxy.cs:62-68` | `low` | `patch` | Cuando `ConnectProxyPort` es `0` (o usa un puerto efímero de respaldo), `BoundPort` no actualiza `SandboxOptions.ConnectProxyPort`, por lo que `SandboxManager` sigue inyectando `:8081`. |
| ECH-8 | edge-case-hunter | `src/CodeNetix.Sandbox/Docker/DockerClient.cs:138-149` | `medium` | `patch` | Dos llamadas concurrentes a `EnsureNetworkAsync` cuando la red aún no existe pueden recibir `409 Conflict` en `CreateNetworkAsync` y abortar la creación del Sandbox. |
| BH-1 | blind-hunter | `src/CodeNetix.Host/ServeMode.cs:40` | `medium` | `patch` | Misma causa raíz que `ECH-1`: Kestrel fija `:8080` antes de leer `CODENETIX__Sandbox__LlmGatewayPort`. |
| BH-2 | blind-hunter | `src/CodeNetix.Host/Configuration/InstanceOptionsValidator.cs:109-118`, `AgentEndpointResolver.cs:38-44` | `medium` | `patch` | Mismas causas raíz que `ECH-2` y `ECH-3`: validación de `Coder` omitida cuando `Coder.Provider` está vacío y `fallbackModel` no nulo en Proveedores personalizados. |
| BH-3 | blind-hunter | `src/CodeNetix.Host/Configuration/InstanceOptionsValidator.cs:80-105` | `low` | `reject` | Hallazgo cosmético/menor sobre validaciones adicionales de puertos en `InstanceOptionsValidator` que requerirían añadir nuevas constantes y ramas fuera de los requisitos de la especificación. |
| BH-4 | blind-hunter | `src/CodeNetix.Sandbox/Gateway/NuGetConnectProxy.cs:61-67` | `low` | `patch` | Misma causa raíz que `ECH-7`: sincronizar `SandboxOptions.ConnectProxyPort` con `BoundPort` al iniciar el listener. |
| BH-5 | blind-hunter | `src/CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs:402-411` | `medium` | `patch` | Misma causa raíz que `ECH-5`: conservar contadores individuales de `input_tokens`, `cache_creation_input_tokens` y `cache_read_input_tokens` entre eventos SSE. |
| BH-6 | blind-hunter | `src/CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs:259-296` | `medium` | `patch` | Misma causa raíz que `ECH-4` en lectura acotada del cuerpo de petición, más descarte limpio de líneas SSE que superen `MaxLineBytes` hasta el siguiente salto de línea. |
| BH-7 | blind-hunter | `src/CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs:88-133`, `ReceiveWebhookEndpoint.cs:35` | `medium` | `patch` | Rutas como `/llm///` o con segmentos `..` superan `llmRest.Value.Length > 1` y permiten alcanzar la raíz o salir del prefijo de `BaseUrl`; se bloquean exigiendo subruta efectiva sin segmentos `..`. |
| BH-8 | blind-hunter | `src/CodeNetix.Sandbox/Gateway/LlmReverseProxy.cs:377-407` | `low` | `reject` | En respuestas `2xx` SSE el Proveedor no devuelve la API key como texto dividido entre chunks, y retener chunks en buffer violaría la restricción de retransmisión SSE sin buffering de `<frozen-after-approval>`. |
| BH-9 | blind-hunter | `src/CodeNetix.Sandbox/Gateway/SandboxGatewayRegistry.cs:43-55,107-202` | `medium` | `patch` | `UpdateContainerIp` con `AddOrUpdate` puede recrear una sesión revocada, `ResolveFromProxyAuthorization` aceptaba GUIDs sin autenticar el token, y el fallback de sesión única en `ResolveRunId` se aplicaba aunque la sesión tuviera otra `ContainerIp` distinta de loopback. |
| BH-10 | blind-hunter | `src/CodeNetix.Sandbox/Docker/DockerClient.cs:138-149` | `medium` | `patch` | Misma causa raíz que `ECH-8`: tolerar `409 Conflict` (`already exists`) en `CreateNetworkAsync`. |
| VG-1 | verification-gap | `tests/CodeNetix.Host.Tests/EgressGatewayTests.cs:50-267,606-662` | `medium` | `patch` | `FakeLlmServer` no capturaba el cuerpo de la petición tras `TryReadRequestModelAsync` y todas las respuestas de prueba incluían `"model"`, dejando sin verificar el rebobinado del stream y el fallback al modelo de la petición. |
| VG-2 | verification-gap | `src/CodeNetix.Host/ServeMode.cs:73-77`, `tests/CodeNetix.Host.Tests/EgressGatewayTests.cs:374-476` | `medium` | `patch` | `ServeMode.ReadConfiguredHosts` (`CODENETIX:Sandbox:AllowedNuGetHosts`) y la persistencia polimórfica de `EgressBlocked` en `IRunEventSink`/`IRunReader` no estaban cubiertos por un test sobre `CodeNetixFactory`. |
| VG-3 | verification-gap | `src/CodeNetix.Sandbox/Gateway/SandboxGatewayRegistry.cs:107-137`, `tests/CodeNetix.Host.Tests/SandboxModuleTests.cs:186` | `medium` | `patch` | Las pruebas de `ResolveRunId` registraban una única sesión (`_sessions.Values.Count == 1`), enmascarando la resolución por `ContainerIp` y por `Proxy-Authorization`. |
| VG-Other-1 | verification-gap | `src/CodeNetix.Host/Configuration/InstanceOptionsValidator.cs:109-118` | `medium` | `patch` | Misma causa raíz que `ECH-2`. |
| VG-Other-2 | verification-gap | `src/CodeNetix.Host/Configuration/AgentEndpointResolver.cs:38-45` | `medium` | `patch` | Misma causa raíz que `ECH-3`. |

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todos los tests unitarios, de arquitectura y de Gateway en verde.

