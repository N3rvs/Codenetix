---
title: 'Historia 6.3: CI del proyecto'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: '6dc1e14affeeff632264606546f6396a6ee72ad4'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** El repositorio de CodeNetix todavía no cuenta con un workflow de integración continua en `.github/workflows/ci.yml` que valide automáticamente cada push y Pull Request ejecutando el build del Panel y .NET, `ArchitectureTests`, los tests unitarios y de integración con Docker (incluida la prueba de fuego 2.5) y el build `buildx` multiarquitectura (`linux/amd64` y `linux/arm64`) de ambas imágenes Docker.

**Approach:** Crear `.github/workflows/ci.yml` con permisos mínimos (`contents: read`), cancelación de concurrencia y 3 jobs (`panel`, `build-and-test` con Docker y `Category=SecurityFire`, y `docker-multiarch` con QEMU + Buildx para `linux/amd64,linux/arm64` sobre `docker/codenetix.Dockerfile` y `docker/sandbox.Dockerfile` sin push), copiar `.github/workflows/ci.yml` en `docker/codenetix.Dockerfile` para pruebas dentro del build, y añadir `tests/CodeNetix.Host.Tests/CiWorkflowTests.cs` verificando estructuralmente el contrato del workflow CI.

## Boundaries & Constraints

**Always:**
- **Triggers, permisos y concurrencia (`.github/workflows/ci.yml`):**
  - Disparado en `push` (rama `main`) y `pull_request` (nunca `pull_request_target`).
  - `permissions: { contents: read }` (mínimo privilegio).
  - `concurrency` con `cancel-in-progress: true`.
  - Todos los jobs corren en `ubuntu-latest` y declaran `timeout-minutes`.
- **Job `panel`:**
  - Node 24 (`actions/setup-node`), `npm --prefix panel ci`, `npm --prefix panel run check:api` (AD-14), `npm --prefix panel test` (Vitest) y `npm --prefix panel run build` (`tsc --noEmit && vite build`).
- **Job `build-and-test`:**
  - .NET 10 leyendo `global.json` (`actions/setup-dotnet`), `dotnet restore`, `dotnet build CodeNetix.slnx -c Release --no-restore`.
  - Ejecución explícita de `CodeNetix.ArchitectureTests` (AD-1).
  - Construcción local de `docker/sandbox.Dockerfile` (`codenetix-sandbox:latest`) para que los tests de integración con Docker y la prueba de fuego 2.5 **corran de verdad** en el runner (no se omitan).
  - Ejecución de `CodeNetix.Host.Tests` completos y paso explícito verificando `Category=SecurityFire` (`-- --filter-trait Category=SecurityFire`).
- **Job `docker-multiarch`:**
  - Depende (`needs`) de `panel` y `build-and-test`.
  - Configura `docker/setup-qemu-action` y `docker/setup-buildx-action`.
  - Construye ambas imágenes (`docker/codenetix.Dockerfile` y `docker/sandbox.Dockerfile`) con `docker/build-push-action`, `platforms: linux/amd64,linux/arm64`, `push: false` y caché GHA (`cache-from`/`cache-to`), preparado para ser reutilizado por el workflow de release en 6.4.
- **Pruebas y Dockerfile:**
  - `tests/CodeNetix.Host.Tests/CiWorkflowTests.cs` valida estructuralmente `.github/workflows/ci.yml` usando `YamlDotNet.RepresentationModel`.
  - `docker/codenetix.Dockerfile` copia `.github/workflows/ci.yml` en la etapa `build` para que `dotnet test` dentro de `docker build` también lo valide.

**Never:**
- No hacer `push: true` de imágenes desde el workflow de CI (la publicación firmada es Historia 6.4).
- No conceder permisos de escritura (`contents: write`, `packages: write`) en `.github/workflows/ci.yml`.
- No usar `pull_request_target` en CI.
- No omitir la construcción de `codenetix-sandbox` antes de ejecutar los tests de integración con Docker.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Triggers y mínimo privilegio | Parseo YAML de `.github/workflows/ci.yml` | `on` contiene `push` y `pull_request` (nunca `pull_request_target`); `permissions.contents == "read"`; `concurrency` configurada | Falla el test si se añaden permisos de escritura o triggers inseguros |
| Validación de Panel y .NET | Jobs `panel` y `build-and-test` | `panel` ejecuta `ci`, `check:api`, `test`, `build`; `build-and-test` ejecuta `dotnet build`, `ArchitectureTests`, `docker build` de `sandbox.Dockerfile`, `Host.Tests` y `Category=SecurityFire` | Todos con `timeout-minutes` y `runs-on: ubuntu-latest` |
| Build multiarquitectura `buildx` | Job `docker-multiarch` | `needs: [panel, build-and-test]`, QEMU + Buildx, ambos Dockerfiles (`codenetix.Dockerfile` y `sandbox.Dockerfile`), `platforms: linux/amd64,linux/arm64`, `push: false` | Falla el test si falta alguna arquitectura, falta algún Dockerfile o `push != false` |
| Consistencia dentro de `docker build` | `docker/codenetix.Dockerfile` | Copia `.github/workflows/ci.yml` antes de `dotnet test` en la etapa `build` | `CiWorkflowTests` pasa tanto en host como dentro de `docker build` |

