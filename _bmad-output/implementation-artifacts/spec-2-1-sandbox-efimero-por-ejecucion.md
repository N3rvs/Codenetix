---
title: 'Historia 2.1: Sandbox efímero por Ejecución'
type: 'feature'
created: '2026-10-28'
status: 'done'
route: 'dispatch'
baseline_commit: '2dd2b075b171d14336d347dce536aafb0840e59c'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-1-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** No existe ningún lugar aislado donde ejecutar el código del repositorio. El Programador y los tests (2.3 y 2.4) necesitan un contenedor desechable por Ejecución, sin `.git`, con límites y que se destruya siempre (FR-11, NFR-1, NFR-4, AD-6, AD-16 paso 2).

**Approach:**
- El módulo Sandbox habla con el motor Docker del host por su API HTTP (socket Unix o *named pipe*), sin CLI y sin *bind mounts*.
- `ISandboxManager.CreateForRun` crea el volumen `codenetix-ws-<runId>` y un contenedor `codenetix-sandbox` con límites y la etiqueta, y sube el árbol (`git archive` del `BaseSha`, sin `.git`) con la API de archivos.
- `ISandboxSession` permite ejecutar comandos, se detiene en 10 s como máximo al cancelarse y lo destruye todo al liberarse.
- El orquestador la crea tras el clonado y la libera en el `finally`.

## Boundaries & Constraints

**Always:**
- **Contracts:**
  - `ISandboxManager.CreateForRunAsync(runId, ct)` → `ISandboxSession`.
  - `ISandboxSession` (`IAsyncDisposable`) expone:
    - `RunId` y `ContainerId`;
    - `ExecAsync(SandboxCommand, ct)` → `{ ExitCode, StdOut, StdErr, TimedOut, Truncated }`. `SandboxCommand` contiene el ejecutable, los argumentos, el directorio de trabajo (por defecto `/workspace`), las variables de entorno y el timeout. La salida se limita a 1 MiB por flujo.
    - `StopAsync()`.
  - `IRepositoryWorkspace.ExportTreeAsync(runId, Stream destino, ct)` (GitHub) escribe `git archive --format=tar <BaseSha>`: solo los archivos versionados y nunca `.git`.
- **Motor Docker (Sandbox):**
  - Un cliente interno mínimo sobre `HttpClient` contra la API v1.43 o superior. No se añade Docker.DotNet.
  - Endpoint: `CODENETIX__Sandbox__DockerEndpoint`, `DOCKER_HOST` o, por defecto, `unix:///var/run/docker.sock` (`npipe://./pipe/docker_engine` en Windows).
  - Se usa la API para volúmenes (crear y borrar), contenedores (crear, iniciar, parar, matar, borrar, inspeccionar), `PUT /containers/{id}/archive` y exec (crear, iniciar con flujo multiplexado, inspeccionar). El flujo multiplexado se separa en stdout y stderr.
- **Imagen `docker/sandbox.Dockerfile`:**
  - Base `mcr.microsoft.com/dotnet/sdk:10.0` más Node 24 LTS.
  - Usuario no root `sandbox` (uid 1000) y `WORKDIR /workspace`.
  - **Sin** `@anthropic-ai/claude-code`.
  - Nombre configurable con `CODENETIX__Sandbox__Image` (por defecto `codenetix-sandbox:latest`).
- **Contenedor por Ejecución:**
  - Nombre `codenetix-sbx-<shortId>`, etiquetas `codenetix.run=<runId>` y `codenetix.managed=true`.
  - Volumen `codenetix-ws-<runId>` (con la misma etiqueta) montado en `/workspace`, y volumen `codenetix-tools` (se crea si no existe) montado **en solo lectura** en `/tools`.
  - Nunca un *bind mount*.
  - `NetworkMode: none`: la red aislada y el Gateway llegan en la 2.2.
  - Límites:
    - `CODENETIX__Sandbox__Cpus` (2), `__MemoryMb` (4096) y `__PidsLimit` (512);
    - `CapDrop: ALL`, `no-new-privileges`, `ReadonlyRootfs` con `tmpfs` en `/tmp` y `HOME=/tmp/home`, y `NUGET_PACKAGES=/tmp/nuget`;
    - usuario `1000:1000`.
  - Arranca con `sleep infinity` y los comandos se ejecutan por exec.
  - Antes del `start`, se sube el tar del árbol a `/workspace`.
  - Vida máxima de `CODENETIX__Sandbox__MaxLifetimeMinutes` (60): al vencer, la sesión lo detiene.
