---
title: 'Historia 6.4: Publicación firmada de imágenes'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: '43aeeae784a25a82df43fc2f5552b5ba3a436692'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Aunque el workflow de CI (Historia 6.3) construye ambas imágenes multiarquitectura sin publicar, todavía no existe un workflow de release que publique `ghcr.io/n3rvs/codenetix` y `ghcr.io/n3rvs/codenetix-sandbox` ante una etiqueta semver `vX.Y.Z` con etiquetas `vX.Y.Z` y `vX.Y`, firmadas con `cosign` y con SBOM, ni configuración de actualización automática para las imágenes base y la versión fijada del CLI de Claude Code.

**Approach:** Crear `.github/workflows/release.yml` disparado exclusivamente por etiquetas semver `vX.Y.Z` que verifique el build/tests antes de publicar, construya y publique `ghcr.io/n3rvs/codenetix` y `ghcr.io/n3rvs/codenetix-sandbox` multiarquitectura (`linux/amd64,linux/arm64`) con etiquetas `vX.Y.Z` y `vX.Y` (sin `:latest`), adjunte SBOM (`sbom: true`) y provenance, y firme ambos digests con `cosign` keyless (OIDC); añadir `renovate.json` vigilando los Dockerfiles (`docker/*.Dockerfile`), dependencias del repo y el CLI `@anthropic-ai/claude-code` fijado en `src/CodeNetix.Contracts/Sandbox.cs`; y validar todo con `tests/CodeNetix.Host.Tests/ReleaseWorkflowTests.cs`.

## Boundaries & Constraints

**Always:**
- **Workflow de release (`.github/workflows/release.yml`):**
  - Disparado exclusivamente en `push: tags: ['v[0-9]+.[0-9]+.[0-9]+']`.
  - Permisos mínimos explícitos por job: el job de verificación usa `contents: read`; el job de publicación usa `contents: read`, `packages: write`, `id-token: write`, `attestations: write`.
  - Todos los jobs corren en `ubuntu-latest` y declaran `timeout-minutes`.
  - **Verificación previa:** valida formato semver exacto de `GITHUB_REF_NAME`, ejecuta `panel` (`ci`, `check:api`, `test`, `build`), `dotnet build -c Release`, `CodeNetix.ArchitectureTests`, `docker build -f docker/sandbox.Dockerfile -t codenetix-sandbox:latest .`, `CodeNetix.Host.Tests` (`--fail-skips on`) y `Category=SecurityFire` (`--fail-skips on`) antes de publicar.
  - **Publicación multi-arch en GHCR (`ghcr.io/n3rvs/`):**
    - Publica `ghcr.io/n3rvs/codenetix` (`docker/codenetix.Dockerfile`) y `ghcr.io/n3rvs/codenetix-sandbox` (`docker/sandbox.Dockerfile`) para `linux/amd64,linux/arm64` con `push: true`.
    - Etiquetas generadas vía `docker/metadata-action`: `vX.Y.Z` y `vX.Y` únicamente (`flavor: latest=false`, nunca `:latest`).
    - Adjunta SBOM (`sbom: true`) y `provenance: mode=max` en ambos builds.
  - **Firma con `cosign`:**
    - Instala `cosign` con `sigstore/cosign-installer`.
    - Firma los digests exactos publicados (`ghcr.io/n3rvs/codenetix@<digest>` y `ghcr.io/n3rvs/codenetix-sandbox@<digest>`) mediante `cosign sign --yes` (keyless OIDC), pasando digests/referencias exclusivamente por `env:` (cero `${{ ... }}` en `run:`).
- **Vigilancia de imágenes base y CLI de Claude Code (`renovate.json`):**
  - Configura Renovate vigilando los Dockerfiles (`docker/codenetix.Dockerfile` y `docker/sandbox.Dockerfile`), dependencias NuGet, `panel/` (npm), GitHub Actions y un `customManagers` (`regex`) que rastrea `ClaudeCliVersion` en `src/CodeNetix.Contracts/Sandbox.cs` contra el paquete npm `@anthropic-ai/claude-code`.
