# Revisión de verificación: ARCHITECTURE-SPINE.md (CodeNetix v1)

- **Fecha:** 2026-10-27
- **Lente:** comprobar que cada decisión se verificó en la web, en el proyecto o en el starter, y no se dio por buena a partir de datos de entrenamiento: versiones vigentes, tecnologías que siguen existiendo y encajan, y comportamientos por defecto actuales.
- **Alcance:** las afirmaciones que **no** están en la tabla Stack, porque esas versiones ya se comprobaron con `npm view` y NuGet según `.memlog.md`. Aun así, la tabla Stack tiene un hallazgo (H-3).
- **Veredicto:** la base tecnológica existe y encaja, pero varias afirmaciones operativas de AD-5, AD-6 y AD-7 se escribieron de memoria y, contrastadas con la documentación actual, no funcionan tal como están redactadas. Hay que corregirlas antes de pasar a historias.

## Resumen de hallazgos

| ID | Severidad | Tema | Decisión |
| --- | --- | --- | --- |
| H-1 | **Alta** | `claude -p --output-format json` por sí solo no basta: desde la v2.1.302, `-p` arranca en modo Manual y deniega las ediciones y Bash; además, sin `--bare` carga los hooks y MCP del repositorio | AD-5 |
| H-2 | **Alta** | El montaje de `/workspace` con el socket de Docker (Docker-outside-of-Docker) resuelve las rutas en el host, no en el contenedor `codenetix` | AD-6, AD-8 |
| H-3 | **Media-Alta** | `Docker.DotNet 3.125.15` es de mayo de 2023 y no tiene versiones posteriores; el fork mantenido es `Docker.DotNet.Enhanced` (4.x) | Stack, AD-6 |
| H-4 | **Media** | El mecanismo del Egress Gateway no está fijado: con `HTTPS_PROXY` (túnel CONNECT) no se puede sustituir la key sin MITM; hay que usar `ANTHROPIC_BASE_URL` | AD-7 |
| H-5 | **Media** | Red `internal`: el Gateway solo es alcanzable si el contenedor `codenetix` se une a esa red o escucha en la IP del gateway del bridge; falta decirlo, y hay que concretarlo para el modo Action | AD-7, AD-12 |
| H-6 | **Media** | Salida estructurada con `IChatClient` en modelos locales (Ollama): depende de la versión y del modelo; no se ha comprobado | AD-5 |
| H-7 | Baja | Tráfico no esencial de Claude Code (telemetría, comprobación de actualizaciones) que el Gateway bloqueará y registrará como ruido | AD-7 |
| H-8 | Baja | Contabilidad del presupuesto en el Gateway: hay que leer el `usage` de las respuestas SSE; `--output-format json` ya da `total_cost_usd` | AD-7, FR-13 |
| OK | — | `Guid.CreateVersion7()` existe desde .NET 9 (vale en .NET 10) | Convenciones |
| OK | — | Los runners Ubuntu alojados en GitHub traen Docker (dato conocido; no se volvió a comprobar en esta sesión, ver nota) | AD-12 |

---

## H-1 — `claude -p --output-format json` no basta para que el Programador edite y ejecute [Alta]

**Afirmación (AD-5):** «`ClaudeCodeAgent`, que invoca `claude -p --output-format json`».

**Evidencia:** https://code.claude.com/docs/en/headless
- «With Claude Code v2.1.302 or later, `claude -p` runs start in Manual mode when nothing sets a permission mode.» En Manual, «a call that needs approval is denied unless something approves it». La versión fijada (2.1.311) es posterior, así que sin más flags el Programador **no podrá editar archivos ni ejecutar `dotnet build`/`dotnet test`**.
- «Without `--bare`, a `-p` session runs the hooks in a project's `.claude/settings.json` and connects the servers in its `.mcp.json`, even in a folder you've never trusted.» El repositorio objetivo es contenido no confiable (FR-27, prompt injection): sus hooks y servidores MCP se ejecutarían dentro del Sandbox.
- «`--bare` is the recommended mode for scripted and SDK calls». En modo bare, la autenticación sale de `ANTHROPIC_API_KEY` o de `apiKeyHelper`.
- Existen `--json-schema` (con salida en `structured_output`), `--max-budget-usd`, `--permission-prompts none` (desde la v2.1.259) y `total_cost_usd` en la salida JSON.

