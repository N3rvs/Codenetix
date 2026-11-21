---
title: 'Historia 2.5: Prueba de fuego de seguridad'
type: 'feature'
created: '2026-11-21'
status: 'done'
route: 'dispatch'
baseline_commit: 'c458595e973173a00b80da0f915348e7f18e361e'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-2-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Aunque el aislamiento del Sandbox, la red interna y el Egress Gateway están implementados por piezas, falta una prueba automatizada de extremo a extremo con un repositorio hostil que intente exfiltrar credenciales y abrir conexiones externas para demostrar de forma reproducible que las keys y el token de GitHub están protegidos (FR-27, NFR-1).

**Approach:** Añadir una suite automatizada de "prueba de fuego de seguridad" en `CodeNetix.Host.Tests` (`SandboxSecurityFireTests`) que ejecute un repositorio de prueba hostil contra el Sandbox real con Docker + Egress Gateway (`NuGetConnectProxy` + `SandboxGatewayRegistry` + `IRunEventSink`) — y una verificación determinista complementaria sin Docker — demostrando que el código del repositorio solo ve la key ficticia `cnx-run-<runId>-<nonce>`, nunca halla `.git/config` ni credenciales reales, y cualquier conexión externa arbitraria falla registrando un `RunEvent` `EgressBlocked`.

## Boundaries & Constraints

**Always:**
- El test de integración con Docker forma parte de `tests/CodeNetix.Host.Tests/` (la suite estándar que ejecuta `dotnet test` en CI), reutiliza `SandboxDockerFixture` y se omite limpiamente con `Assert.Skip` cuando el motor Docker o la imagen `codenetix-sandbox` no están disponibles en local.
- El repositorio de prueba hostil contiene código .NET 10 real que intenta explícitamente:
  1. Leer `ANTHROPIC_API_KEY` y todas las variables de entorno para exfiltrar la key real del proveedor y el token de GitHub.
  2. Buscar y leer `.git/config` en `/workspace` y en directorios ancestros.
  3. Abrir una conexión saliente hacia un dominio externo no autorizado (ej. `evil.example.com:443`) tanto a través de `HTTPS_PROXY` (proxy `CONNECT` del Gateway) como por conexión TCP directa.
- El test verifica con aserciones explícitas que:
  - `ANTHROPIC_API_KEY` dentro del Sandbox solo contiene el token efímero `cnx-run-<runId>-<nonce>` y nunca la key real ni el token de GitHub.
  - `.git/config` no existe en `/workspace` y ningún archivo montado contiene el token de GitHub.
  - La conexión directa externa falla por la red `internal` y la conexión vía `HTTPS_PROXY` a `evil.example.com:443` es rechazada con `403 Forbidden`, emitiendo un `RunEvent` `EgressBlocked` con `Target = "evil.example.com:443"` y el `runId` correcto.
- El proyecto hostil compila y corre 100 % offline dentro del Sandbox (sin requerir paquetes NuGet de internet) para que la prueba sea determinista y rápida en CI.
- Incluir pruebas deterministas sin Docker en el mismo archivo que validen el escenario completo de aislamiento (exclusión de `.git/config` del tar de `LocalGitRepos`, sustitución de key ficticia, revocación al destruir la sesión y bloqueo `403` + `EgressBlocked` en el `NuGetConnectProxy` real) para garantizar cobertura continua en entornos sin daemon Docker.
- Limpiar todos los contenedores y volúmenes etiquetados `codenetix.test=<marcador>` en `DisposeAsync`.

