---
title: 'Historia 6.5: Documentación de lanzamiento'
type: 'feature'
created: '2026-11-22'
status: 'done'
baseline_commit: '10b898ee2deab42f2fd2f462ca4c533a85aa4c5f'
route: 'dispatch'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Aunque CodeNetix ya cuenta con el pipeline completo, Panel en vivo, control de presupuesto, acción `composite`, CI y publicación firmada en GHCR, falta `SECURITY.md` (política de divulgación), el `README.md` todavía contiene un aviso obsoleto de desarrollo, no incluye un quickstart con las imágenes firmadas de `ghcr.io/n3rvs/` ni la verificación `cosign`, y faltan los créditos a la inspiración (Eric Risco).

**Approach:** Crear `SECURITY.md` con la política de divulgación responsable y alcance de seguridad; actualizar `README.md` con un quickstart completo (< 10 minutos) usando `docker run -p 127.0.0.1:5000:5000 …` y `codenetix fix` (coherente con la versión semver de `action.yml`, nunca `latest`), documentación explícita del riesgo del socket de Docker, del modelo de seguridad del Sandbox, del uso de keys propias (BYOK + presupuesto), de la verificación de firmas `cosign` + SBOM, créditos a Eric Risco y enlaces a `SECURITY.md`, `LICENSE` (Apache-2.0) y `CONTRIBUTING.md` (DCO `git commit -s`); copiar los documentos de lanzamiento en `docker/codenetix.Dockerfile`; y validar todo con `tests/CodeNetix.Host.Tests/ReleaseDocumentationTests.cs`.

## Boundaries & Constraints

**Always:**
- **`SECURITY.md` (nuevo en la raíz):**
  - Política clara de divulgación responsable (no abrir issues públicas; reportar de forma privada vía GitHub Security Advisories en `https://github.com/N3rvs/codenetix/security/advisories/new`).
  - Versiones soportadas y plazos orientativos de respuesta.
  - Resumen del modelo de seguridad y responsabilidades del operador (socket de Docker, Sandbox aislado, Egress Gateway, manejo de secretos BYOK y verificación de imágenes con `cosign`).
- **`README.md` (actualizado para lanzamiento):**
  - Eliminar el aviso obsoleto (*"la Revisión y la creación del PR llegan en la próxima épica"*).
  - **Quickstart (< 10 min, SM-1):** pasos claros con `docker run -p 127.0.0.1:5000:5000 …` (etiqueta semver coherente con `action.yml`, nunca `:latest` en los comandos de imágenes GHCR) y `docker exec codenetix codenetix fix <url> --follow`, además de cómo abrir el Panel y construir en local.
  - **Seguridad y riesgos:** documentar explícitamente el **riesgo del socket de Docker**, el **modelo de seguridad del Sandbox** (sin `.git`, sin credenciales, red `internal: true`, Egress Gateway, prueba de fuego 2.5), el **uso de keys propias (BYOK)** + control de presupuesto, y cómo **verificar firmas `cosign` y SBOM** de las imágenes publicadas en `ghcr.io/n3rvs/`.
  - **Modo GitHub Action:** mantener íntegramente todos los requisitos validados por `CompositeActionTests.cs` (versión coherente con `action.yml`, los 8 inputs, permisos de `GITHUB_TOKEN`, aviso de *secrets* en forks y aviso de PRs que no disparan workflows).
  - **Créditos, contribución y licencia:** reconocer explícitamente la inspiración en **Eric Risco**, enlazar [SECURITY.md](SECURITY.md), [CONTRIBUTING.md](CONTRIBUTING.md) (DCO `git commit -s`) y [LICENSE](LICENSE) (Apache 2.0).
- **`LICENSE` y `CONTRIBUTING.md`:**
  - Conservar `LICENSE` (Apache-2.0) y `CONTRIBUTING.md` (DCO `git commit -s`, enlace a `SECURITY.md`) verificados por tests.
