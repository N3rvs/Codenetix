# Digest: Group B, commercial platforms (round 1, agent 1)

Accessed 2026-10-26 for every claim. Format: claim | source | publisher | pub_date | confidence | class.
Caveat: some facts came from search-result synthesis over official domains rather than a full page fetch. Those are marked "(search snippet)" and capped at medium confidence.

---

## 1. Sentry Seer

| # | Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|---|
| S1 | Seer scans issues automatically as they arrive. It runs root-cause analysis (RCA), scores fixability, and adds an initial guess to the issue page. No user request is needed. | https://docs.sentry.io/product/ai-in-sentry/seer/ | Sentry docs | unknown | high | feature |
| S2 | Seer "always performs root cause analysis and solution planning" before writing code, so there is a plan step (a rough equivalent of a spec). | https://docs.sentry.io/product/ai-in-sentry/seer/ | Sentry docs | unknown | high | feature |
| S3 | Automatic runs are gated. Automation must be on, the issue needs at least 10 events in the last 14 days, and the ML fixability score must be high enough. A per-project setting stops automation at Root Cause, Plan, or PR Drafted. | https://docs.sentry.io/product/ai-in-sentry/seer/autofix/ (search snippet) | Sentry docs | unknown | medium | feature |
| S4 | Seer never merges. Its most autonomous setting ends at a drafted PR, and a human reviews and merges it. | https://docs.sentry.io/product/ai-in-sentry/seer/autofix/ ; https://blog.sentry.io/automated-debugging-workflow-sentry/ (search snippet) | Sentry | unknown | medium | feature |
| S5 | Instead of writing the code itself, Seer can hand implementation to Claude Code, Cursor Cloud Agents, or GitHub Copilot. | https://docs.sentry.io/product/ai-in-sentry/seer/ | Sentry docs | unknown | high | feature/positioning |
| S6 | Seer includes AI Code Review on GitHub and GitLab.com, which catches bugs before merge. | https://docs.sentry.io/product/ai-in-sentry/seer/ | Sentry docs | unknown | high | feature |
| S7 | Seer supports only GitHub.com and GitLab.com cloud. Self-hosted Sentry and GHES/GitLab self-managed are not supported. | https://docs.sentry.io/product/ai-in-sentry/seer/ | Sentry docs | unknown | high | feature |
| S8 | The docs do not name the underlying model provider. No bring-your-own-key (BYOK) is documented. Sentry does not train on customer data by default. | https://docs.sentry.io/product/ai-in-sentry/seer/ | Sentry docs | unknown | medium (absence) | feature |
| S9 | Seer is a paid add-on and needs a Team plan or higher. Base plans: Team $29/mo ($26 annual), Business $89/mo ($80 annual), Enterprise custom, Developer free with no Seer. | https://sentry.io/pricing/ | Sentry | unknown (live page) | high | pricing |
| S10 | Seer costs $40 per active contributor per month, with unlimited use. An active contributor is anyone who opens 2 or more PRs a month in a Seer-enabled repo. GitHub [bot] accounts are excluded. Flat pricing was announced 2026-01-27. Before 2026 the model was $20/mo plus $25 of credits. | https://docs.sentry.io/pricing/ ; https://docs.sentry.io/pricing/quotas/manage-seer-budget/ (search snippet) | Sentry docs | 2026-01 | medium (the pricing page itself does not show the $40) | pricing |
| S11 | Seer Agent (open beta) adds conversational investigation and actions such as dashboards, monitors, and a Slack surface. It can also pull data from Datadog and GCP. | https://docs.sentry.io/product/ai-in-sentry/seer/ | Sentry docs | unknown | high | trajectory |
| S12 | Seer is expanding to local development and code review (press release), and there is a Sentry Agent for Linear. | https://sentry.io/about/press-releases/sentry-expands-seer-ai-debugging-agent/ ; https://docs.sentry.io/organization/integrations/issue-tracking/sentry-linear-agent (titles only) | Sentry | unknown | low (not opened) | trajectory |