**Corrección propuesta:** fijar en AD-5 la invocación canónica, por ejemplo:
`claude --bare -p "<prompt>" --output-format json --permission-mode acceptEdits --allowedTools "Bash,Read,Edit" --permission-prompts none --max-budget-usd <restante>`.
O bien `--permission-mode bypassPermissions` / `--dangerously-skip-permissions`, justificando que el aislamiento lo da el Sandbox. Hay que decidir y escribir cuál de las dos. Añadir también una regla: «la versión del CLI fijada en la imagen se revisa contra el changelog en cada actualización, porque cambian los valores por defecto de `-p`».

## H-2 — Montaje de `/workspace` con el socket del host: las rutas son del host [Alta]

**Afirmación (AD-6 y AD-8):** el módulo GitHub clona «al directorio de trabajo que luego se monta», y el Sandbox monta el repositorio como volumen `/workspace` usando el Docker del host a través del socket montado en el contenedor `codenetix`.

**Evidencia:** con Docker-outside-of-Docker, el daemon es el del host y resuelve el origen de un bind mount en el sistema de archivos **del host**, no en el del contenedor que hace la llamada. Los contenedores creados son hermanos, no hijos. Hay casos reales que fallan así («bind source path does not exist», workspace no compartido):
- https://drqp.readthedocs.io/en/stable/Howto/issue-using-act-and-devcontainers-ci.html
- https://gitea.com/gitea/runner/pulls/810.patch
- https://issues.jenkins.io/browse/JENKINS-43222

Si `codenetix` clona en `/data/work/<runId>` dentro de su contenedor y pasa esa ruta como bind, el Sandbox recibirá un directorio vacío o la creación del contenedor fallará.

**Corrección propuesta:** fijar una de estas dos opciones en AD-6:
- (a) Un **volumen con nombre** por Ejecución (`codenetix-ws-<runId>`), montado en el contenedor `codenetix` (para clonar y hacer push) y en el Sandbox (como `/workspace`), y eliminado en el `finally`.
- (b) Montar el directorio de trabajo con **la misma ruta en host y contenedor** (`-v /var/lib/codenetix/work:/var/lib/codenetix/work`), declarada en la configuración de instancia.

En ambos casos, añadir un test de integración que lo verifique. Indicar también qué pasa en el modo Action, donde `codenetix action` puede correr directamente en el runner.

## H-3 — `Docker.DotNet 3.125.15` no se mantiene [Media-Alta]

**Afirmación (Stack):** `Docker.DotNet | 3.125.15`. La versión es la última publicada, así que `npm view`/NuGet la «confirman», pero eso no dice nada de su vigencia.

**Evidencia:**
- https://www.nuget.org/packages/Docker.DotNet: la última versión es 3.125.15, publicada el **18-05-2023**. No hay versiones posteriores; lleva más de 3 años sin publicar.
- https://www.nuget.org/packages/docker.dotnet.enhanced: es el fork mantenido por Testcontainers, con la línea 4.x (4.2.0, abril de 2026). Testcontainers 4.12.0 depende de `Docker.DotNet.Enhanced >= 4.2.0` (https://www.nuget.org/packages/testcontainers/).

**Riesgo:** compatibilidad con las versiones actuales de la API de Docker Engine y soporte de `net10.0`; además, los tests de integración probablemente usen Testcontainers, lo que mezclaría dos clientes distintos.

**Corrección propuesta:** sustituirlo por `Docker.DotNet.Enhanced` (versión actual, comprobada con NuGet) o justificar el paquete original. Registrar la comprobación en `.memlog.md`.