- **Dockerfile y pruebas (`tests/CodeNetix.Host.Tests/ReleaseDocumentationTests.cs`):**
  - Copiar `SECURITY.md`, `LICENSE` y `CONTRIBUTING.md` en la etapa `build` de `docker/codenetix.Dockerfile` antes de `dotnet test`.
  - `ReleaseDocumentationTests.cs` verifica existencia y requisitos clave de `SECURITY.md`, `LICENSE`, `CONTRIBUTING.md`, `README.md` (sin romper `CompositeActionTests.cs`) y la copia en `docker/codenetix.Dockerfile`.

**Never:**
- No usar `:latest` ni `@latest` en los comandos de imágenes `ghcr.io/n3rvs/` del `README.md`.
- No romper ninguna aserción existente de `CompositeActionTests.cs`, `CiWorkflowTests.cs` ni `ReleaseWorkflowTests.cs`.
- No alterar los términos legales de `LICENSE` (Apache-2.0).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Quickstart en `README.md` | Lectura de `README.md` | Incluye `docker run` con `-p 127.0.0.1:5000:5000`, `ghcr.io/n3rvs/codenetix:<version>` (coherente con `action.yml`, sin `:latest`), `codenetix fix`, y sin texto obsoleto de épicas pasadas | `CompositeActionTests` y `ReleaseDocumentationTests` ambos en verde |
| Seguridad y riesgos en `README.md` | Lectura de `README.md` | Documenta riesgo del socket de Docker, modelo de seguridad del Sandbox, uso de keys propias (BYOK) y verificación con `cosign` + SBOM | Enlaza a `SECURITY.md` |
| `SECURITY.md` | Lectura de `SECURITY.md` | Política de divulgación privada (GitHub Security Advisories, sin issues públicas), versiones soportadas y alcance de seguridad | Copiado en `docker/codenetix.Dockerfile` antes de `dotnet test` |
| Créditos, licencia y DCO | `README.md`, `LICENSE`, `CONTRIBUTING.md` | Créditos explícitos a **Eric Risco**, `LICENSE` Apache-2.0, `CONTRIBUTING.md` exige `git commit -s` (DCO) y enlaza a `SECURITY.md` | Validado automáticamente en `ReleaseDocumentationTests.cs` |

</frozen-after-approval>

## Code Map

- `SECURITY.md` (nuevo) -- política de divulgación responsable y resumen de seguridad.
- `README.md` -- quickstart < 10 min, verificación `cosign`/SBOM, riesgos (socket Docker, Sandbox, keys propias/BYOK), créditos a Eric Risco y enlaces a `SECURITY.md`, `CONTRIBUTING.md` y `LICENSE`.
- `docker/codenetix.Dockerfile` -- copiar `SECURITY.md`, `LICENSE` y `CONTRIBUTING.md` en la etapa `build` antes de `dotnet test`.
- `tests/CodeNetix.Host.Tests/ReleaseDocumentationTests.cs` (nuevo) -- pruebas estructurales cubriendo las 4 filas de la matriz I/O.

## Tasks & Acceptance

**Execution:**
- [x] `SECURITY.md` y `README.md` -- crear política de divulgación y completar documentación de lanzamiento (quickstart `docker run -p 127.0.0.1:5000:5000` + `codenetix fix`, verificación `cosign`, riesgos de socket Docker/Sandbox/keys propias, créditos a Eric Risco, licencia Apache-2.0 y DCO) -- cumple SM-1 y OQ-2.
- [x] `docker/codenetix.Dockerfile` y `tests/CodeNetix.Host.Tests/ReleaseDocumentationTests.cs` -- copiar documentos en la etapa `build` y cubrir las 4 filas de la matriz I/O.

**Acceptance Criteria:**
- Given el repositorio público, when un usuario nuevo sigue el `README.md`, then encuentra un quickstart con `docker run -p 127.0.0.1:5000:5000 …` y `codenetix fix` (SM-1), el riesgo del socket de Docker, el modelo de seguridad del Sandbox, el uso de keys propias y la verificación de imágenes firmadas.
- Given el repositorio público, when se inspeccionan sus archivos raíz, then existen `SECURITY.md`, `LICENSE` (Apache-2.0), `CONTRIBUTING.md` exigiendo DCO (`git commit -s`) y los créditos a Eric Risco.