Gaps relevant to the founder: no separate multi-perspective reviewers on its own fixes, no tiered merge policy because it never merges, no .NET-specific feature found, cloud only, and no BYOK.

## 2. GitHub Copilot coding agent (now "Copilot cloud agent")

| # | Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|---|
| C1 | The product is now called "Copilot cloud agent." You can start it from the agents panel, a conversation, or an issue or PR. Automations run on a schedule or in response to events through GitHub Agentic Workflows, which run as Actions workflows. Teams and Slack delegation is available. | https://docs.github.com/en/copilot/concepts/agents/coding-agent/about-coding-agent | GitHub Docs | unknown (live) | high | feature |
| C2 | It works in an ephemeral cloud environment, edits files, and runs tests and linters. The docs say session logs "do not replace your own review and testing." The page does not describe any auto-merge. | same | GitHub Docs | unknown | high | feature |
| C3 | It "only works with repositories hosted on GitHub." No GHES support is stated on that page. | same | GitHub Docs | unknown | medium (GHES absence not confirmed elsewhere) | feature |
| C4 | Copilot for Jira: public preview 2026-03, enhancements 2026-04, general availability 2026-06-25. You can trigger it by assigning, by @mention, or by a Jira automation rule. It reads acceptance criteria and custom fields and streams progress into a Jira panel. It requires Jira Cloud with Rovo enabled. | https://github.blog/changelog/2026-06-25-github-copilot-for-jira-is-now-generally-available/ ; https://docs.github.com/en/copilot/how-tos/copilot-integrations/integrate-cloud-agent-with-jira (search snippet) | GitHub | 2026-06 | medium-high | trajectory/feature |
| C5 | Azure Boards, Jira, and Linear integrations "only support creating a pull request directly." | GitHub docs (search snippet) | GitHub | unknown | medium | feature |
| C6 | Copilot code review can run automatically. Security campaigns can assign code-scanning alerts to Copilot to fix. Custom agents, skills, MCP, and hooks are supported. | about-coding-agent page | GitHub Docs | unknown | high | feature |
| C7 | Usage-based billing started 2026-06-01. Premium requests were replaced by token-priced "GitHub AI Credits." Base prices are unchanged: Pro $10, Pro+ $39, Business $19/user, Enterprise $39/user, each including that amount in credits. Agentic sessions draw credits, and cloud agent and code review also use Actions minutes. | https://github.blog/news-insights/company-news/github-copilot-is-moving-to-usage-based-billing/ | GitHub Blog | 2026-04 | high | pricing |
| C8 | Under the legacy model, the cloud agent cost 1 premium request × the model multiplier per session and per steering comment, with $0.04/request overage. | https://docs.github.com/en/copilot/concepts/billing/copilot-requests (search snippet) | GitHub Docs | legacy | medium (superseded) | pricing |
| C9 | Users can choose the model, including premium models. Availability depends on plan and policy. | about-coding-agent page | GitHub Docs | unknown | high | feature |
| C10 | Enterprise BYOK (Anthropic, Foundry, OpenAI, xAI, plus Bedrock, Google AI Studio, and OpenAI-compatible as of 2026-01) exists for Copilot Chat, CLI, SDK, and app. **No evidence that BYOK applies to the cloud agent.** | https://github.blog/changelog/2026-01-15-github-copilot-bring-your-own-key-byok-enhancements/ ; https://github.blog/changelog/2026-06-23-github-copilot-app-support-for-byok/ (search snippet) | GitHub | 2026-01 / 2026-06 | medium | feature |

## 3. Devin (Cognition)

| # | Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|---|
| D1 | Plans: Free $0, Pro $20/mo, Max $200/mo, Teams $80/mo plus $40 per dev seat (up to 200 users), Enterprise custom. Quotas refresh daily and weekly, and extra usage is billed at API pricing. The pricing page no longer mentions ACUs. | https://devin.ai/pricing | Cognition | unknown (live) | high | pricing |
| D2 | Enterprise offers VPC deployment, a "dedicated deployment option," SAML/OIDC SSO, and teamspace isolation. No BYOK or data-residency statement was found. | https://devin.ai/pricing | Cognition | unknown | high (BYOK absence: medium) | feature |
| D3 | Listed integrations: Linear, Jira, Slack, Teams, GitHub, GitLab, Bitbucket, and custom git providers. | https://devin.ai/pricing | Cognition | unknown | high | feature |
| D4 | Automations triggers: schedule, webhook, GitHub events (issue, comment, PR, review, push, check_run with CI-failure filter), Jira and Linear events, GitLab, Slack, Pylon, and PagerDuty. Multiple triggers combine as OR. Templates include "Fix CI Failures" and "Fix Sentry Errors Daily." | https://docs.devin.ai/product-guides/automations | Devin Docs | unknown | high | feature |
| D5 | Sentry works through the MCP Marketplace (OAuth) or an alert webhook into the Devin API, with one session per error. The docs suggest a 50+ events/hour threshold. | https://docs.devin.ai/enterprise/integrations/sentry ; https://docs.devin.ai/use-cases/gallery/api-sentry-auto-triage (search snippet) | Devin Docs | unknown | medium | feature |
| D6 | The Automations docs describe Devin opening PRs. They describe no merge policy, no human approval gate, and no separate reviewer agent. Guardrails are preflight scripts or LLM filters, ACU budgets, rate limits, and network policies. | https://docs.devin.ai/product-guides/automations | Devin Docs | unknown | high (absence within that page only) | feature |
| D7 | Cognition raised more than $2B in a Series E at a $48B valuation (2026-09-08; led by a16z and Accel). The Series D was more than $1B at $26B post-money (2026-05). Run-rate revenue is near $900M. | https://dealroom.co/news/149496-cognition-raises-2b-series-e-at-48b-valuation-to-scale-devin/ ; https://www.channelinsider.com/ai/news-cognition-devin-2b-funding-48b-valuation/ | trade press / aggregators | 2026-09 | medium (no primary source opened) | trajectory |
| D8 | Complaints: it gets stuck in loops, makes wrong assumptions, and runs for a long time in the wrong direction (Gartner Peer Insights reviewer). In a 2025 team test it succeeded on 3 of 20 tasks. | https://www.gartner.com/reviews/product/devin-ai-568760006 ; https://news.hada.io/topic?id=18787 | Gartner Peer Insights; GeekNews | unknown; 2025-01 | low (stale and indirect) | sentiment |

## 4. Atlassian Rovo Dev / Jira Coding Agent

| # | Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|---|
| A1 | Rovo Dev is reaching end of life as a standalone product. From 2026-06-22, "Rovo Dev in Jira" became the **Jira Coding Agent**. The CLI, IDE, and AI code review stay under Rovo Dev Standard. | https://www.atlassian.com/software/rovo-dev/pricing ; https://support.atlassian.com/rovo/docs/rovo-dev-rollout/ (search snippet) | Atlassian | 2026-06 | medium | trajectory |
| A2 | Rovo Dev Standard costs $20 per developer per month, includes 2,000 Rovo Dev credits, and charges $0.01 per credit above that. The free tier is 350 credits per user per site. The allowance covers roughly 20–75 code reviews or 5–50 CLI tasks. | https://support.atlassian.com/subscriptions-and-billing/docs/how-billing-works-for-rovo-dev-standard/ (search snippet) | Atlassian | unknown | medium | pricing |
| A3 | The Jira Coding Agent runs in a cloud sandbox from a work item, with Jira, Confluence, and Teamwork Graph as context. The user reviews and edits, then opens a PR. It consumes Rovo credits as a "premium AI feature," and the announcement says it is built into every paid Jira plan. | https://support.atlassian.com/jira-software-cloud/docs/what-is-the-jira-coding-agent/ | Atlassian | unknown | high (credits-per-run not stated) | feature/pricing |
| A4 | When run from Jira automation it opens a **draft** PR and never merges. It cannot exceed the initiating user's permissions. | https://support.atlassian.com/jira-software-cloud/docs/work-with-jira-coding-agent-in-automations/ (search snippet) | Atlassian | unknown | medium | feature |
| A5 | Jira can assign work items to third-party agents (Claude Code, Cursor, GitHub Copilot). Since 2026-06 it can also launch local agents from the Development panel. Jira is positioning itself as the orchestrator, not necessarily the coder. | https://support.atlassian.com/jira-software-cloud/docs/use-third-party-coding-agents-in-jira-automation/ ; https://www.atlassian.com/blog/company-news/ai-sdlc (search snippet) | Atlassian | 2026 | medium | positioning |
| A6 | Bitbucket "Agentic Pipelines" exists. | https://support.atlassian.com/bitbucket-cloud/docs/agentic-pipelines/ (title only) | Atlassian | unknown | low | trajectory |
| A7 | Spec or plan step, test generation, model choice, BYOK, and data residency are not stated on the Jira Coding Agent page. | what-is-the-jira-coding-agent | Atlassian | unknown | medium (absence) | feature |