- **Parada y liberación:**
  - Al cancelarse el token de la Ejecución (o con `StopAsync`), `POST /containers/{id}/stop?t=10`; si sigue vivo, `kill`. Detenido en 10 s como máximo.
  - `DisposeAsync`: parar, borrar el contenedor (`force`) y el volumen de trabajo. Es idempotente, no lanza excepciones y registra los fallos en el log. `codenetix-tools` no se borra.
- **Orquestador:**
  - Tras resolver la configuración: `CreateForRunAsync` → `RunEvent` `SandboxCreated { image }`.
  - Si falla → `Fail(StageError, "no se pudo crear el Sandbox: <motivo>")`.
  - El resto del flujo de la 1.8/1.9 sigue igual.
  - En el `finally` de la Ejecución, la sesión se libera antes de borrar el árbol de trabajo y se emite `SandboxReleased`.
- **Salud:** al arrancar `serve`, si Docker no responde (`/_ping`) o falta la imagen → modo `Diagnostic` con el motivo (por ejemplo, "falta la imagen codenetix-sandbox: constrúyela con docker build -f docker/sandbox.Dockerfile -t codenetix-sandbox .").
- **Despliegue:** el contenedor `codenetix` necesita el socket de Docker. Documentarlo en el README (`-v /var/run/docker.sock:/var/run/docker.sock`).

