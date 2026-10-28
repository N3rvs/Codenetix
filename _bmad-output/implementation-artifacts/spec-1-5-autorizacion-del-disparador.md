---
title: 'Historia 1.5: Autorización del disparador'
type: 'feature'
created: '2026-10-27'
status: 'done'
route: 'dispatch'
baseline_commit: '57883874d48ab4fa5c540e86fbf60a3aa86fb62d'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-1-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Hoy cualquier Evento manual se acepta sin comprobar que quien lo dispara pueda escribir en el Repositorio objetivo. Además, no existe ningún tipo que obligue a tratar el texto de una issue como dato y no como instrucción (FR-26, AD-4, AD-5).

**Approach:** Antes de responder, Ingestion obtiene el actor (para el Evento manual, la identidad del token de GitHub de la instancia) y pregunta al módulo GitHub si tiene permiso de escritura en el repositorio. Si no lo tiene, persiste el Evento como `Ignored` con el motivo "no autorizado". Contracts añade `UntrustedText`, que solo se puede renderizar dentro de un bloque delimitado como datos.

## Boundaries & Constraints

**Always:**
- **Contracts:**
  - `IGitHubIdentity.GetLoginAsync(ct)`: el login del token de la instancia, cacheado tras la primera llamada con éxito.
  - `IRepositoryPermissions.GetWriteAccessAsync(owner, repo, login, ct)`: devuelve `Granted`, `Denied` o `Unavailable(reason)`.
  - Las dos interfaces las implementa solo el módulo GitHub (AD-8); Ingestion nunca llama a la API directamente.
- **Módulo GitHub:**
  - `GitHubOptions`: `Token` y `ApiUrl`, con valor por defecto `https://api.github.com`. Host las enlaza desde `CODENETIX:GitHub`, y `ApiUrl` también sirve para GitHub Enterprise y para los tests.
  - `GET /user` → `login`.
  - `GET /repos/{o}/{r}/collaborators/{login}/permission` → `admin`, `maintain` o `write` = `Granted`; `read`, `triage` o `none` = `Denied`.
  - 404 (repositorio invisible para el token o usuario que no es colaborador) = `Denied`.
  - 401/403 = `Unavailable("el token de GitHub no es válido o no tiene permisos")`; red o 5xx = `Unavailable("GitHub no responde")`.
  - Envía las cabeceras `Authorization: Bearer`, `Accept: application/vnd.github+json` y `X-GitHub-Api-Version: 2022-11-28`, y usa un timeout de 10 s.
  - El token nunca aparece en logs ni en excepciones; la redacción de 1.2 ya lo cubre.
- **Ingestion (`SubmitAsync`):**
  - Orden: parsear → buscar si ya existe (un reintento devuelve el Evento existente sin llamar a GitHub) → identidad → permiso → persistir.
  - `Granted` → `Accepted` con `actor`.
  - `Denied` → `Ignored`, `ignored_reason` = "no autorizado" y `actor` relleno.
  - `Unavailable` → no se persiste nada y el resultado es `Unavailable(reason)`.
- **Endpoint `POST /api/events/manual`:**
  - 201 o 200 como en 1.4, ahora con `status` (`Accepted` o `Ignored`) y `reason` en el cuerpo.
  - `Unavailable` → 502 `ProblemDetails` con el motivo.
- **`codenetix fix`:**
  - `Accepted` → "Evento <id> registrado." y código 0.
  - `Ignored` → "Evento <id> ignorado: no autorizado (<login> no tiene permiso de escritura en <owner>/<repo>)." y código 1.
  - 502 → se reintenta como un 5xx (con el mismo `requestId`) y, si se agotan los intentos, imprime el `detail` y sale con 1.