---

## Cross-cutting read for the founder (inference, not evidence)
- **Every platform stops at a PR (draft or regular).** None documents a deterministic tiered merge policy, such as auto-merging small unanimous changes while gating DB or business-logic changes. Seer explicitly never merges, Atlassian never merges, and Copilot and Devin describe no auto-merge. This is the clearest candidate gap.
- **No platform documents separate reviewer agents (security, performance, UI) voting on the agent's own output.** Reviewers exist only as standalone PR-review products (Seer Code Review, Copilot code review, Rovo Dev code review).
- **Self-hosting and BYOK are weak everywhere.** Seer is cloud only. The Copilot cloud agent is GitHub.com only, and its BYOK is not shown to cover the agent. Devin offers VPC or dedicated deployment at Enterprise tier, but no BYOK was found. Atlassian is cloud with no BYOK stated.
- **Incumbents are converging on interoperability.** Seer hands off to Copilot, Cursor, or Claude Code. Jira assigns to third-party agents. Copilot listens to Jira. An event-driven orchestrator that plugs into these is consistent with the market, but it competes with Devin Automations, which already covers Sentry, Jira, GitHub, and CI triggers.
- **No .NET-specific capability was found for any of the four.**

## Leads worth chasing
1. Seer Autofix docs page (full fetch) and the "self-healing workflow" cookbook: exact automation thresholds, and whether Seer writes or runs tests.
2. Copilot cloud agent on GHES and data residency: check the GHE.com data-residency docs and the GHES release notes.
3. Whether Copilot BYOK extends to the cloud agent; check the GitHub changelog after 2026-06.
4. Devin Review (a separate review product?) and the merge or approval controls in the Devin docs.
5. Credits per Jira Coding Agent run, and the supported repos (Azure DevOps?).
6. Copilot cloud agent sentiment on GitHub Community discussions and Hacker News. The search with reddit.com in the allowed domains failed with an API error, so no Copilot sentiment was gathered.
7. Primary Cognition press release for the Series E.

## Looked for but could not find
- A spec-first artifact distinct from a plan, for any vendor. Seer has a plan step. The others are not documented.
- Auto-merge or a risk-tiered merge policy, for any vendor.
- BYOK for Seer, the Copilot cloud agent, Devin, or the Jira Coding Agent.
- A live pipeline dashboard: only Jira progress streaming (Copilot for Jira) and session logs. No cross-agent pipeline view found.
- .NET-specific support.
- A Sentry trigger for Copilot; it exists only as a Seer handoff to Copilot.
- Fresh (12 months or less) first-hand 1–3★ complaints for Seer, Copilot agent, or Rovo. Devin sentiment is stale (2025) and indirect.