</frozen-after-approval>

## Code Map

- `.github/workflows/ci.yml` (nuevo) -- workflow completo de CI (`panel`, `build-and-test`, `docker-multiarch`).
- `docker/codenetix.Dockerfile` -- copiar `.github/workflows/ci.yml` en la etapa `build`.
- `tests/CodeNetix.Host.Tests/CiWorkflowTests.cs` (nuevo) -- validación YAML del contrato de CI cubriendo las 4 filas de la matriz I/O.

## Tasks & Acceptance

**Execution:**
- [x] `.github/workflows/ci.yml` y `docker/codenetix.Dockerfile` -- crear workflow de CI con `panel`, `build-and-test` (incluyendo `ArchitectureTests`, integración Docker y `Category=SecurityFire`) y `docker-multiarch` (`linux/amd64,linux/arm64`, `push: false`) y copiarlo en `codenetix.Dockerfile`.
- [x] `tests/CodeNetix.Host.Tests/CiWorkflowTests.cs` -- validar estructuralmente `.github/workflows/ci.yml` cubriendo las 4 filas de la matriz I/O.

**Acceptance Criteria:**
- Given un push o un PR al repositorio de CodeNetix, when se ejecuta `.github/workflows/ci.yml`, then se ejecutan el build del Panel y .NET, `ArchitectureTests`, los tests unitarios, los tests de integración con Docker (incluida la prueba de fuego 2.5 `Category=SecurityFire`) y el build `buildx` para `linux/amd64` y `linux/arm64` de ambas imágenes (`codenetix` y `codenetix-sandbox`).

## Implementation Notes
- Creado `.github/workflows/ci.yml` con mínimo privilegio (`contents: read`), `concurrency`, y 3 jobs: `panel`, `build-and-test` (con `docker build` de `sandbox.Dockerfile` y paso explícito `Category=SecurityFire`) y `docker-multiarch` (QEMU + Buildx `linux/amd64,linux/arm64`, `push: false` para ambos Dockerfiles).
- Actualizados `.dockerignore` (permitiendo `.github/workflows/ci.yml`) y `docker/codenetix.Dockerfile` (copiando `.github/workflows/ci.yml` y `docker/codenetix.Dockerfile` antes de `dotnet test`), más 5 pruebas en `tests/CodeNetix.Host.Tests/CiWorkflowTests.cs`.
- Verificación: `dotnet build CodeNetix.slnx` (0 errores, 0 warnings); `dotnet test --solution CodeNetix.slnx` (1024 correctos, 0 errores, 1 omitido Docker).

## Spec Change Log

## Review Triage Log
- **Capa 1 (`blind-hunter`), Capa 2 (`edge-case-hunter`), Capa 3 (`verification-gap`) — Iteración 1:**
  - `[PATCH][HIGH]` (`blind-hunter` #1, #5, `edge-case-hunter` #2): Añadir `--fail-skips on` en los pasos `CodeNetix.Host.Tests` y `SecurityFire` de `.github/workflows/ci.yml` (y `--filter-not-trait Category=SecurityFire` en el paso general para no duplicar la prueba de fuego), impidiendo que las pruebas Docker/2.5 se omitan silenciosamente en CI.
  - `[PATCH][HIGH]` (`blind-hunter` #2, `edge-case-hunter` #3): Restringir `cache-to` en `docker-multiarch` a `push` en `main` con `ignore-error=true` para no romper PRs desde forks ni agotar la cuota de caché GHA.
  - `[PATCH][HIGH]` (`edge-case-hunter` #1, `blind-hunter` #4): Usar `FROM --platform=$BUILDPLATFORM` en las etapas `panel` y `build` de `docker/codenetix.Dockerfile` para ejecutar `vite build` y `dotnet test` de forma nativa sin emulación QEMU `arm64` (evitando timeouts de 45 min y falsos fallos).
  - `[PATCH][HIGH]` (`verification-gap` #1–#7, `edge-case-hunter` #5): Endurecer `CiWorkflowTests.cs`: verificar ausencia de `permissions` con `write` a nivel de job, distinguir `hostTestsIndex != securityFireIndex`, validar `--fail-skips on`, `-t codenetix-sandbox:latest`, orden `restore < build < tests`, orden `qemu`/`buildx` antes de `build-push-action`, `type=gha`, `context: .`, `actions/checkout@`, `COPY docker/codenetix.Dockerfile` y reglas de `.dockerignore`.
  - `[REJECT]` (`blind-hunter` #3, `edge-case-hunter` #4): Mantener `concurrency.cancel-in-progress: true` tal como está fijado en la sección congelada de la spec.