- **Docker build y pruebas (`tests/CodeNetix.Host.Tests/ReleaseWorkflowTests.cs`):**
  - Permitir `.github/workflows/release.yml` en `.dockerignore` y copiar `.github/workflows/release.yml` y `renovate.json` en `docker/codenetix.Dockerfile` antes de `dotnet test`.
  - `ReleaseWorkflowTests.cs` valida estructuralmente `.github/workflows/release.yml` y `renovate.json`, incluyendo que el regex de `customManagers` **hace match real** contra `src/CodeNetix.Contracts/Sandbox.cs` extrayendo `ISandboxToolsProvisioner.ClaudeCliVersion`.

**Never:**
- No publicar etiqueta `:latest` en `release.yml`.
- No interpolar `${{ ... }}` dentro de bloques `run:` en `release.yml`.
- No dar permisos `write` a nivel raíz del workflow (solo al job de publicación).
- No ejecutar `git push` ni publicar imágenes reales desde esta sesión.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Trigger y mínimo privilegio de release | `.github/workflows/release.yml` | Solo `push.tags: ['v[0-9]+.[0-9]+.[0-9]+']`; permisos raíz `contents: read`; `packages: write` e `id-token: write` solo en el job de publicación | Falla el test si se publican ramas o permisos amplios en raíz |
| Publicación multi-arch, etiquetas semver y SBOM | Job `publish-images` | Publica `ghcr.io/n3rvs/codenetix` y `ghcr.io/n3rvs/codenetix-sandbox` en `linux/amd64,linux/arm64` con `vX.Y.Z` y `vX.Y`, `latest=false`, `sbom: true`, `provenance: mode=max`, `push: true` | `needs` exige pasar verificación previa antes de publicar |
| Firma `cosign` segura | Pasos de firma en `publish-images` | `sigstore/cosign-installer` + `cosign sign --yes` sobre ambos digests pasados por `env:` (sin `${{` en `run:`) | Falla el test si falta firmar cualquiera de las dos imágenes o hay interpolación en `run:` |
| Vigilancia de imágenes base y CLI Claude Code | `renovate.json` + `src/CodeNetix.Contracts/Sandbox.cs` | JSON válido que cubre `docker/*.Dockerfile` y contiene `customManagers` cuyo regex extrae exactamente `ISandboxToolsProvisioner.ClaudeCliVersion` para `@anthropic-ai/claude-code` | Falla el test si el regex deja de hacer match con `Sandbox.cs` |

</frozen-after-approval>

## Code Map

- `.github/workflows/release.yml` (nuevo) -- workflow de release firmado en GHCR (`ghcr.io/n3rvs/codenetix` y `ghcr.io/n3rvs/codenetix-sandbox`) con verificación previa, `vX.Y.Z` + `vX.Y`, SBOM y `cosign`.
- `renovate.json` (nuevo) -- vigilancia automática de imágenes base Docker, NuGet, npm, GitHub Actions y `@anthropic-ai/claude-code` en `src/CodeNetix.Contracts/Sandbox.cs`.
- `.dockerignore` y `docker/codenetix.Dockerfile` -- incluir `.github/workflows/release.yml` y `renovate.json` en la etapa `build` antes de `dotnet test`.
- `tests/CodeNetix.Host.Tests/ReleaseWorkflowTests.cs` (nuevo) -- validación estructural de `.github/workflows/release.yml`, `renovate.json` (incluido match real del regex contra `Sandbox.cs`) y Dockerfile/`.dockerignore`.

## Tasks & Acceptance

**Execution:**
- [x] `.github/workflows/release.yml` -- crear workflow de release ante tags `vX.Y.Z` con verificación previa, build/push multi-arch (`linux/amd64,linux/arm64`) en `ghcr.io/n3rvs/` con etiquetas `vX.Y.Z` y `vX.Y` (sin `latest`), `sbom: true`, `provenance: mode=max` y firma keyless `cosign` sobre ambos digests.
- [x] `renovate.json`, `.dockerignore` y `docker/codenetix.Dockerfile` -- configurar vigilancia de imágenes base Docker y `@anthropic-ai/claude-code` (`ClaudeCliVersion` en `Sandbox.cs`), y copiar artefactos en `codenetix.Dockerfile`.
- [x] `tests/CodeNetix.Host.Tests/ReleaseWorkflowTests.cs` -- cubrir las 4 filas de la matriz I/O.