**Never:**
- No escribir credenciales reales de producción ni depender de dominios externos reales en internet para pasar los tests.
- No modificar el comportamiento de seguridad del Sandbox o del Gateway para facilitar el test (la prueba ejercita los componentes reales tal cual corren en producción).
- No dejar contenedores ni volúmenes huérfanos tras la ejecución.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Lectura de `ANTHROPIC_API_KEY` y entorno | Código hostil lee `env` dentro del Sandbox | Solo obtiene `cnx-run-<runId>-<nonce>`; 0 coincidencias de key real (`sk-ant-...`) ni token GitHub (`ghp_...`) | Falla el test si aparece cualquier secreto real |
| Lectura de `.git/config` | Código hostil busca `.git/config` en `/workspace` y padres | Archivo inexistente; 0 referencias a `.git` en el árbol montado | Falla el test si existe `.git` o el token en disco |
| Egress directo a IP externa | Código hostil intenta conexión TCP directa a `1.1.1.1:443` | Conexión rechazada/inaccesible por red Docker `internal` | Falla el test si la conexión prospera |
| Egress vía proxy a dominio prohibido | Código hostil hace petición HTTPS a `https://evil.example.com/` vía `HTTPS_PROXY` | Proxy `CONNECT` responde `403 Forbidden` y emite `EgressBlocked(Target: "evil.example.com:443")` con el `runId` | Falla el test si no se bloquea o no se registra `EgressBlocked` |
| Verificación determinista sin Docker | `LocalGitRepos` + `SandboxManager` (`FakeDockerEngine`) + `NuGetConnectProxy` real | Tar sin `.git/config`, env sin secretos reales, `CONNECT evil.example.com:443` -> `403` + `EgressBlocked` | Ejecuta siempre en `dotnet test` aunque Docker no esté activo |
| Entorno local sin Docker | `SandboxDockerFixture.Unavailable != null` | El test con Docker se marca `Skip` con el motivo claro; los tests sin Docker corren y pasan | No rompe `dotnet test` local |

</frozen-after-approval>

## Code Map

- `tests/CodeNetix.Host.Tests/SandboxSecurityFireTests.cs` (nuevo) -- contiene:
  - `SandboxSecurityFireTests` (`IClassFixture<SandboxDockerFixture>`, `[Trait("Category", "SecurityFire")]`): prueba de fuego real en contenedor Docker con repositorio hostil .NET 10, `NuGetConnectProxy` real y `SandboxGatewayRegistry`.
  - `SandboxSecurityFireOfflineTests` (`[Trait("Category", "SecurityFire")]`): verificación determinista del escenario hostil completo usando `LocalGitRepos`, `FakeDockerEngine`, `SandboxManager` y `NuGetConnectProxy` real.
- `tests/CodeNetix.Host.Tests/SandboxIntegrationTests.cs` -- reutiliza `SandboxDockerFixture` (ya `public sealed`).
- `README.md` -- actualizar el bloque de estado y mencionar brevemente la suite automatizada de prueba de fuego de seguridad (FR-27 / NFR-1).

## Tasks & Acceptance

**Execution:**
- [x] `tests/CodeNetix.Host.Tests/SandboxSecurityFireTests.cs` -- implementar la prueba de fuego real con Docker (`SandboxSecurityFireTests`) y la suite determinista sin Docker (`SandboxSecurityFireOfflineTests`) cubriendo las 6 filas de la matriz I/O -- cumple FR-27, NFR-1 y los criterios de la Historia 2.5.
- [x] `README.md` -- actualizar la nota de estado (Épica 2 completada) y documentar cómo ejecutar la prueba de fuego de seguridad (`dotnet test --filter Category=SecurityFire`) -- respalda la afirmación de aislamiento en el README.

**Acceptance Criteria:**
- Given un repositorio de prueba cuyo código hostil intenta leer `ANTHROPIC_API_KEY`, `.git/config`, el token de GitHub y conectarse a `evil.example.com`, when se ejecuta dentro del Sandbox en el test de integración con Docker, then solo obtiene la key ficticia `cnx-run-<runId>-<nonce>`, no encuentra `.git/config` ni ninguna credencial real, la conexión externa falla y el host registra un `RunEvent` `EgressBlocked` asociado al `runId`.
- Given el mismo escenario hostil ejecutado en la suite determinista sin Docker, when corre `dotnet test`, then todas las aserciones de exclusión de `.git/config`, aislamiento de entorno y bloqueo `403` + `EgressBlocked` en el `NuGetConnectProxy` real pasan con 0 errores y 0 advertencias.
- Given la suite de pruebas del proyecto (`CodeNetix.Host.Tests`), when se ejecuta `dotnet test CodeNetix.slnx` en CI, then la prueba de fuego forma parte de la suite estándar.

## Implementation Notes