## H-4 — Cómo sustituye la key el Egress Gateway: `ANTHROPIC_BASE_URL`, no `HTTPS_PROXY` [Media]

**Afirmación (AD-7):** el Gateway es un «proxy HTTP» que «sustituye la key ficticia por la real».

**Evidencia:**
- https://code.claude.com/docs/en/network-config: Claude Code respeta `HTTPS_PROXY`/`HTTP_PROXY`/`NO_PROXY`, pero no SOCKS. Con un proxy HTTPS el tráfico va por túnel CONNECT y TLS va de extremo a extremo, así que **no se pueden reescribir cabeceras** sin MITM (CA propia en `NODE_EXTRA_CA_CERTS`).
- https://code.claude.com/docs/en/llm-gateway y https://code.claude.com/docs/en/llm-gateway-protocol: `ANTHROPIC_BASE_URL` apunta a un gateway que sirve `/v1/messages` (y `/v1/messages/count_tokens`, opcional) y debe reenviar `anthropic-beta` y `anthropic-version`. Claude Code envía la credencial en `x-api-key` (`ANTHROPIC_API_KEY`) o en `Authorization` (`ANTHROPIC_AUTH_TOKEN`/`apiKeyHelper`), y el gateway puede consumirla y sustituirla.

**Corrección propuesta:** escribir en AD-7 que el Gateway tiene **dos funciones**:
- (1) **Gateway LLM inverso**: `ANTHROPIC_BASE_URL=http://<gateway>:<port>` dentro del Sandbox. Reescribe `x-api-key`/`Authorization`, reenvía `anthropic-*` y hace streaming SSE sin buffer.
- (2) **Proxy de reenvío con lista de permitidos** (`HTTPS_PROXY`), solo por CONNECT y sin MITM, para los feeds de NuGet.

Fijar también `NO_PROXY` para que el tráfico al Gateway LLM no pase por el proxy.

## H-5 — Red `internal`: cómo alcanza el Sandbox al Gateway [Media]

**Afirmación (AD-7):** el Sandbox está solo en la red interna `codenetix-sandbox` y su única salida es el Gateway, que está dentro del proceso Host.

**Evidencia:** https://docs.docker.com/reference/cli/docker/network/create/: «Containers on an internal network may communicate between each other, but not with any other network… Communication with the gateway IP address (and thus appropriately configured host services) is possible».

**Implicación:** en el modo `serve`, el contenedor `codenetix` tiene que **conectarse también** a `codenetix-sandbox` (y a una red normal para su propia salida), y el Gateway tiene que escuchar en esa interfaz. En el modo `action`, si el proceso corre directamente en el runner, el Sandbox solo llega a él a través de la IP del gateway del bridge, así que el Gateway tiene que escuchar ahí. Si se usa `0.0.0.0`, conviene tener en cuenta la exposición en el runner. Nada de esto aparece en el spine, y es una fuente típica de divergencia entre modos (va contra la regla de AD-12).

**Corrección propuesta:** añadir a AD-7 cómo se conectan las redes en cada modo:
- `serve`: `docker network connect codenetix-sandbox <codenetix>`, con el Gateway escuchando en la IP de esa red.
- `action`: el Gateway escucha en la IP del gateway del bridge interno, o `codenetix action` se ejecuta también como contenedor (como sugiere el diagrama C2) y se conecta igual que en `serve`.

Añadir un test de integración que compruebe que, desde el Sandbox, `curl https://example.com` falla y que el Gateway responde.

## H-6 — Salida estructurada de `IChatClient` con modelos locales [Media]

**Afirmación (AD-5):** los Revisores usan `IChatClient` con **salida estructurada** y «admite cualquier Proveedor compatible con OpenAI, incluidos los modelos locales».

