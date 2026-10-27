---
title: 'Addendum: CodeNetix Product Brief'
created: '2026-10-26'
updated: '2026-10-26'
---

# Addendum: detalle para el PRD y la arquitectura

## Decisiones técnicas preliminares (Winston)

- **Backend:** .NET organizado como monolito modular con Vertical Slices y un pequeño núcleo de dominio rico (agregado `PipelineRun` con su máquina de estados, y `MergePolicy`). Se descarta el DDD completo; se aplica la regla de tres antes de abstraer.
  - Estados: Recibido → Analizando → Programando → Revisando → Decidiendo → (Merge | PR manual | Fallido).
  - Módulos: Ingestion, Pipeline, Agents, Integrations, Api (con un hub de SignalR).
- **Persistencia:** SQLite embebido. Postgres será opcional, solo en Enterprise.
- **Ingesta:** webhooks con firma verificada, respuesta `200` inmediata, persistencia del evento y deduplicación por ID de entrega.
- **Modos de entrada:**
  - A) comando directo, sin fricción;
  - B) *polling* de issues con una etiqueta;
  - C) webhooks, que requieren servidor o túnel.
  - La GitHub Action evita el problema de la URL pública.
- **Agente programador:** se orquestan agentes existentes en lugar de construir uno propio.
  - Claude Code (`claude -p --output-format json`, como subproceso).
  - Codex (`codex exec --json`).
  - OpenHands (API REST/WebSocket).
  - Ninguno tiene SDK nativo de .NET.
- **Orquestación:** evaluar Microsoft Agent Framework (MIT; .NET; flujos con checkpoints, HITL y OpenTelemetry) frente a Semantic Kernel, siempre detrás de interfaces propias.
- **Sandbox:** contenedor aislado y efímero para ejecutar el código y los tests generados. Nunca en el host.
- **Política de merge:** determinista y definida en `codenetix.yml`: rutas protegidas (migraciones, `Domain/**`…), umbral de tamaño del diff y unanimidad de los revisores.
- **Seguridad:** solo escucha en localhost por defecto, con un token opcional por variable de entorno si se expone. Las API keys van en variables de entorno, nunca en la base de datos.
- **Matiz sobre el tipado estricto:** garantiza la *forma* de la respuesta, no su corrección. La corrección la aportan los tests y los revisores. No hay que prometer que "evita alucinaciones".

## Frontend

- **React con Vite**, React Flow (grafo de la cadena de montaje), xterm.js (terminal en vivo), `@microsoft/signalr` y shadcn/ui con Tailwind.
- El mismo proceso de .NET sirve el panel ya compilado.
- **Alternativas descartadas:**
  - Blazor: un solo lenguaje, pero menos ecosistema para grafos y terminal y menos contribuidores.
  - Angular: viable, pero dependería de wrappers para React Flow y xterm.js.
  - Astro: para el panel no encaja; sí para la web y la documentación (Starlight).

## Distribución y coste cero

- **Canales gratuitos:**
  - el repositorio público en GitHub;
  - la imagen en GHCR;
  - los binarios en GitHub Releases;
  - el CI en GitHub Actions;
  - la documentación en GitHub Pages.
- **Límites de GitHub Actions:**
  - 6 h por job;
  - gratis en repositorios públicos;
  - los *secrets* no llegan a PRs que vienen de forks.
- **Límite de Azure DevOps:** los agentes gratuitos permiten 60 min por job en proyectos privados.

## Licencias consideradas

- **MIT o Apache 2.0:** máxima adopción, mínima protección.
- **AGPL:** obliga a compartir los cambios si se ofrece como servicio.
- **FSL o BSL:** código visible, pero prohíbe competir durante un tiempo. No es open source en sentido estricto.
- **Open core:** el núcleo con licencia abierta y `enterprise/` con licencia comercial.
- **Recomendación provisional:** núcleo AGPL (o FSL), carpeta Enterprise comercial, CLA desde el primer día y marca registrada. Hay que validarlo con un abogado.

## Nombres considerados

- **CodeNetix (elegido):** Code + .NET + Cybernetics, y coincide con el nombre de la carpeta.
- **Alternativas:**
  - OrchestAI;
  - FabrIA (funciona en español, pero limita a nivel internacional);
  - Agentyx;
  - SynapSync.
- **Evitar** "harness" en el nombre: choca con la marca Harness.io.

## Ideas aparcadas para la hoja de ruta

- Fallos de CI/CD como disparador. Ya lo ofrecen GitHub, GitLab (manual), CircleCI y Harness; el valor de CodeNetix sería dispararlo automáticamente y pasarlo por la política de merge.
- Optimización de pipelines como tipo de tarea. Es un posible nicho libre, sin verificar.
- Linear, Azure DevOps y GitLab como fuentes y destinos.
- Ecosistema de revisores y políticas compartidas por la comunidad.

## Competencia

Ver el análisis completo con fuentes en `_bmad-output/planning-artifacts/research/competitive-codenetix-2026-10-26/research.md`.
