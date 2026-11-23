# Epic 6 Context: GitHub Action y lanzamiento público (preview)

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Que cualquiera pueda usar CodeNetix sin servidor y descargar imágenes firmadas: el mismo binario se ejecuta como paso de GitHub Actions (`codenetix action`, disparado al etiquetar una issue) con la misma seguridad que `serve` (Ingestion con FR-26, Pipeline, Sandbox Docker y Egress Gateway); se publica una acción `composite`; el proyecto tiene CI, versionado semver, imágenes firmadas con cosign + SBOM, `SECURITY.md`, licencia y un README que lleva al primer PR en menos de 10 minutos documentando los riesgos (FR-25 + requisitos de publicación).

## Stories

- Story 6.1: Modo `codenetix action`
- Story 6.2: Acción `composite` publicada
- Story 6.3: CI del proyecto
- Story 6.4: Publicación firmada de imágenes
- Story 6.5: Documentación de lanzamiento

## Requirements & Constraints

- **Modo Action (FR-25):** el workflow se dispara al etiquetar una issue y ejecuta la cadena completa en el runner. La key del Proveedor sale de los *secrets* del repositorio. No hay Panel: la traza completa va al log de la Action y al cuerpo del PR. La única Fuente es GitHub (issue etiquetada); Sentry exige `serve`. Se usa el mismo Sandbox Docker sobre el Docker del runner (runner `ubuntu-*`), cumpliendo también FR-27. Sin estado entre ejecuciones (límite de 6 h por job).
- **Un binario, tres modos (AD-12):** `codenetix action` es una Ejecución sin Panel y con SQLite temporal; construye un `EventEnvelope{Source=GitHubAction}` desde `GITHUB_EVENT_PATH` y pasa por Ingestion **sin firma pero con FR-26**; usa el mismo Pipeline, Sandbox y Gateway; vuelca los `RunEvent` a la salida estándar. Ningún modo se salta el Sandbox, el Gateway ni la autorización.
- **Autorización (FR-26, AD-4):** solo se procesan Eventos de actores con permiso de escritura (`admin`/`maintain`/`write`) en el Repositorio objetivo; sin permiso el Evento queda `ignored` con su motivo; si GitHub no responde, se falla cerrado. El texto del Evento siempre es dato (`UntrustedText`), nunca instrucciones. Solo el Dispatcher crea Ejecuciones (como mucho una no terminal por objeto).
- **Token en modo Action (AD-12):** `GITHUB_TOKEN` con permisos `contents`, `pull-requests` e `issues` de escritura. Los PR creados con `GITHUB_TOKEN` no disparan workflows; los *secrets* no llegan a workflows disparados desde forks; la v1 solo se dispara por issue etiquetada (nunca `pull_request_target` ni `workflow_run`).
- **Acción `composite` (Story 6.2):** `action.yml` lanza `ghcr.io/n3rvs/codenetix:<tag> action` con el socket de Docker del runner; documenta un workflow de ejemplo, los permisos de `GITHUB_TOKEN` y los avisos de forks y de PR que no disparan workflows.
- **Secretos (AD-13, FR-2):** un único `ISecretRedactor` en el sumidero de `RunEvent` y en los logs; ningún secreto se persiste ni se imprime.
- **Release (Stories 6.3–6.5):** CI en GitHub Actions (build, `ArchitectureTests`, tests unitarios e integración con Docker incluida la prueba de fuego 2.5, `buildx` x64 + arm64 de `codenetix` y `codenetix-sandbox`); etiquetas `vX.Y.Z` y `vX.Y` en `ghcr.io/n3rvs/` (nunca `latest` en la documentación); cosign + SBOM; Dependabot/Renovate para imágenes base y el CLI de Claude Code; `SECURITY.md`, `LICENSE` Apache-2.0, DCO (`git commit -s`), créditos (Eric Risco) y README con quickstart `docker run -p 127.0.0.1:5000:5000 …` + `codenetix fix`, riesgo del socket de Docker, modelo de seguridad del Sandbox y uso de keys propias.

## Technical Decisions

- **Host compone, los módulos no leen el entorno (AD-1, AD-10):** solo `CodeNetix.Host` lee `GITHUB_*` y `CODENETIX__*`; la normalización del evento de Actions a `EventEnvelope` y la autorización viven en `CodeNetix.Ingestion` detrás de una interfaz de `CodeNetix.Contracts`. Los módulos de negocio solo referencian Contracts (`ArchitectureTests`).
- **Arranque (AD-18):** `action` reutiliza `StartupReconciler` (migraciones → salud → reconciliación → limpieza → `Ready`/`Diagnostic`) sobre una base de datos temporal; en `Diagnostic` no se procesa trabajo.
- **Puertos (AD-7, AD-17):** en `action` no hay Panel ni API (`:5000`) ni webhooks (`:5001`); solo el Egress Gateway (`:8080` `/llm`, `:8081` `CONNECT` de NuGet) para el Sandbox, en la red `internal` `codenetix-sandbox` con el alias `gateway`.
- **Imágenes:** `codenetix` (base `aspnet:10.0`) y `codenetix-sandbox` (base `sdk:10.0` + Node, sin el CLI de Claude Code), x64 y arm64, misma etiqueta semver; migraciones solo hacia delante.

## Cross-Story Dependencies

- **6.1 → 6.2:** `action.yml` invoca el modo `action` de la 6.1 (variables `GITHUB_*`, token y key del Proveedor) y documenta sus códigos de salida.
- **6.3 → 6.4:** el workflow de release reutiliza el build multiarquitectura del CI.
- **6.1/6.2/6.4 → 6.5:** el README documenta el modo Action, la acción publicada y la verificación de firmas.
