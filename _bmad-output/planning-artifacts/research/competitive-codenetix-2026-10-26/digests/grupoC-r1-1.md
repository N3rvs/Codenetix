# Group C: Open-source agentic dev pipelines (digest r1-1)

Accessed: 2026-10-26 for every claim. The run was cut short after 13 of 20 tool calls because the user interrupted (they thought it was looping). Items 3 and 6 and part of item 5 are **not evidenced** and are listed under gaps.

## 1. OpenHands (All Hands AI)

| Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|
| Main repo is MIT-licensed. The page did not show an enterprise or non-MIT directory. | https://github.com/OpenHands/OpenHands | GitHub/OpenHands | 2026-10 | high | license |
| About 91.9k stars and 12.2k forks | same | GitHub | 2026-10 | high | trajectory |
| Releases are very frequent: v1.30.0 through v1.33.0 shipped 22–23 Oct 2026, several per day. | https://github.com/OpenHands/OpenHands/releases | GitHub | 2026-10 | high | trajectory |
| The local/desktop product is now branded **"OpenHands Agent Canvas"** (`npm i -g @openhands/agent-canvas`, "OpenHands Agent Canvas.app"). The repo page and the pricing tier both use the name. | repo + https://openhands.dev/pricing | OpenHands | 2026-10 | medium | positioning |
| Self-hosting options: local agent server, a single Docker sandbox, one Docker sandbox per conversation (`OH_CONVERSATION_RUNTIME=docker`), or a deployment on a VM or company infrastructure (SELF_HOSTING.md). | repo README | OpenHands | 2026-10 | high | feature |
| Triggers: "Automations" run on a schedule or a webhook. Named integrations are Slack, GitHub, Linear and Notion, plus Datadog as a trigger. Example: breaking GitHub issues into tasks. **Jira, GitLab and the old "resolver" were not mentioned** on the current page. | repo README | OpenHands | 2026-10 | medium | feature |
| Headless/API: an Agent Server with a REST and WebSocket API, a Python SDK, a TypeScript client, and a CLI with `--backend-only`. | repo README | OpenHands | 2026-10 | high | technical |
| **Pricing:** the free Agent Canvas tier is MIT, self-hosted, BYO LLM key, "works with any agent (Codex, Claude, etc.)" and includes Automations. **Teams** costs $499/mo for up to 10 users plus $50 per extra seat. It adds self-hosting, RBAC, SSO/SAML, budgeting and built-in integrations, with a cap of 5,000 sessions/mo. **Enterprise** is custom-priced, self-hosted and uncapped. | https://openhands.dev/pricing | OpenHands | unknown (©2026) | medium | pricing |
| Positioning shift: OpenHands now presents itself as a self-hosted **orchestrator of any coding agent** (Codex, Claude), with automations. It is open-core, and paid tiers add multi-user, RBAC and SSO. **This is close to Codenetix's intended positioning and its intended open-core model.** | pricing + README | OpenHands | 2026-10 | medium | positioning |
| Third-party aggregators disagree on cloud prices ($20/$50 per seat vs usage-based vs enterprise-only). They conflict with the primary pricing page and should be discarded. | costbench.com, aitoolsatlas.ai, aiagentrank.io | aggregators | 2026-03..08 | low | pricing |
| Funding: not retrieved this run. | — | — | — | — | trajectory |

