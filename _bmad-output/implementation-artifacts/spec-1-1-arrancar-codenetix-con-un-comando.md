---
title: 'Historia 1.1: Arrancar CodeNetix con un comando'
type: 'feature'
created: '2026-10-27'
status: 'done'
route: 'dispatch'
baseline_commit: 'c825185883c7abca0e689c6305eea8def088e3a5'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-1-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** El repositorio solo tiene la planificación; no hay solución .NET, Panel ni imagen, así que nadie puede arrancar CodeNetix ni comprobar que está vivo (FR-1).

**Approach:** Crear el esqueleto del monolito modular de AD-1: los 8 proyectos más `ArchitectureTests`, un Panel React 19 + Vite 8 mínimo y la imagen `codenetix` sobre `aspnet:10.0`. `codenetix serve` crea `/data/codenetix.db` en WAL, expone `GET /api/health` y sirve la página de bienvenida en `:5000`.

## Boundaries & Constraints

**Always:**
- Los módulos de negocio (Ingestion, Pipeline, Agents, Sandbox, GitHub, Persistence) solo referencian `CodeNetix.Contracts`; solo Host referencia a todos. `ArchitectureTests` lo comprueba leyendo los `ProjectReference` de cada `.csproj` (determinista; el compilador elimina referencias de ensamblado sin uso) y se ejecuta dentro del `docker build`, de modo que una violación rompe la imagen.
- `net10.0`, `Nullable` activado, `TreatWarningsAsErrors`, versiones centralizadas en `Directory.Packages.props`. Las versiones son las del Stack del spine.
- Solo Host lee el entorno; Persistence recibe `IOptions<PersistenceOptions>` (`DataDirectory`, por defecto `/data`, que se puede sobrescribir con `CODENETIX__Persistence__DataDirectory`).
- Persistence abre la BD con `journal_mode=WAL` y `busy_timeout=5000`, y crea el directorio si falta. Host lo invoca desde `StartupReconciler` (paso 1 de AD-18; los demás pasos llegan en historias posteriores).
- Los textos visibles para el usuario van en español.
- Contenedor sin root (usuario `app`), `VOLUME /data` con propietario `app`, `ENTRYPOINT ["codenetix"]`, `CMD ["serve"]`, binario `codenetix` en el `PATH` y `HEALTHCHECK` que llama a `/api/health`.
- Decisión: `GET /api/health` es anónimo. Lo necesita el `HEALTHCHECK` y la historia 1.3 debe mantenerlo exento del `admin-token`.
- Decisión: el `HEALTHCHECK` usa el subcomando interno `codenetix healthcheck`, que hace GET a `http://127.0.0.1:5000/api/health` y sale con 0 o 1. No es un modo de usuario y evita instalar curl.

**Never:** variables de configuración o `IInstanceHealth` (1.2), auth, `admin-token` o `:5001` (1.3), `codenetix fix` (1.4), `DbContext` o tablas, Tailwind o diseño del Panel (Épica 4), CI o multiarquitectura con buildx (Épica 6), registro, login o cuentas, telemetría.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Arranque limpio | `docker run -p 127.0.0.1:5000:5000 -v codenetix-data:/data codenetix serve`, volumen vacío | `/data/codenetix.db` creado en WAL; `GET /api/health` → 200 `{"status":"Ready"}` en < 60 s | N/A |
| Rearranque | Volumen con BD existente | Reutiliza la BD sin error | N/A |
| Panel | `GET /` o ruta SPA desconocida (`/runs/x`) | 200 `index.html` con la bienvenida y el ejemplo `docker exec codenetix codenetix fix <url-de-issue>` | `/api/*` desconocido → 404, nunca `index.html` |
| Modo desconocido | `codenetix foo` o sin argumentos fuera de la imagen | Uso por stderr y código de salida 2 | No arranca Kestrel |
| `/data` no escribible | Directorio sin permisos | El proceso termina con código ≠ 0 y un log claro de la ruta | Sin salud falsa |

</frozen-after-approval>

## Code Map