**Never:**
- *Bind mounts*.
- `.git`, el token de GitHub o las keys de los Proveedores dentro del Sandbox (ni en el entorno ni en el árbol).
- Contenedores `--privileged`.
- Red con salida: en la 2.1 es `none`.
- Instalar el CLI de Claude Code (2.3).
- Egress Gateway (2.2).
- Ejecutar el Programador o los tests (2.3 y 2.4).
- Descargar el árbol de vuelta (2.4).
- Limpieza de huérfanos al arrancar (5.3).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Creación | Ejecución tras el clonado | Volumen y contenedor con etiqueta, límites, red `none` y `/workspace` con los archivos del `BaseSha` y sin `.git` | — |
| Exec | `dotnet --version` en la sesión | Código 0 y la versión en stdout | — |
| Salida enorme | Comando con más de 1 MiB de salida | Recortada a 1 MiB con `Truncated=true` | — |
| Timeout de exec | Comando que no termina en el timeout | `TimedOut=true`; la sesión sigue usable | — |
| Cancelación | El token se cancela con un exec en curso | Contenedor detenido en ≤ 10 s | `kill` si `stop` no basta |
| Liberación | La Ejecución termina en cualquier Estado | Contenedor y volumen `codenetix-ws-<runId>` borrados; `codenetix-tools` se conserva | Fallos registrados, nunca lanzados |
| Sin bind | Inspección del contenedor | `HostConfig.Binds` vacío; solo montajes de tipo `volume` | — |
| Docker caído | El socket no responde al arrancar | `Diagnostic` con el motivo | — |
| Fallo al crear | Docker devuelve 500 o falta la imagen | `Failed(StageError, "no se pudo crear el Sandbox: …")` | Lo creado a medias se borra |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Sandbox/` -- proyecto vacío; solo referencia Contracts. Aquí van el cliente de Docker, el gestor y la sesión.
- `src/CodeNetix.GitHub/RepositoryWorkspace.cs` -- clon, `BaseSha` y ejecución de git con el entorno saneado (1.7); añadir `ExportTreeAsync`.
- `src/CodeNetix.Pipeline/Features/RunPipeline/RunOrchestrator.cs` -- pasos 1 y 3 de AD-16 y el `finally` que borra el árbol.
- `src/CodeNetix.Host/` -- composición, opciones de instancia y comprobaciones de salud (`InstanceOptionsValidator` y el flujo `Diagnostic` de 1.2).
- `docker/` -- `codenetix.Dockerfile` (no tocar salvo necesidad); nuevo `sandbox.Dockerfile`.
- `tests/CodeNetix.Host.Tests/CodeNetixFactory.cs` -- por defecto un `ISandboxManager` falso, para que ningún test existente necesite Docker.

## Tasks & Acceptance

**Execution:**
- [x] `docker/sandbox.Dockerfile` -- la imagen del Sandbox.
- [x] `src/CodeNetix.Contracts/` -- `ISandboxManager`, `ISandboxSession`, `SandboxCommand`/`SandboxExecResult` y los `RunEvent` `SandboxCreated` y `SandboxReleased`.
- [x] `src/CodeNetix.GitHub/` -- `ExportTreeAsync`.
- [x] `src/CodeNetix.Sandbox/` -- cliente de Docker (unix y npipe), gestor, sesión (exec con demultiplexado, límites, parada y liberación) y comprobación de salud.
- [x] `src/CodeNetix.Pipeline/` -- crear y liberar la sesión en el orquestador.
- [x] `src/CodeNetix.Host/` -- opciones `Sandbox`, `Diagnostic` si Docker o la imagen no están, y composición.
- [x] `tests/` -- tests unitarios del cliente contra un servidor HTTP falso (forma de las peticiones: etiquetas, límites, sin binds, red `none`, demultiplexado) y tests de integración contra el Docker real, que se saltan si Docker no está disponible (creación sin `.git`, exec, timeout, salida recortada, cancelación en ≤ 10 s y liberación).
- [x] `README.md` -- el socket de Docker y la construcción de la imagen del Sandbox.

**Acceptance Criteria:**
- Given la imagen `codenetix-sandbox`, when el orquestador llama a `CreateForRunAsync` tras el clonado, then existe el volumen `codenetix-ws-<runId>` montado en `/workspace` sin `.git`, el contenedor tiene la etiqueta `codenetix.run=<runId>` y límites de CPU, memoria y PIDs, y no hay ningún *bind mount*.
- Given una Ejecución que termina en cualquier Estado, when se ejecuta el `finally`, then el contenedor y el volumen se destruyen.
- Given que se cancela el token, when la sesión lo observa, then el contenedor se detiene en 10 s como máximo.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde (los de integración se ejecutan con Docker Desktop disponible).
- `docker build -f docker/sandbox.Dockerfile -t codenetix-sandbox .` -- expected: imagen construida.
- `docker build -f docker/codenetix.Dockerfile -t codenetix .` -- expected: tests de Linux en verde (los de integración se saltan sin Docker).

## Implementation Notes

**Decisiones**
- **Contrato:** `ISandboxSession` expone además `Image` (para `SandboxCreated { image }` sin que el orquestador lea opciones del módulo Sandbox). Se añaden `SandboxCreationException` (el orquestador la traduce a `Fail(StageError, "no se pudo crear el Sandbox: <motivo>")`) y `SandboxUnavailableException` (exec sobre un Sandbox detenido o con Docker caído). `SandboxCommand` es un record con `Executable`, `Arguments`, `WorkingDirectory` (`/workspace`), `Environment` y `Timeout` (10 min por defecto); `SandboxExecResult.MaxOutputBytes` = 1 MiB.
- **Quién exporta el árbol:** `SandboxManager` depende de `IRepositoryWorkspace` (Contracts) y llama a `ExportTreeAsync`; el orquestador solo invoca `CreateForRunAsync(runId, ct)`. Sandbox sigue referenciando solo Contracts (AD-1). `ExportTreeAsync` resuelve `HEAD` (el clon no se toca tras fijar el `BaseSha`, así que es el `BaseSha`) y ejecuta `git archive --format=tar <sha>` con el mismo entorno saneado de la 1.7 (refactor de `RunGitAsync` en `StartGit`/`WaitForExitAsync` más `RunGitToStreamAsync`, que copia la salida binaria sin acumularla en memoria).
- **Tar:** se escribe a un temporal (`DeleteOnClose`) y se sube con `PUT /containers/{id}/archive?path=/workspace` antes del `start`, reescrito al vuelo (`WorkspaceArchiveContent`, `System.Formats.Tar`): `git archive` marca los archivos como root, así que se fijan a 1000:1000 (`sandbox`); se descartan la cabecera global PAX y, por defensa, cualquier entrada bajo `.git`.
- **Cliente Docker:** `HttpClient` + `SocketsHttpHandler.ConnectCallback` (socket Unix o `NamedPipeClientStream`; `tcp://` sin TLS para pruebas y hosts remotos). Versión: `/v1.43` salvo que `/version` exija una mínima mayor (los motores recientes pueden exigir ≥ 1.44); si el motor ofrece < 1.43 → `Diagnostic` ("no es compatible"). El exec se lee sin `Upgrade`: Docker devuelve el flujo multiplexado como cuerpo hasta cerrar la conexión. Un endpoint no reconocido no rompe el arranque: `DockerEndpoint.ParseOrInvalid` da un transporte `Invalid` cuyas conexiones fallan con el motivo → `Diagnostic`. `DOCKER_HOST` lo lee Host (solo Host lee el entorno) en un `PostConfigure` de `SandboxOptions`. Sin socket Unix, el motivo dice "no existe el socket …" (no el `docker:80` ficticio de `HttpClient`).
- **Contenedor:** además de lo exigido, `Init: true` (tini como PID 1: `sleep infinity` como PID 1 ignora SIGTERM y cada `stop` agotaría los 10 s), `MemorySwap = Memory` (sin swap), `NetworkDisabled`, `Binds: []`, `RestartPolicy: no`, `DOTNET_CLI_HOME=/tmp/home` (sin él `dotnet` no arranca: `/tmp/home` no existe en el tmpfs recién montado) y tmpfs `/tmp` `rw,nosuid,nodev,exec,size=2g,mode=1777`. Los restos de un intento anterior de la misma Ejecución (contenedor con el mismo nombre y la misma etiqueta `codenetix.run`, y el volumen de trabajo) se borran antes de crear; un contenedor con ese nombre de otra Ejecución hace fallar la creación.
- **Exec y timeout:** la API no permite matar un exec, así que el comando se envuelve en `timeout --kill-after=2s <N>s` (coreutils de la imagen). `TimedOut` = el flujo se ha abandonado tras timeout + 12 s, o el código es 124/137 **y** ha vencido el plazo (un 124 propio del comando antes de tiempo no cuenta). Cancelar el token de un exec en curso detiene el contenedor (la sesión deja de ser usable) y lanza `OperationCanceledException`. La salida se recorta a 1 MiB por flujo sin dejar de leer y sin partir un carácter UTF-8.
- **Parada:** `stop?t=10` (plazo HTTP de 12 s); si falla o el contenedor sigue vivo, `kill`. Idempotente (una única tarea compartida); la disparan el token de la Ejecución, la vida máxima (`MaxLifetimeMinutes`) y `StopAsync`. `DisposeAsync`: parar, `DELETE /containers/{id}?force=true` y `DELETE /volumes/codenetix-ws-<runId>?force=true`; nunca `codenetix-tools`; nunca lanza.
- **Orquestador:** tras confirmar `Analyzing` (paso 1), `CreateForRunAsync` con el token de la Ejecución → `SandboxCreated` en su propio `SaveChanges`. En el `finally`: `DisposeAsync` → `SandboxReleased` (vía `IRunEventSink`, `CancellationToken.None`; un fallo solo va al log) → borrado del árbol. Sin Sandbox (clon fallido, `codenetix.yml` inválido o fallo al crearlo) no hay `SandboxReleased`. `codenetix fix --follow` formatea `SandboxCreated <imagen>`; sigue terminando en el `StateChanged` terminal, así que no muestra el `SandboxReleased` posterior.
- **Salud:** `ISandboxHealthCheck` (público en el módulo Sandbox; lo usa Host en `StartupReconciler`): `/_ping`, versión de la API y `GET /images/{image}/json`, con 10 s como mucho en total. `InstanceOptionsValidator` valida `Image`, `Cpus` > 0, `MemoryMb` ≥ 256, `PidsLimit` ≥ 64 y `MaxLifetimeMinutes` ≥ 1.
- **Imagen:** `sdk:10.0` es Ubuntu 24.04 y trae el usuario `ubuntu` con uid 1000: el Dockerfile lo elimina antes de crear `sandbox` (1000:1000). Node 24 LTS se copia de `node:24-bookworm-slim` (binario oficial más npm y npx), sin apt ni repositorios externos.
- **README.md** no existía: se crea con la construcción de las dos imágenes, el `docker run` con `-v /var/run/docker.sock:/var/run/docker.sock` y `--group-add` (CodeNetix corre como `app`, no como root; con Docker Desktop el socket es `root:root 0660` → `--group-add 0`, comprobado), las variables `CODENETIX__Sandbox__*` y una advertencia de seguridad sobre el socket. `docker/codenetix.Dockerfile` no se toca. Comprobado a mano: la imagen `codenetix` con el socket montado y `--group-add 0` arranca `Ready`; sin el socket, `Diagnostic` con el motivo.