**Software Agent SDK** (https://github.com/OpenHands/software-agent-sdk, 2026-10):
- MIT-licensed, Python core with a TypeScript client, about 1.3k stars. It is the engine behind the OpenHands CLI and Cloud (confidence: high; class: technical/license).
- Its Agent Server exposes REST and WebSocket and runs agents in ephemeral Docker or Kubernetes workspaces. **A C# orchestrator can drive it over HTTP or WebSocket without Python interop** (my inference from the API shape; confidence: medium).

Reviewers, merge policy and HITL dashboard: none were seen on the pages fetched. Not evidenced.

## 2. SWE-agent / mini-swe-agent

| Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|
| mini-swe-agent is a roughly 100-line, bash-only agent that "solves GitHub issues". It is now at v2. | https://github.com/SWE-agent/mini-swe-agent | GitHub/Princeton–Stanford | 2026-10 | high | feature |
| MIT-licensed, about 8.9k stars | same | GitHub | 2026-10 | high | license |
| Usable as a library: `pip install mini-swe-agent` with DefaultAgent, LitellmModel and Local/Docker/Podman/Singularity environments. Models come via litellm or OpenRouter. It needs no tool-calling, so it works with nearly any model. | same | GitHub | 2026-10 | high | technical |
| The README recommends mini-swe-agent as the "default choice" over full SWE-agent. SWE-agent is kept for tool and history experimentation. **It is not declared deprecated.** | same | GitHub | 2026-10 | medium | trajectory |
| Claims ">74% SWE-bench Verified". No model or date is given, so the figure is unverified marketing. | same | GitHub | 2026-10 | low | technical |

Triggers, dashboard, reviewers and merge policy: none (it is a component, not a pipeline).

## 3. Sweep and other OSS issue→PR bots

Not researched this run. My **unverified belief** from training data: Sweep pivoted away from the GitHub issue→PR bot to a JetBrains assistant. Aider, Cline, Roo and Kilo have CLI or headless modes. All of this needs checking against 2026 primary sources.

## 4. Headless orchestration feasibility

| Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|
| The Claude Agent SDK officially supports **Python and TypeScript only**. For other languages, the docs say to "run the CLI as a subprocess with `-p` and `--output-format json`". **There is no official C#/.NET SDK.** | https://code.claude.com/docs/en/agent-sdk/overview | Anthropic | 2026-10 | high | technical |
| The SDK "runs the Claude Code binary" and includes tools, hooks, subagents, MCP, permissions, sessions, skills and plugins. | same | Anthropic | 2026-10 | high | technical |
| **ToS:** third-party products may not offer claude.ai login or rate limits unless approved, so they must use API-key auth (BYO key works). Use falls under Anthropic Commercial Terms, including when powering your own customers' products. | same | Anthropic | 2026-10 | high | license |
| **Branding:** you may not call your product "Claude Code" or use look-alike visuals. "Powered by Claude" is allowed. | same | Anthropic | 2026-10 | high | license |
| Alternative: Anthropic **Managed Agents** runs a hosted harness with an Anthropic or self-hosted sandbox, reachable through a REST API or SDKs in several languages. That is a possible C# route, but it means Anthropic hosts the agent loop. | same | Anthropic | 2026-10 | medium | technical |
| `codex exec` supports a `--json` JSONL event stream (thread, turn and item events covering commands, file changes and MCP calls) and `--output-schema` for structured final output. | https://learn.chatgpt.com/docs/non-interactive-mode | OpenAI | 2026-10 | high | technical |
| `codex exec` sandboxes are read-only (default), `workspace-write` or `danger-full-access`. `--full-auto` was removed in favour of `--approve-for-me`. `--ephemeral` skips saved session files. | same | OpenAI | 2026-10 | high | technical |
| `codex exec` CI auth uses `CODEX_API_KEY` set per invocation, with a warning about repository-controlled code. | same | OpenAI | 2026-10 | high | technical |
| There is a Codex GitHub Action, `openai/codex-action@v1`, and a TypeScript Codex SDK. No other SDK languages are listed. | same | OpenAI | 2026-10 | medium | technical |
| The Codex CLI is Apache-2.0. This is only seen in forks/mirrors. The official LICENSE was not fetched. | github.com forks (e.g. loadingalias/codex) | GitHub mirrors | unknown | low | license |

What this means for a C# orchestrator: all three agents can be driven from .NET. Claude Code and Codex would run as subprocesses parsing JSON or JSONL streams. OpenHands would be driven over its Agent Server's REST/WebSocket API. None of the three offers a native .NET SDK (confidence: medium, synthesized from the rows above).

## 5. Semantic Kernel vs Microsoft Agent Framework

| Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|
| MAF is MIT-licensed and targets .NET and Python, with Go in a separate repo. About 14.3k stars and 3.7k commits. | https://github.com/microsoft/agent-framework | Microsoft | 2026-10 | high | license |
| MAF offers graph workflows (sequential, concurrent, handoff, group), **checkpointing, human-in-the-loop and time-travel**, middleware, OpenTelemetry, declarative YAML agents and DevUI. | same | Microsoft | 2026-10 | high | feature |
| MAF integrates Foundry, Azure OpenAI, OpenAI and the **GitHub Copilot SDK**. Claude Code is not mentioned. | same | Microsoft | 2026-10 | medium | feature |
| The README links migration guides from Semantic Kernel and AutoGen, which implies MAF is the forward path. It does not explicitly say "successor". | same | Microsoft | 2026-10 | medium | trajectory |
| MAF GA/1.0 date and SK's maintenance status: **not retrieved** (the search was interrupted). | — | — | — | — | — |

## 6. .NET-native open-source agentic dev pipeline

Not researched this run. No evidence either way.

## Leads worth chasing

- **OpenHands is the closest competitor.** It is MIT, self-hosted, BYO key, orchestrates Codex and Claude, runs event/webhook automations, and is open-core at $499/mo for Teams. Codenetix needs to differentiate on the spec → multi-reviewer → deterministic merge-policy flow, on .NET/enterprise-Microsoft shops, and on Sentry/Jira triggers. Verify whether OpenHands has reviewer agents or merge gates (fetch docs.openhands.dev for automations and integrations).
- Check whether OpenHands still supports **Jira and GitLab** and the old GitHub resolver, or dropped them in the Agent Canvas rewrite.
- MAF + GitHub Copilot SDK is a native .NET route to a coding agent. Check the Copilot SDK's .NET support, license and ToS.
- Anthropic Managed Agents with self-hosted sandboxes: check whether a C# REST client is viable and whether it conflicts with the "zero hosting for maintainer" goal (it shouldn't, since the user pays Anthropic).
- Fetch the official openai/codex LICENSE and the latest release date.
- Sweep's status, Aider/Cline/Kilo headless modes, and any .NET-native pipeline (search GitHub for "agent framework" + "pull request" + C#).

## Looked for but could not find (this run)

- OpenHands funding and its latest round. A release date for the Software Agent SDK (the Releases panel was empty).
- OpenHands reviewer agents, merge policy or pipeline dashboard.
- MAF 1.0 GA date and Semantic Kernel's deprecation or maintenance status.
- Primary-source Codex CLI license and release cadence.
- Everything in items 3 and 6.