**Acceptance Criteria:**
- Given una etiqueta semver `vX.Y.Z`, when se ejecuta `.github/workflows/release.yml`, then se publican `ghcr.io/n3rvs/codenetix` y `ghcr.io/n3rvs/codenetix-sandbox` multiarquitectura con las etiquetas `vX.Y.Z` y `vX.Y` (sin `latest`), firmadas con `cosign` y con SBOM.
- Given `renovate.json`, when se inspeccionan sus reglas, then vigila las imágenes base de `docker/*.Dockerfile` y la versión fijada de `@anthropic-ai/claude-code` en `src/CodeNetix.Contracts/Sandbox.cs`.

## Implementation Notes
- Creado `.github/workflows/release.yml` (tag semver `vX.Y.Z`, verificación previa con `panel` + `dotnet test` + `SecurityFire --fail-skips on`, publicación multi-arch `linux/amd64,linux/arm64` en `ghcr.io/n3rvs/codenetix` y `ghcr.io/n3rvs/codenetix-sandbox` con `vX.Y.Z` y `vX.Y`, `latest=false`, `sbom: true`, `provenance: mode=max` y firma `cosign sign --yes` sobre ambos digests vía `env:`).
- Creado `renovate.json` vigilando `docker/*.Dockerfile`, NuGet, `panel/` (npm), GitHub Actions y `ClaudeCliVersion` (`@anthropic-ai/claude-code`) en `src/CodeNetix.Contracts/Sandbox.cs`.
- Actualizados `.dockerignore` y `docker/codenetix.Dockerfile`, y añadidas 5 pruebas en `tests/CodeNetix.Host.Tests/ReleaseWorkflowTests.cs`.
- Verificación: `dotnet build CodeNetix.slnx` (0 errores, 0 warnings); `dotnet test --solution CodeNetix.slnx` (1029 correctos, 0 errores, 1 omitido Docker).

## Spec Change Log

## Review Triage Log
- **Capa 1 (`blind-hunter`), Capa 2 (`edge-case-hunter`), Capa 3 (`verification-gap`) — Iteración 1:**
  - `[REJECT]` (`verification-gap` #1): `on.push.tags: ['v[0-9]+.[0-9]+.[0-9]+']` es sintaxis oficial válida de filtros de GitHub Actions (confirmado por `blind-hunter` y `edge-case-hunter`) y está fijado en la spec congelada.
  - `[REJECT]` (`blind-hunter` #2): Mantener `attestations: write` en `publish-images.permissions` tal como está fijado en la sección congelada de la spec.
  - `[PATCH][HIGH]` (`blind-hunter` #1, `edge-case-hunter` #1): Migrar `fileMatch` (deprecado en Renovate v40+) a `managerFilePatterns` con sintaxis `/.../` en `renovate.json`.
  - `[PATCH][MEDIUM]` (`edge-case-hunter` #2, #3, #4, `blind-hunter` #3): Añadir `--recursive` a `cosign sign --yes --recursive`, firmar cada imagen inmediatamente tras su `docker/build-push-action` (evitando imágenes publicadas sin firma ante fallo parcial), y ajustar el regex SemVer 2.0.0 en `verify` a `^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$`.
  - `[PATCH][HIGH]` (`verification-gap` #2–#7, `blind-hunter` #4): Endurecer `ReleaseWorkflowTests.cs`: verificar emparejamiento 1:1 y orden `meta < build < sign` (`id`, `file`, `tags`, `labels`, `scope`, `digest`, `set -euo pipefail`, `^sha256:`), cardinalidad exacta de `permissions` en `verify` (obligatorio) y `publish-images` (4), 2 reglas exactas en `metadata-action.with.tags`, 2 plataformas exactas, ausencia de `continue-on-error: true`, `managerFilePatterns` con `Assert.Single` + casos negativos + listado dinámico de `docker/*.Dockerfile`, y acotación de `COPY` a la etapa `AS build` y orden relativo en `.dockerignore`.
