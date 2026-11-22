---
title: 'Historia 6.2: Acción `composite` publicada'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: 'a71752c0fa41a9893770f9c64b774333632686ea'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Aunque la Historia 6.1 implementó el modo `codenetix action` en el binario, los usuarios todavía no disponen de una acción `composite` (`action.yml`) lista para añadir a su repositorio con unas pocas líneas de YAML, ni de un workflow de ejemplo y documentación completa de permisos y avisos de seguridad de GitHub Actions.

**Approach:** Crear `action.yml` como acción `composite` en la raíz del repositorio que lance `ghcr.io/n3rvs/codenetix:<tag> action` con el socket de Docker del runner, `--no-healthcheck`, montaje de solo lectura de `GITHUB_EVENT_PATH` y paso seguro de secretos (solo vía `env:` y `-e VAR` sin valores en argv); incluir un workflow de ejemplo en `examples/workflows/codenetix.yml`; actualizar `README.md` con el workflow de ejemplo, permisos de `GITHUB_TOKEN` y los avisos obligatorios sobre forks y PRs que no disparan workflows; y añadir `tests/CodeNetix.Host.Tests/CompositeActionTests.cs` verificando estructuralmente `action.yml`, el workflow de ejemplo y la documentación.

## Boundaries & Constraints

**Always:**
- **`action.yml` (acción `composite`, FR-25):**
  - Ubicado en la raíz del repositorio (`action.yml`) con `runs.using: composite` y `shell: bash`.
  - Inputs claros: `version` (etiqueta semver por defecto `v0.1.0`, nunca `latest`), `github-token` (default `${{ github.token }}`), `anthropic-api-key`, `openai-api-key`, `label` (default `codenetix`), `budget-usd`, y overrides opcionales `image` y `sandbox-image`.
  - **Sin inyección de shell:** todos los `${{ inputs.* }}` se pasan al paso exclusivamente mediante `env:`; **nunca** se interpola `${{ ... }}` dentro del bloque `run:`.
  - **Sin secretos en argv:** todas las credenciales (`GITHUB_TOKEN`, `CODENETIX__Providers__*`) se pasan a `docker run` usando `-e NOMBRE_VAR` (heredando del entorno), nunca `-e NOMBRE_VAR="$VALOR"`.
  - **Ejecución correcta del modo Action:**
    - Valida existencia de `/var/run/docker.sock` y `GITHUB_EVENT_PATH`, y rechaza `version=latest` (o etiquetas con caracteres inválidos).
    - Asegura que la imagen del Sandbox (`ghcr.io/n3rvs/codenetix-sandbox:<tag>` o `sandbox-image`) está descargada antes de arrancar `codenetix action`.
    - Lanza `docker run --rm --no-healthcheck` montando `/var/run/docker.sock:/var/run/docker.sock` y `"$GITHUB_EVENT_PATH:/run/codenetix-event.json:ro"`, pasando `GITHUB_EVENT_NAME`, `GITHUB_EVENT_PATH=/run/codenetix-event.json`, `GITHUB_REPOSITORY`, `GITHUB_RUN_ID`, `GITHUB_RUN_ATTEMPT`, `GITHUB_ACTIONS`, `GITHUB_API_URL`, `GITHUB_SERVER_URL`, `GITHUB_TOKEN`, `CODENETIX__Action__Label`, `CODENETIX__Sandbox__Image` y opcionalmente las keys de Proveedor y `CODENETIX__Limits__BudgetUsd`, invocando el subcomando `action`.
    - Maneja `SIGTERM`/`SIGINT` (`trap`) para detener limpiamente el contenedor si el job se cancela.