**Evidencia:**
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.chatclientstructuredoutputextensions.getresponseasync: `GetResponseAsync<T>` tiene un parámetro `useNativeJsonSchema`. Según la documentación, el esquema nativo «might cause an error if the model does not support it».
- https://learn.microsoft.com/en-us/answers/questions/2278693/structured-output-is-not-supported-in-non-openai-m: hay errores 400 en endpoints que no soportan `json_schema`.
- https://pydantic.dev/docs/ai/api/models/ollama/index.md: Ollama ≥ 0.5.0 respeta `json_schema` mediante gramática; Ollama Cloud lo acepta pero **no lo aplica**.

No se ha comprobado con Agent Framework 1.26 + `Microsoft.Extensions.AI.OpenAI` contra Ollama, LM Studio o vLLM.

**Corrección propuesta:** precisar AD-5:
- Exigir Ollama ≥ 0.5 (o un servidor equivalente con `json_schema`).
- Configurar `useNativeJsonSchema` por Proveedor y, si no hay soporte, usar el modo JSON + validación del esquema + un reintento acotado.
- Si la salida no es válida tras los reintentos, la Etapa termina en `Fallida`, con un motivo claro.
- Añadir una prueba de humo contra un modelo local en CI o en la documentación.

## H-7 — Tráfico no esencial del CLI dentro del Sandbox [Baja]

**Evidencia:** https://code.claude.com/docs/en/network-config: el CLI contacta con `api.anthropic.com` (también para feature flags y telemetría), con hosts de Datadog (telemetría opcional) y con hosts de actualización. Además, la comprobación de disponibilidad de fast mode va siempre a `api.anthropic.com`, nunca al gateway.

**Corrección propuesta:** en la imagen `codenetix-sandbox`, definir `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` y `DISABLE_TELEMETRY=1`, desactivar el autoupdater y verificar que el único destino que queda es el Gateway LLM. Así se evita que cada Ejecución llene el log de `RunEvent` de bloqueos esperados.

## H-8 — Contabilidad de tokens en el Gateway [Baja]

**Afirmación (AD-7):** «El consumo de tokens que pasa por el Gateway se contabiliza».

**Evidencia:** las respuestas de `/v1/messages?beta=true` vienen en streaming SSE (https://code.claude.com/docs/en/llm-gateway-protocol), así que el `usage` llega en los eventos `message_start`/`message_delta`. Además, `--output-format json` devuelve `total_cost_usd` y el desglose por modelo, aunque es una estimación del cliente (https://code.claude.com/docs/en/headless).

**Corrección propuesta:** decir que el Gateway analiza el SSE sin almacenarlo en buffer (para no romper los watchdogs del CLI) y que el control fuerte del presupuesto se hace con `--max-budget-usd` más el contador del Gateway como valor de referencia.

---

## Comprobaciones correctas

- **`Guid.CreateVersion7()`:** existe desde .NET 9 y está en .NET 10 (https://learn.microsoft.com/en-us/dotnet/api/system.guid.createversion7). Es correcto. Hay que tener en cuenta que el orden de UUIDv7 en SQLite depende de cómo EF Core guarde el `Guid` (TEXT o BLOB); conviene comprobarlo si los índices dependen del orden temporal.
- **Docker en runners Ubuntu de GitHub:** las imágenes `ubuntu-*` incluyen Docker Engine. No se volvió a comprobar en esta sesión por el límite de llamadas. Se recomienda citar `actions/runner-images` (README de Ubuntu) en el spine. **No** está disponible en runners macOS ni en algunos runners ARM/larger; esto hay que documentarlo en `action.yml` (`runs-on: ubuntu-*`).
- **Socket montado:** que montar el socket equivale a root en el host ya está reconocido en AD-6 y en Deferred. No hay nada que añadir aparte de H-2.

## Nota de proceso

`.memlog.md` solo registra la verificación de la tabla Stack. Se recomienda ampliarlo con las verificaciones de comportamiento (flags del CLI, mecanismo del gateway, red interna y montaje de volúmenes) y dejar en el spine una referencia breve a la fuente de cada una.