- **Prueba de fuego de seguridad:** Creado [SandboxSecurityFireTests.cs](../../tests/CodeNetix.Host.Tests/SandboxSecurityFireTests.cs) con `SandboxSecurityFireTests` (Docker real vía `SandboxDockerFixture`, proyecto .NET 10 hostil 100 % offline, `NuGetConnectProxy` real, `SandboxGatewayRegistry`, relevo TCP para Docker Desktop en Windows/macOS y limpieza de contenedores/volúmenes por etiqueta) y `SandboxSecurityFireOfflineTests` (suite determinista sin Docker sobre `FakeDockerEngine` + `NuGetConnectProxy` real).
- **Documentación:** Actualizado [README.md](../../README.md) con el estado tras completar la Épica 2 y el comando `dotnet test --project tests/CodeNetix.Host.Tests --filter Category=SecurityFire`.
- **Verificación:** `dotnet test --solution CodeNetix.slnx` ejecutado con **787/787 tests pasados** (incluyendo los de integración reales con Docker), 0 errores, 0 advertencias y 0 contenedores/volúmenes residuales.

## Spec Change Log

## Review Triage Log

| ID | Capa | Veredicto | Ruta | Evidencia / Justificación |
|---|---|---|---|---|
| BH-1 / VG-O-1 | blind-hunter, verification-gap | `medium` | patch | Bajo Microsoft.Testing.Platform (`global.json`) y `xunit.v3`, `--filter` no está soportado; se actualiza el comando en `README.md` a `dotnet test --project tests/CodeNetix.Host.Tests -- --filter-trait Category=SecurityFire`. |
| BH-2 / EC-1 | blind-hunter, edge-case-hunter | `medium` | patch | `StartGatewayRelayIfNeededAsync` usaba `Task.Delay(300)` fijo; se sustituye por sondeo de disponibilidad del contenedor relevo con timeout acotado. |
| BH-3 | blind-hunter | `low` | defer | La atribución de `runId` multi-sesión ya está cubierta en `EgressGatewayTests.SandboxGatewayRegistry_ResolveRunId_distinguishes_multiple_sessions_and_rejects_unauthenticated_or_unknown_ip`. |
| BH-4 | blind-hunter | `medium` | patch | `Assert.Contains("403", report.ProxyHttpError)` podía coincidir con un puerto efímero; se captura `HttpRequestException.StatusCode` (o código explícito de proxy `403`) en `HostileProbeReport`. |
| BH-5 | blind-hunter | `low` | patch | Se amplían aserciones en `SandboxSecurityFireTests` para `ANTHROPIC_BASE_URL`, `HTTP_PROXY`, `NO_PROXY`, `DirectTcpError` no vacío y petición HTTP plana bloqueada por el proxy. |
| BH-6 | blind-hunter | `low` | patch | Se añade la precondición `Assert.True(File.Exists(.../.git/config))` en el test Docker y escaneo de `.gitconfig`/`.git-credentials` en `HostileProbeRepo`. |
| BH-7 / EC-5 | blind-hunter, edge-case-hunter | `low` | patch | `SecurityFireEventSink.AppendAsync` usa ahora `Interlocked.Increment(ref _seq)` para asignación atómica de `Seq`. |
| BH-8 | blind-hunter | `low` | patch | `WaitForBlockedAsync` aumenta timeout a 5 s y falla con mensaje explícito listando los eventos capturados si no llega `expectedTarget`. |
| BH-9 / EC-2 | blind-hunter, edge-case-hunter | `medium` | patch | `SandboxSecurityFireTests.DisposeAsync` envuelve la limpieza Docker en `try/catch` por recurso y garantiza `_repos.Dispose()` y `LocalGitRepos.DeleteDirectory(_root)` en `finally`. |
| EC-3 | edge-case-hunter | `low` | patch | `SandboxSecurityFireOfflineTests.DisposeAsync` protege `_docker?.Dispose()` y `_engine is not null`. |
| EC-4 | edge-case-hunter | `low` | patch | `ReadHeadersAsync` añade un timeout enlazado de 5 s con `CancellationTokenSource.CreateLinkedTokenSource`. |
| BH-10 | blind-hunter | `false` | - | Las casillas `[x]` y las `Implementation Notes` ya se actualizaron antes de la revisión; el diff reflejaba el instante previo a esa edición. |