## Implementation Notes

- Creado `SECURITY.md` en español con divulgación privada vía GitHub Security Advisories (`https://github.com/N3rvs/codenetix/security/advisories/new`), tabla de versiones soportadas (última `vX.Y.Z`), plazos orientativos (acuse ≤ 72 h, evaluación ≤ 7 días) y modelo de seguridad/responsabilidades del operador.
- Actualizado `README.md`: eliminado aviso obsoleto de desarrollo, añadido Quickstart (< 10 min) con `ghcr.io/n3rvs/codenetix:v0.1.0` y `ghcr.io/n3rvs/codenetix-sandbox:v0.1.0` (`docker run -p 127.0.0.1:5000:5000 …`, `codenetix fix … --follow`, Panel `/data/admin.token`), verificación `cosign verify` + SBOM, sección de seguridad/riesgos/BYOK/presupuesto, preservada sección de GitHub Action, y añadidos créditos a Eric Risco + enlaces a `SECURITY.md`, `CONTRIBUTING.md` y `LICENSE`.
- Actualizado `docker/codenetix.Dockerfile` para copiar `SECURITY.md`, `LICENSE` y `CONTRIBUTING.md` en la etapa `build` antes de `dotnet test`.
- Creado `tests/CodeNetix.Host.Tests/ReleaseDocumentationTests.cs` con 4 pruebas estructurales. `dotnet build CodeNetix.slnx`: 0 errores, 0 advertencias; `dotnet test --solution CodeNetix.slnx`: 1034/1034 correctos.

## Spec Change Log

## Review Triage Log

> Nota: los subagentes `blind-hunter` y `edge-case-hunter` fallaron por cuota agotada (429); ambas capas se ejecutaron manualmente por el orquestador contrastando cada afirmación de la documentación con el código (`InstanceOptions`, `InstanceOptionsValidator`, `AdminTokenStore`, `SandboxManager`, `ClaudeCodeAgent`). `verification-gap` completó su revisión.

| # | Capa | Severidad | Hallazgo | Decisión |
|---|---|---|---|---|
| 1 | verification-gap | medium | Aserciones de seguridad del README evaluadas sobre todo el documento (falsos positivos por menciones previas). | **patch**: el test extrae la sección `## Seguridad, riesgos y uso de keys propias (BYOK)` y valida dentro de ella socket Docker/root, `internal: true`, `.git`, `no-new-privileges`, Egress `:8080/llm`/`:8081`, `Category=SecurityFire`, BYOK, presupuesto, `402`, `--max-budget-usd` y enlace a `SECURITY.md`. |
| 2 | verification-gap | medium | Quickstart (`docker exec … fix`, `--follow`, Panel, build local) y coherencia global de etiquetas semver no verificados. | **patch**: nuevas aserciones + `Assert.All` sobre todas las etiquetas concretas `ghcr.io/n3rvs/...:vX` (excluye marcadores `<tag>`) e `imagetools inspect` de ambas imágenes con la versión de `action.yml`. |
| 3 | verification-gap | low | `CONTRIBUTING.md` mencionaba `SECURITY.md` como código, no como enlace. | **patch**: `[SECURITY.md](SECURITY.md)` + aserción exacta. |
| 4 | verification-gap | low | El test del Dockerfile no cubría `README.md` ni `action.yml`. | **patch**: añadidos a la comprobación de `COPY` y `.dockerignore`. |
| 5 | blind/edge (manual) | — | Verificados: `CODENETIX__Limits__BudgetUsd`, `CODENETIX__Pricing__<Modelo>__*`, `/data/admin.token` con `0600`, usuario `1000:1000`, `ReadonlyRootfs`, `/tmp` tmpfs, `CapDrop ALL`, `no-new-privileges`, `--max-budget-usd`. | **dismiss**: documentación coherente con el código. |

Verificación final: `dotnet build CodeNetix.slnx` 0 errores/0 advertencias; `dotnet test --solution CodeNetix.slnx` 1034/1034 correctos.