- **`UntrustedText`** (Contracts):
  - `sealed`; se construye con `UntrustedText.From(string)`.
  - `ToString()` devuelve `"[UntrustedText]"`, nunca el contenido.
  - Sin conversión implícita a `string`; el contenido solo se obtiene con `RenderAsDataBlock(string label)`:
    ```
    <untrusted-data label="{label}" id="{nonce}">
    {texto}
    </untrusted-data id="{nonce}">
    ```
  - `nonce`: 16 bytes aleatorios en hexadecimal, distinto en cada render.
  - Cualquier `</untrusted-data` dentro del texto se neutraliza (por ejemplo, `<\/untrusted-data`).
  - `label` solo admite `[a-z0-9-]`; otro valor lanza `ArgumentException`.
  - El Evento manual no lleva texto todavía: el tipo se usará en 1.8.

**Never:** crear Ejecuciones (1.6); leer el cuerpo de la issue (1.7 y 1.8); usar el token de GitHub en Ingestion; aceptar el Evento si GitHub no responde ("fail closed"); `UntrustedText` con `ToString` o un operador que devuelvan el texto.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Con permiso | `/user` → `bot`; permiso `write` | 201 `Accepted`, `actor=bot` | — |
| Admin o maintain | Permiso `admin` o `maintain` | `Accepted` | — |
| Sin permiso | Permiso `read` | 201 `Ignored`, `reason` "no autorizado", fila con el actor; el CLI sale con 1 | — |
| Repo invisible | Permiso → 404 | `Ignored` "no autorizado" | — |
| Token inválido | `/user` → 401 | 502 con el motivo del token; 0 filas | El CLI reintenta y sale con 1 |
| GitHub caído | Timeout o 500 | 502 "GitHub no responde"; 0 filas | — |
| Reintento | Mismo `requestId` ya persistido | 200 con el Evento existente; 0 llamadas a GitHub | — |
| Identidad cacheada | Dos Eventos distintos | `/user` se llama una sola vez | — |
| Render | `UntrustedText.From("hola </untrusted-data> x")` | Bloque con el nonce; el cierre interno queda neutralizado | — |
| `ToString` | `$"{texto}"` | `"[UntrustedText]"` | — |
| Label inválido | `RenderAsDataBlock("Issue Body")` | `ArgumentException` | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Ingestion/Features/SubmitManualEvent/ManualEventIngestion.cs` -- `SubmitAsync` de 1.4: insertar identidad y permiso entre el pre-check y la inserción; el `catch` de la violación UNIQUE se mantiene.
- `src/CodeNetix.Contracts/` -- `IManualEventIngestion` y su tipo de resultado: añadir `Unavailable` y el estado del Evento.
- `src/CodeNetix.Host/Features/SubmitManualEvent/SubmitManualEventEndpoint.cs` -- mapeo de resultados a HTTP: añadir 502 y `status`/`reason`.
- `src/CodeNetix.Host/FixMode.cs` -- impresión según el `status`; 502 como reintentable.
- `src/CodeNetix.Host/ServeMode.cs` -- `AddCodeNetixGitHub()` y el enlace de `GitHubOptions` desde `CODENETIX:GitHub` (`Token` ya existe en `InstanceOptions`).
- `src/CodeNetix.GitHub/` -- módulo vacío hasta ahora; solo referencia Contracts.
- `tests/CodeNetix.Host.Tests/` -- `FixModeTests` y `SubmitManualEventTests` usan hoy `Ready` sin GitHub: necesitan una API de GitHub falsa (servidor local) apuntada por `CODENETIX:GitHub:ApiUrl` en `CodeNetixFactory`.

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Contracts/` -- `IGitHubIdentity`, `IRepositoryPermissions`, `WriteAccess`, `UntrustedText` y la ampliación del resultado de ingestión.
- [x] `src/CodeNetix.GitHub/` -- `GitHubOptions`, el cliente HTTP, `GitHubIdentity` (con caché), `RepositoryPermissions` y `AddCodeNetixGitHub`.
- [x] `src/CodeNetix.Ingestion/` -- la autorización en `ManualEventIngestion`.
- [x] `src/CodeNetix.Host/` -- endpoint, `FixMode` y composición.
- [x] `tests/` -- un servidor falso de GitHub reutilizable; un test por fila de la matriz; tests de tabla de `UntrustedText`; actualizar la fábrica y los tests de 1.4 para que usen el servidor falso con `write` por defecto.