**Pruebas**
- `CodeNetixFactory` registra por defecto `FakeSandboxManager` y `FakeSandboxHealthCheck` (sin motivos): ningún test de Host necesita Docker ni entra en `Diagnostic` por él. Las secuencias de eventos de 1.6–1.9 incluyen ahora `SandboxCreated` y `SandboxReleased`, y los helpers de espera esperan a `SandboxReleased` cuando hubo Sandbox (es el último paso del orquestador).
- `SandboxModuleTests` (sin Docker; `FakeDockerEngine` en Kestrel por `tcp://`): forma y orden de las peticiones (etiquetas, límites, volúmenes, sin binds, red `none`, archive antes de start, tar 1000:1000 sin `.git`), demultiplexado, recorte, 124/137, validación de comandos, liberación (también con Docker devolviendo 500), cancelación, salud, versión de la API, endpoints y `ExportTreeAsync`.
- `SandboxIntegrationTests` (Docker real; se omiten con `Assert.Skip` si Docker no responde o falta la imagen; `CODENETIX_TEST_SANDBOX_IMAGE` para usar otra): creación sin `.git` y sin binds con inspección real, `dotnet --version`, stderr separado, salida > 1 MiB, timeout con la sesión usable, cancelación en ≤ 10 s, vida máxima, liberación idempotente, imagen inexistente y salud. Todo lleva `codenetix.test=<marcador>` y se borra al terminar; el fixture borra `codenetix-tools` solo si no existía antes.