- `.gitignore` -- ya ignora `bin/`, `obj/`, `node_modules/`, `dist/`, `*.db*`; añadir `src/CodeNetix.Host/wwwroot/`, que se genera con el build del Panel.
- `_bmad-output/planning-artifacts/architecture/architecture-Codenetix-2026-10-26/ARCHITECTURE-SPINE.md` -- AD-1, AD-11, AD-12, AD-17, AD-18, Stack y árbol del Structural Seed: fuente de verdad de nombres y rutas.
- No hay código previo; todo es nuevo y no hay nada que reutilizar.

## Tasks & Acceptance

**Execution:**
- [x] `CodeNetix.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json` (SDK 10.0.402, `rollForward: latestFeature`) -- base común de la solución.
- [x] `src/CodeNetix.{Contracts,Ingestion,Pipeline,Agents,Sandbox,GitHub}/` -- bibliotecas vacías; cada una solo con `ProjectReference` a Contracts (Contracts sin referencias).
- [x] `src/CodeNetix.Persistence/` -- `PersistenceOptions`, `SqliteDatabase` (cadena de conexión hacia `codenetix.db` e `InitializeAsync`, que crea el directorio y aplica los PRAGMA) y `AddCodeNetixPersistence`, con `Microsoft.Data.Sqlite`.
- [x] `src/CodeNetix.Host/` -- `AssemblyName=codenetix`; `Program.cs` con el despacho de modos (`serve`, `healthcheck`, el resto → 2); Kestrel en `http://+:5000`; `Features/GetHealth/` (endpoint); `StartupReconciler` (`IHostedService`); `UseDefaultFiles`, `UseStaticFiles` y fallback SPA que excluye `/api`; `public partial class Program`.
- [x] `panel/` -- Vite 8.3.5 + React 19.3.1 + TypeScript; `build.outDir` = `../src/CodeNetix.Host/wwwroot` con `emptyOutDir`; `App.tsx` con la bienvenida "CodeNetix está en marcha" y el comando de ejemplo.
- [x] `tests/CodeNetix.ArchitectureTests/` -- xUnit; descubre `src/*/*.csproj` desde la raíz de la solución y verifica la regla de AD-1; incluye un caso de tabla que demuestra que la función de validación detecta una violación sintética.
- [x] `tests/CodeNetix.Host.Tests/` -- xUnit + `WebApplicationFactory<Program>` con un `DataDirectory` y un `WebRootPath` temporales (con su propio `index.html`): salud 200, BD creada con `journal_mode` = `wal`, `/` y ruta SPA devuelven HTML, `/api/nope` → 404, rearranque sobre la BD existente.
- [x] `docker/codenetix.Dockerfile` y `.dockerignore` -- etapas `node:24` (panel) → `sdk:10.0` (restore, `dotnet test` de ArchitectureTests, publish con el `wwwroot` del panel) → `aspnet:10.0` (runtime).