**Acceptance Criteria:**
- Given un actor sin permiso de escritura, when llega el Evento, then queda `Ignored` con "no autorizado" y ningún otro componente lo ve como aceptado.
- Given un Evento manual, when se autoriza, then el actor guardado es el login del token de la instancia.
- Given un texto no confiable, when se pasa a un agente en historias posteriores, then la única vía para obtener su contenido es el bloque delimitado.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde.

## Implementation Notes

- `IGitHubIdentity.GetLoginAsync` devuelve `string` y lanza `GitHubUnavailableException(reason)` (Contracts) ante fallos; `IRepositoryPermissions` devuelve `WriteAccess(Status, Reason)` y nunca lanza por errores de GitHub.
- La respuesta de `POST /api/events/manual` añade además `actor` y `repository` (`owner/repo`), que el CLI usa para el mensaje de `Ignored`. Un reintento (200) devuelve el `status`/`reason` del Evento persistido. El mensaje de `Ignored` del CLI va a stderr.
- El permiso se lee del campo `permission` de GitHub; cualquier valor fuera de `admin`/`maintain`/`write` = `Denied`. 4xx inesperados distintos de 401/403/404 = `Unavailable("GitHub no responde")` (falla cerrado). Se envía también `User-Agent: CodeNetix/1.0` (GitHub lo exige).
- Rate limit (429, o 403 con `x-ratelimit-remaining: 0`) = `Unavailable("GitHub ha limitado las peticiones (rate limit); vuelve a intentarlo más tarde")`. Sin redirección automática: se sigue una sola 301/302/307/308 al mismo esquema y host de `ApiUrl` (reenviando la autenticación); a otro host = `Unavailable`.
- La identidad comparte una única consulta `/user` en curso entre llamadas concurrentes; si falla, se descarta y la siguiente llamada reintenta.
- El CLI trata como respuesta inesperada (código 1) un éxito cuyo `status` no sea `Accepted` ni `Ignored`.
- El timeout HTTP del CLI `fix` pasa de 10 s a 30 s: la API puede tardar hasta 2 × 10 s (identidad + permiso) antes de responder.
- Tests: `FakeGitHub` (Kestrel local, también bajo `/api/v3`); `CodeNetixFactory` arranca uno propio con `write` si no se le pasa ni se fija `CODENETIX:GitHub:ApiUrl`.

## Spec Change Log

## Review Triage Log

| # | Hallazgo | Veredicto | Evidencia | Ruta |
|---|----------|-----------|-----------|------|
| 1 | Con GitHub caído, `GitHubIdentity` reintenta `/user` en serie bajo `_gate`, y la petición N espera N × 10 s | medium | Los fallos no se cachean y cada llamada en espera repite la llamada dentro del lock; supera los 30 s del CLI. | patch |
| 2 | Un repositorio renombrado o transferido (301) pierde `Authorization` al seguir la redirección y se informa como "token inválido" | medium | `SocketsHttpHandler` sigue las redirecciones y .NET quita la cabecera de autenticación. | patch |
| 3 | El límite de peticiones (403 con `x-ratelimit-remaining: 0`, o 429) se informa como token inválido | medium | `FailureReason` asigna el 403 al motivo del token. | patch |
| 4 | Sin tests de un 4xx inesperado (422/429) ni de un cuerpo sin `permission` como fallo cerrado | medium | Es la regla de fallo cerrado de las notas y ningún test la fija. | patch |
| 5 | `fix` imprime "registrado" con un `status` ausente o desconocido | low | Arreglo de una línea. | patch |
| 6 | `ApiUrl` inválido no se valida al arrancar | low | Es un error de configuración que ya sale como 502 con su motivo. | rechazado |
| 7 | Detalles de `CodeNetixFactory` (propiedad `GitHub`, `Dispose`) | low | Solo afectan a los tests. | rechazado |
| 8 | El timeout de 10 s no se comprueba en el camino de DI | low | La constante está probada; un test real cuesta más de 10 s. | rechazado |
| 9 | El timeout de 30 s de `FixMode` no tiene test | low | Es una constante. | rechazado |