**Parches de la revisión**
- **1 (abandono del exec):** los `BoundedOutput` se crean fuera del lector y lo leído sobrevive al abandono. Tras el plazo se consulta el exec: si ya no corre, se devuelve su código real con `TimedOut=false` (un nieto mantenía abierta la tubería); si sigue vivo, `TimedOut=true` y `-1`. El contenedor lleva `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0` y `UseSharedCompilation=false`. Prueba de integración: `echo hola; sleep 60 & exit 5` → 5, "hola", sin timeout.
- **2 (`.gitattributes`):** antes de `git archive` se escribe `.git/info/attributes` con `* -export-ignore -export-subst`, que prevalece sobre los del repositorio. Prueba con `/tests export-ignore` y `export-subst`: viajan intactos.
- **3 (tar):** se descartan las rutas absolutas o con `..`, las entradas bajo un enlace simbólico ya visto, los enlaces absolutos o que salen de la raíz y los tipos especiales. Un enlace duro hace fallar la subida (`InvalidDataException` → "el árbol de trabajo no es válido: …"). Prueba con un tar construido a mano.
- **4 (creación):** `CreateForRunAsync` captura cualquier excepción que no sea la cancelación de la Ejecución, borra lo creado y la envuelve en `SandboxCreationException` (las inesperadas, como "error inesperado (Tipo): …"). `MaxLifetimeMinutes` fuera de 1–1440 → `Diagnostic`.
- **12 (raíz escribible):** el tar empieza con una entrada `./` (directorio 0755 de 1000:1000). La prueba de integración hace `touch /workspace/x && mkdir /workspace/d` → código 0, con `/workspace` de 1000:1000.
- **5:** si el flujo termina porque la sesión se está deteniendo (vida máxima o `StopAsync`), el exec lanza `SandboxUnavailableException` en lugar de devolver un resultado.
- **7:** `ValidateCommand` rechaza un timeout ≤ 0 o > 24 h.
- **8:** se inserta `--` en el envoltorio, pero **antes de la duración** (`timeout --kill-after=2s -- <N>s <exe> …`). GNU `timeout` solo admite `--` como fin de opciones antes de `DURATION`: detrás, lo toma como el comando (comprobado: "failed to run command '--'", código 127). Todo lo que sigue a la duración ya es el comando y sus argumentos.
- **9:** la comprobación de salud trata cualquier fallo que no sea la cancelación del arranque (también `JsonException`) como Docker inaccesible → `Diagnostic`. "No es compatible" queda solo para la versión de API insuficiente.
- **10:** en el flujo multiplexado, el tipo 3 (`systemerr`) se acumula (4 KiB como mucho) y lanza `SandboxUnavailableException` con su texto al terminar de leer; los tipos desconocidos se descartan y el 0 sigue contando como stdout.