- **Workflow de ejemplo y documentación (`examples/workflows/codenetix.yml` + `README.md`):**
  - `examples/workflows/codenetix.yml` dispara únicamente en `on: issues: types: [labeled]` con `if: github.event.label.name == 'codenetix'`, `runs-on: ubuntu-latest`, `timeout-minutes: 60`, `permissions: { contents: write, pull-requests: write, issues: write }`, etiqueta semver (`@v0.1.0`, nunca `@latest`) y `secrets.ANTHROPIC_API_KEY`.
  - `README.md` documenta el workflow de ejemplo, la tabla de inputs de `action.yml`, los permisos de `GITHUB_TOKEN` (incluido activar *"Allow GitHub Actions to create and approve pull requests"*), el aviso de que **los *secrets* no llegan a forks** y el aviso de que **los PR creados con `GITHUB_TOKEN` no disparan workflows**.
- **Pruebas automatizadas (`tests/CodeNetix.Host.Tests/CompositeActionTests.cs`):**
  - Parseo YAML real de `action.yml` y `examples/workflows/codenetix.yml` verificando estructura, ausencia de `${{ ... }}` en `run:`, ausencia de `-e KEY=$VAL` para secretos, uso de `--no-healthcheck`, montaje del socket Docker, etiqueta semver (nunca `latest`), permisos `write` en el workflow de ejemplo, ausencia de `pull_request_target`/`workflow_run`, y presencia de todos los avisos requeridos en `README.md`.

**Never:**
- No usar `:latest` ni `@latest` en `action.yml`, `examples/workflows/codenetix.yml` ni en la documentación del modo Action.
- No interpolar `${{ inputs.* }}` ni `${{ secrets.* }}` dentro de `run:` en `action.yml`.
- No pasar tokens ni API keys como `KEY=VALUE` en la línea de comandos de `docker run`.
- No disparar el workflow de ejemplo en `pull_request_target` ni `workflow_run`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Definición de `action.yml` | Parseo YAML de `action.yml` en la raíz | `runs.using == "composite"`, inputs documentados, `version` semver (`vX.Y.Z`, no `latest`), paso bash sin `${{ ... }}` en `run:` | Falla el test si alguien introduce interpolación insegura o `latest` |
| Paso de secretos y montaje Docker | Inspección del script `run:` en `action.yml` | Monta `/var/run/docker.sock` y `$GITHUB_EVENT_PATH:/run/codenetix-event.json:ro`, usa `--no-healthcheck`, pasa `GITHUB_TOKEN` y API keys con `-e VAR` (sin `=$VAL`), invoca `ghcr.io/n3rvs/codenetix:<tag> action` | Comprueba existencia de Docker socket y `GITHUB_EVENT_PATH` con error claro |
| Workflow de ejemplo | `examples/workflows/codenetix.yml` | Solo `on: issues: [labeled]`, `runs-on: ubuntu-latest`, `timeout-minutes`, `permissions` (`contents`, `pull-requests`, `issues`: `write`), referencia semver | Nunca `pull_request_target`, `workflow_run` ni `@latest` |
| Documentación de riesgos y permisos | `README.md` | Incluye workflow de ejemplo, permisos de `GITHUB_TOKEN`, aviso de que los *secrets* no llegan a forks, y aviso de que los PR creados con `GITHUB_TOKEN` no disparan workflows | Verificado en `CompositeActionTests.cs` |

</frozen-after-approval>

## Code Map

- `action.yml` (nuevo) -- acción `composite` publicada en la raíz del repositorio.
- `examples/workflows/codenetix.yml` (nuevo) -- workflow de ejemplo listo para copiar.
- `README.md` -- documentación de la acción `composite`, inputs, workflow de ejemplo, permisos de `GITHUB_TOKEN` y avisos obligatorios (forks y PRs sin disparo de workflows).
- `tests/CodeNetix.Host.Tests/CompositeActionTests.cs` (nuevo) -- pruebas de contrato/seguridad sobre `action.yml`, `examples/workflows/codenetix.yml` y `README.md`.

## Tasks & Acceptance