**Acceptance Criteria:**
- Given un `ProjectReference` de Pipeline a Ingestion, when se ejecutan `dotnet test` o `docker build`, then `ArchitectureTests` falla nombrando la referencia prohibida.
- Given la solución limpia, when `docker build -f docker/codenetix.Dockerfile -t codenetix .`, then la imagen se construye con el Panel compilado en `wwwroot`.
- Given la imagen, when se arranca con el comando de FR-1, then `docker inspect` llega a `healthy` y `http://localhost:5000` muestra la bienvenida sin ningún paso de registro ni login.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test CodeNetix.slnx` -- expected: todo en verde.
- `npm ci && npm run build` (en `panel/`) -- expected: `src/CodeNetix.Host/wwwroot/index.html` generado.
- `docker build -f docker/codenetix.Dockerfile -t codenetix .` y después `docker run -d --name codenetix-smoke -p 127.0.0.1:5000:5000 -v codenetix-smoke-data:/data codenetix serve` -- expected: `curl localhost:5000/api/health` → 200 en < 60 s, `curl localhost:5000/` contiene "CodeNetix está en marcha" y `docker exec codenetix-smoke ls /data` muestra `codenetix.db`. Al terminar se borran el contenedor y el volumen de humo.

## Implementation Notes

## Spec Change Log

## Review Triage Log

| # | Hallazgo | Veredicto | Evidencia | Ruta |
|---|----------|-----------|-----------|------|
| 1 | `codenetix --x=y` arranca `serve` en vez de salir con 2 (blind, edge, verif.) | medium | `CodeNetixCommandLine.cs`: cualquier `--k=v` como primer argumento va a `ServeMode`, contra la fila "Modo desconocido" de la matriz. | patch |
| 2 | `codenetix healthcheck` sin tests (blind, verif.) | medium | Ningún test toca `HealthcheckMode`; el `HEALTHCHECK` de la imagen depende de su código de salida. | patch |
| 3 | El fallo de arranque de `serve` (código ≠ 0 y log con la ruta) no se prueba; `ThrowsAny` es demasiado laxo (blind, verif.) | medium | `WebApplicationFactory` nunca ejecuta el `try/catch` de `ServeMode.RunAsync`. | patch |
| 4 | Persistence usa `FrameworkReference` a `Microsoft.AspNetCore.App` (blind) | low | Acopla un módulo de negocio a la pila web solo para usar Options y DI; se corrige cambiando las referencias. | patch |
| 5 | Si la BD existente es de solo lectura, la salud dice `Ready` (edge) | medium | SQLite abre en solo lectura sin avisar y `journal_mode` sigue devolviendo `wal`, lo que da una salud falsa que la matriz prohíbe. | patch |
| 6 | No se comprueba `busy_timeout=5000` (verif.) | low | Ningún test lee `PRAGMA busy_timeout` desde `OpenConnectionAsync`; un test lo arregla. | patch |
| 7 | `Dispose` de los tests solo captura `IOException` (blind, edge ×2) | low | En Windows un archivo bloqueado lanza `UnauthorizedAccessException`; se corrige con un cambio directo. | patch |
| 8 | El `docker exec codenetix …` de la bienvenida no funciona con el `docker run` de FR-1, que no tiene `--name` (blind, edge) | medium | Incoherencia ya presente en epics y arquitectura (AD-12 frente a FR-1); el texto está en la intención congelada. | defer |
| 9 | Falta `--help`; `healthcheck` no aparece en el uso (blind) | low | Se imprime el uso igualmente; añadirlo supone una rama nueva. | rechazado |
| 10 | La rama 503 `Starting` es inalcanzable (blind) | maybe-false | Depende del orden de arranque de `GenericWebHostService` frente a los hosted services; es una rama defensiva e inocua. Lo resolvería un test de orden. Si fuera cierto, el impacto sería low. | rechazado |
| 11 | La verificación de la spec usa `dotnet test CodeNetix.slnx` (blind) | low | Su arreglo es editar la spec de este build. | rechazado |
| 12 | `DataDirectory` vacío no pasa por el log con la ruta (blind, edge) | low | Falla igualmente de forma ruidosa al construir la DI; es poco probable. | rechazado |
| 13 | El catch-all de `/api` devuelve 404 en lugar de 405 y el fallback responde a POST (blind) | low | No afecta al uso normal; el arreglo añade restricciones de método. | rechazado |
| 14 | `sprint-status` en `in-progress` con la spec en `in-review` (blind) | false | El paso 5 del flujo sincroniza el estado al cerrar la historia. | rechazado |
| 15 | La numeración del orquestador en `epic-1-context.md` salta el paso 2 (blind) | false | Sigue la numeración de AD-16; el paso 2 (Sandbox) no existe en la Épica 1. | rechazado |
| 16 | Imágenes base sin fijar a parche o digest (blind) | low | La reproducibilidad del release es cosa de la Épica 6 (firmado y Renovate). | rechazado |
| 17 | No hay humo sobre `/assets/*.js` del Panel publicado (blind) | low | El humo manual sirvió la página; `UseStaticFiles` es estándar. | rechazado |
| 18 | Una ruta SPA con punto (`/runs/a.b`) da 404 (edge) | low | Los runId son GUID sin puntos; es poco probable. | rechazado |
| 19 | Sin `wwwroot` con `dotnet run`, `/` da 404 sin explicación (edge) | low | Solo afecta al desarrollo local; arreglarlo añade lógica. | rechazado |
| 20 | Un `ProjectReference` con `;` o importado desde `.props` escapa a la regla (edge) | low | Ningún `.csproj` del repo lo usa; cubrirlo exige evaluar MSBuild. | rechazado |
| 21 | Una referencia con otras mayúsculas da un falso positivo (edge) | low | Falla de forma ruidosa y solo pasa si alguien escribe mal el nombre; es poco probable. | rechazado |
