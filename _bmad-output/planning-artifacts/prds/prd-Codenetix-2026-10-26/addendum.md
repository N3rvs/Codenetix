---
title: 'Addendum: PRD CodeNetix'
created: '2026-10-26'
updated: '2026-10-26'
---

# Addendum técnico del PRD

Complementa al addendum del brief (`briefs/brief-Codenetix-2026-10-26/addendum.md`), que contiene el stack completo, la persistencia, el frontend y las licencias. Aquí solo va lo que ha surgido durante el PRD.

## Programador en la v1: Claude Code

- Se invoca como subproceso `claude -p --output-format json` dentro del Sandbox. No hay SDK oficial de .NET; los SDK oficiales son de Python y TypeScript (fuente [13] de `research.md`).
- Términos de uso: cada usuario usa su propia API key; no se ofrece el login de claude.ai; el producto no se llama "Claude Code", aunque "Powered by Claude" está permitido.
- Interfaz `ICodingAgent` propia para añadir Codex (`codex exec --json`) y OpenHands (REST) en la v1.x.

## Revisores

- Las salidas estructuradas (esquema JSON) se aplican al Veredicto y a la Spec.
- Recomendación: usar por defecto un Proveedor o modelo distinto del Programador en al menos un Revisor, siguiendo el precedente de Vercel Foreman.

## Modo GitHub Action

- Límites: 6 h por job y gratis en repositorios públicos. Sin estado entre ejecuciones: la traza vive en el log y en el PR.
- Riesgo: si se usa `pull_request_target` o `workflow_run` con código de forks, hay exposición de *secrets*. La v1 solo se dispara cuando se etiqueta una issue.

## Decisiones tomadas en el PRD

- Solo repositorios .NET (`dotnet build` / `dotnet test`).
- Revisores de la v1: seguridad, calidad y tests. Rendimiento e interfaz se aplazan; el de interfaz necesita visión o capturas.
- En la v1 el destino es siempre un PR; la Política de merge solo clasifica el riesgo.