**Execution:**
- [x] `action.yml` -- crear la acción `composite` lanzando `ghcr.io/n3rvs/codenetix:<tag> action` con el socket de Docker, `--no-healthcheck`, pull previo de `codenetix-sandbox`, paso seguro de secretos vía `env:`/`-e VAR` y manejo de señales -- cumple FR-25.
- [x] `examples/workflows/codenetix.yml` y `README.md` -- añadir workflow de ejemplo, permisos necesarios de `GITHUB_TOKEN` y avisos de forks y de PRs que no disparan workflows -- cumple FR-25.
- [x] `tests/CodeNetix.Host.Tests/CompositeActionTests.cs` -- validar estructuralmente `action.yml`, `examples/workflows/codenetix.yml` y `README.md` cubriendo las 4 filas de la matriz I/O.

**Acceptance Criteria:**
- Given `action.yml` como acción `composite` disparada al etiquetar una issue, when se ejecuta en un runner `ubuntu-*` con Docker, then lanza `ghcr.io/n3rvs/codenetix:<tag> action` con el socket de Docker del runner y lee la key del Proveedor desde los *secrets* del repositorio (FR-25).
- Given la documentación y el workflow de ejemplo, when un usuario los consulta, then encuentra un workflow listo para copiar, los permisos necesarios de `GITHUB_TOKEN`, el aviso de que los *secrets* no llegan a forks y el aviso de que los PR creados con `GITHUB_TOKEN` no disparan workflows.

## Implementation Notes
- Creados `action.yml` (acción `composite` bash sin interpolaciones `${{ ... }}` en `run:`, `--no-healthcheck`, `--group-add` del GID del socket Docker, pull previo de `codenetix-sandbox`, secretos heredados con `-e VAR` y `trap` de `SIGINT`/`SIGTERM`), `examples/workflows/codenetix.yml`, actualización completa de `README.md` y `tests/CodeNetix.Host.Tests/CompositeActionTests.cs` (4 pruebas).
- Actualizado `docker/codenetix.Dockerfile` para copiar `action.yml`, `README.md` y `examples/workflows/codenetix.yml` en la etapa `build` para que `dotnet test` dentro de `docker build` también los valide.
- Verificación: `dotnet build CodeNetix.slnx` (0 errores, 0 warnings); `dotnet test --solution CodeNetix.slnx` (1019 correctos, 0 errores, 1 omitido Docker).

## Spec Change Log

## Review Triage Log
- **Capa 1 (`blind-hunter`), Capa 2 (`edge-case-hunter`), Capa 3 (`verification-gap`) — Iteración 1:**
  - `[PATCH][HIGH]` (`blind-hunter` #1, #2, #3, #4, `edge-case-hunter` #1, #2, #3, #4): Endurecer `action.yml`: validar `CODENETIX_IMAGE` y `CODENETIX__Sandbox__Image` (`validate_image_ref` rechazando `-*`, espacios y `:latest`); copiar `GITHUB_EVENT_PATH` a directorio temporal `0700` con copia `0444` limpiada en `EXIT` para que `USER app` (UID 1654) siempre pueda leerlo; usar `stat -L -c '%g'` para sockets symlink; exportar y pasar `-e CODENETIX__Sandbox__HostContainer`; hacer `docker pull` previo de `$CODENETIX_IMAGE`; corregir `trap` con `trap - SIGINT SIGTERM`, `docker stop -t 7`, señal a `$CHILD_PID` y `exit`.
  - `[PATCH][HIGH]` (`verification-gap` #1–#8): Ampliar `CompositeActionTests.cs` verificando todos los pasos de `runs.steps`, descripciones y defaults de los 8 inputs, todas las comprobaciones y variables `-e` de `docker run`, `trap` `SIGINT`/`SIGTERM` + `docker stop`, regex anti-secretos ampliado (`--env`, `-eVAR=`), `Assert.Single` en `issues.types`, `with:` del workflow coherente con `action.yml`, coherencia exacta de versión `vX.Y.Z` entre `action.yml`/`examples/workflows/codenetix.yml`/`README.md`, los 8 inputs en `README.md` y regex real para el aviso de *secrets* en forks.