**Riesgos y pendientes**
- Montar el socket de Docker da al contenedor `codenetix` control del host (documentado en el README).
- Sin limpieza de huérfanos hasta la 5.3: si el proceso cae, quedan el contenedor y el volumen `codenetix-ws-*` (reanudar la misma Ejecución sí los sustituye).
- El exec sin `Upgrade` depende de que Docker cierre la conexión al terminar el comando. Comprobado con Docker Desktop 4.92 / Engine 29.8 por *named pipe* (Windows) y por socket Unix (las 9 pruebas de integración, a mano, en un contenedor `sdk:10.0` con el socket montado); en la etapa `sdk` del `docker build` se omiten porque no hay Docker.
- El tmpfs de `/tmp` (2 GiB como mucho) cuenta dentro del límite de memoria del contenedor.

## Spec Change Log

## Review Triage Log

Revisión 1 (revisor combinado, 15 hallazgos):

| # | Sev. | Hallazgo | Decisión |
|---|------|----------|----------|
| 1 | medium | Exec abandonado pierde la salida (procesos de MSBuild mantienen el flujo) | patch |
| 2 | medium | `git archive` respeta `export-ignore`/`export-subst` del repositorio | patch |
| 3 | medium | Symlinks absolutos o fuera del árbol en el tar | patch |
| 4 | medium | Excepciones no previstas tras crear el contenedor no limpian; `MaxLifetimeMinutes` sin tope | patch |
| 5 | low | Exec en curso al parar devuelve resultado normal | patch (1 línea) |
| 6 | low | `TaskCanceledException` cruda si vence el plazo antes de las cabeceras | reject |
| 7 | low | Timeout de comando sin tope | patch (validación) |
| 8 | low | Ejecutable que empieza por `-` lo interpreta `timeout` | patch (`--`) |
| 9 | low | `/version` no JSON tumba el arranque; TLS con mensaje engañoso | patch (JSON); defer (TLS) |
| 10 | low | Trama de error del daemon (tipo 3) mezclada en stdout | patch |
| 11 | low | Fallo al copiar el tar reportado como timeout de git | reject |
| 12 | medium | No se comprueba que uid 1000 pueda escribir en `/workspace` | patch |
| 13 | low | Limpieza con contenedor ya creado sin test | reject |
| 14 | low | Liberación en excepción/apagado sin test | reject |
| 15 | low | Abandono de exec y mapeo de errores sin test unitario | reject |
