# Group D Digest, Round 1-1: CI-fix agents, factory-as-GitHub-Action, "harness engineering"

Accessed: 2026-10-26 for every claim. Budget used: 18 tool calls. The primary OpenAI post returned HTTP 403, so the OpenAI claims rely on secondary sources.

---

## D1. Agents that fix broken CI builds or optimize pipelines

| # | Claim | Source URL | Publisher | pub_date | Conf. | Class |
|---|---|---|---|---|---|---|
| 1.1 | GitLab Duo Agent Platform has a "Fix CI/CD Pipeline" flow. It reads the pipeline logs, the MR changes that may have caused the failure, the repo contents and the script errors. For MR pipelines it applies inline code suggestions on the source branch. In other cases it opens a new MR, or it only posts a comment (for example when context is insufficient or the failure is security-sensitive). | https://docs.gitlab.com/ci/pipelines/fix_pipeline/ | GitLab (official docs) | unknown (version history up to 19.5) | high | feature |
| 1.2 | The GitLab flow is **started by a user only**: the "Fix pipeline with Duo" button on the MR or the Pipelines page, or a request in Agentic Chat. The docs describe no automatic trigger on failure. | same | GitLab | unknown | high | feature |
| 1.3 | GitLab timeline: experiment in 18.4, GA in 18.8, added to the Free tier on GitLab.com with GitLab Credits (consumption-based) in 18.10, Agentic Chat use GA in 19.5. Offered on GitLab.com, Self-Managed and Dedicated. The docs name no model and do not mention bring-your-own-model. Only the last 150 KiB of job logs is processed. Customization goes through AGENTS.md. | same | GitLab | unknown | high | pricing/limits |
| 1.4 | Duo Agent Platform GA was announced on 2026-01-15. The announcement says Fix CI/CD Pipeline is a foundational flow in the 18.8 release cycle. | https://about.gitlab.com/press/releases/2026-01-15-gitlab-announces-duo-agent-platform-general-availability/ | GitLab | 2026-01 | medium (seen in search snippet, not fetched) | feature |
| 1.5 | The earlier GitLab Duo Root Cause Analysis (explains a failed job and suggests a fix, no code changes) has been GA since 17.3 (Aug 2024). | https://about.gitlab.com/releases/2024/08/15/gitlab-17-3-released/ | GitLab | 2024-08 | medium (stale) | feature |
| 1.6 | GitHub "Fix with Copilot" button on the logs page of a failed Actions run. The Copilot cloud agent investigates, pushes a fix to the branch and tags the user for review. Rolled out to Business and Enterprise first. | https://github.blog/changelog/2026-05-18-one-click-fixes-for-failing-actions-with-copilot-cloud-agent/ | GitHub changelog | 2026-05 | medium (seen in search snippet) | feature |
| 1.7 | Fix with Copilot for failing Actions extended to Copilot Pro, Pro+ and Max. An admin may need to enable the cloud agent. | https://github.blog/changelog/2026-06-04-fix-with-copilot-for-failing-actions-now-in-pro-pro-and-max/ | GitHub changelog | 2026-06 | medium | pricing |
| 1.8 | GitHub Mobile can also trigger Fix with Copilot on a failed check. It opens a PR on top of the existing PR. | https://github.blog/changelog/2026-07-23-github-mobile-fix-failing-actions-checks-with-copilot-cloud-agent | GitHub changelog | 2026-07 | medium | feature |
| 1.9 | Harness AI explains failed CI/CD steps from the logs: it correlates errors with known issues and suggests fixes. It must be enabled in Account Settings and is opened from the logs of the failed step. | https://developer.harness.io/docs/platform/harness-ai/core-capabilities/in-harness-ui/ci-agent | Harness (official docs) | unknown | medium | feature |
| 1.10 | The Harness AI DevOps Agent builds, configures and troubleshoots pipelines from natural-language prompts. Its error analyzer correlates recent changes, dependencies and historical patterns. This is the closest competitor to a "pipeline optimization" task type, but it runs inside the Harness platform. | https://developer.harness.io/3k-docs/ai/devops-agent | Harness | unknown | medium (search snippet) | feature |
| 1.11 | CircleCI "Chunk" is an autonomous agent that diagnoses failed builds, proposes PRs that fix them, and fixes flaky tests. It can run on a schedule, on a specific failed build at a user's request, or from chat. Its fix loop is: generate the fix, validate it in the CI pipeline, retry if it fails. It is built on Claude. | https://circleci.com/chunk/ ; https://claude.com/customers/circleci | CircleCI / Anthropic (vendor marketing) | 2025-09 launch blog; page undated | medium (marketing register) | feature/positioning |
| 1.12 | OpenAI publishes an official recipe for **auto-fixing CI failures** with the Codex CLI in GitHub Actions. A `workflow_run` workflow is gated on `conclusion == failure`, runs `codex exec` and opens a PR. It needs an `OPENAI_API_KEY` secret and needs Actions to be allowed to create PRs. | https://developers.openai.com/cookbook/examples/codex/autofix-github-actions | OpenAI | 2025-09 | medium-high (official, a little stale) | feature |
| 1.13 | OpsMx markets an "AI DevOps remediation agent" for pipelines, infrastructure and deployments. | https://www.opsmx.com/ai-devops-remediation-agent/ | OpsMx (marketing) | unknown | low | positioning |

**What this means for the founder (inference, not evidence):** fixing CI failures is a **commoditized feature** among platform vendors (GitHub, GitLab, CircleCI, Harness) as of mid-2026. All of them are tied to their own platform, credits or seats. The OSS route (the Codex cookbook) is DIY YAML. Open gaps for a self-hosted, BYO-key option: an automatic trigger (GitLab's flow is manual), a review gate that is independent and deterministic, and Azure DevOps or multi-forge support.

## D2. Running an agent pipeline as a GitHub Action: feasibility and limits

| # | Claim | Source URL | Publisher | pub_date | Conf. | Class |
|---|---|---|---|---|---|---|
| 2.1 | A job on a GitHub-hosted runner can run for at most **6 hours**. A self-hosted runner job can run for 5 days. A workflow run can last at most 35 days, including waiting and approval time. | https://docs.github.com/en/actions/reference/limits | GitHub Docs | unknown (live page) | high | limits |
| 2.2 | Concurrent jobs: Free 20, Pro 40, Team 60, Enterprise 500. Re-runs: 50 per run. Trigger events: 1,500 per 10 s per repo. GITHUB_TOKEN: 1,000 requests per hour per repo (15,000 on Enterprise Cloud). | same | GitHub Docs | unknown | high | limits |
| 2.3 | Actions are **free for public repos** on standard GitHub-hosted runners. Monthly minutes included for private repos: Free 2,000, Pro 3,000, Team 3,000, Enterprise Cloud 50,000. The page shows no last-updated date and no 2026 self-hosted platform fee. | https://docs.github.com/en/billing/concepts/product-billing/github-actions | GitHub Docs | unknown | medium-high (page undated, so check for 2026 billing changes) | pricing |
| 2.4 | `anthropics/claude-code-action` (MIT, about 9.7k stars, v1.0). It triggers on @claude mentions, issue assignments, or automation runs with an explicit prompt. Providers: Anthropic API, Bedrock, Vertex AI, Microsoft Foundry. Keys go in repo secrets, and a repo admin is required to add them. | https://github.com/anthropics/claude-code-action | Anthropic (GitHub repo) | 2026 (footer) | high | feature |
| 2.5 | `openai/codex-action@v1` wraps `codex exec` and offers `sandbox` and `safety-strategy: drop-sudo` options. Only third-party write-ups described it. The official cookbook installs the CLI directly. | https://codex.danielvaughan.com/2026/05/22/codex-cli-github-actions-best-practices-limitations-gotchas/ | Independent blog | 2026-05 | low-medium | feature |
| 2.6 | The `workflow_run` + failure-conclusion pattern is the established way to fire on a broken build (see 1.12). Native triggers already cover issues, labels and comments. | https://developers.openai.com/cookbook/examples/codex/autofix-github-actions | OpenAI | 2025-09 | medium | feature |
| 2.7 | **Secrets are withheld from `pull_request` runs started by a fork**, Dependabot included. `pull_request_target` exposes secrets but is risky if it runs untrusted PR code. This affects BYO-key agents on OSS repos that accept outside contributions. | https://runs-on.com/github-actions/pull-request-vs-pull-request-target/ (official GitHub docs not retrieved) | RunsOn (third party) | unknown | medium (matches a widely known GitHub behavior, but the official doc was not fetched this run) | limits |
| 2.8 | Azure Pipelines free Microsoft-hosted agents: **60 min per job for private projects, 360 min for public projects**. A paid hosted parallel job allows 360 min for any repo, and self-hosted agents are another option. One anecdotal report says free parallelism grants for public projects can take weeks. | https://learn.microsoft.com/azure/devops/pipelines/troubleshooting/troubleshooting?view=azure-devops ; https://learn.microsoft.com/en-us/answers/questions/2265112/ | Microsoft Learn / Q&A | unknown | medium-high (official docs, seen via search) / low (anecdote) | limits |

**What this means for the founder (inference):** a 6-hour job cap is ample for spec → code → test → review cycles. Free minutes on public repos plus BYO-key secrets make "zero maintainer hosting cost" realistic on GitHub. Constraints:
- Fork PRs get no secrets.
- Agent state does not persist between runs, so state must live in the repo, issues or artifacts.
- Azure DevOps free private jobs are capped at 60 minutes, which is tight for a multi-agent pass.
- Claude-code-action and the Codex recipe already occupy the "agent as an Action" slot. The factory would need to differentiate through orchestration (multiple independent reviewers, a deterministic merge policy), not through being an Action.

## D3. "Harness engineering"

| # | Claim | Source URL | Publisher | pub_date | Conf. | Class |
|---|---|---|---|---|---|---|
| 3.1 | Mitchell Hashimoto's "My AI Adoption Journey" has six steps. Step 5 is "Engineer the Harness". He has "grown to calling this 'harness engineering'": whenever the agent makes a mistake, you engineer a fix so it never repeats it, either through AGENTS.md (implicit prompting) or through programmed tools such as screenshot scripts and filtered tests. He explicitly says he is **not** trying to invent a term. | https://mitchellh.com/writing/my-ai-adoption-journey | Mitchell Hashimoto (primary) | 2026-02 (Feb 5) | high | trend |
| 3.2 | OpenAI published "Harness engineering: leveraging Codex in an agent-first world" (Ryan Lopopolo, about 2026-02-11). A small team built an internal beta product of about 1M lines of code with no hand-written code. The harness is defined as the scaffolding, constraints and feedback loops around the agent. The primary page returned 403, and secondary sources give conflicting throughput numbers. | https://www.infoq.com/news/2026/02/openai-harness-engineering-codex (primary https://openai.com/index/harness-engineering/ not retrieved) | InfoQ | 2026-02 | medium | trend |
| 3.3 | Later diffusion, all from secondary blogs: a commentary on martinfowler.com by Birgitta Böckeler (about 2026-02-17); LangChain's framing "Agent = Model + Harness" (about March 2026), with Terminal-Bench 2.0 gains claimed from changing only the harness; an Anthropic harness pattern (about March 2026). None was verified against a primary source. | https://www.heyuan110.com/posts/ai/2026-05-08-harness-engineering-window-of-opportunity/ ; https://lyzr.ai/blog/harness-engineering-for-ai-agents/ | Independent blogs / vendor | 2026-05 | low | trend |
| 3.4 | Signs of adoption: a freeCodeCamp tag, a Zenn book (JP), guides in IT and PT/ES, a Fragmented podcast episode, a Towards AI piece titled "the layer that matters more than the model", and vendors (Lyzr, agentboard) using the term in content marketing. This shows the term spread internationally and across practitioners within about 3 months of February 2026. | search results (freecodecamp.org/news/tag/harnessengineering, zenn.dev/kenimo49/books/harness-engineering-guide, fragmentedpodcast.com/episodes/307) | various | 2026-02..05 | medium (breadth of signal, depth not verified) | trend |
| 3.5 | Positioning risk: **"Harness" is also the brand of Harness.io**, a CI/CD vendor that sells an "AI DevOps Agent" (see 1.9 and 1.10). Using "harness" in product copy for a CI/CD-adjacent tool invites search collisions and brand confusion. | https://developer.harness.io/3k-docs/ai/devops-agent | Harness | unknown | high (fact) / inference (risk) | positioning |

**Verdict (inference):** "harness engineering" is a **real practitioner trend** with two credible primary origins in February 2026 (Hashimoto's blog, then OpenAI's Codex post), not pure vendor buzz. It is now being absorbed into content marketing, and one blog argues it is a "window of opportunity, not a forever moat". It fits well as a concept the founder can explain: the factory is a harness, with spec, tests, independent reviewers and a deterministic merge policy. It is weak as a product name or tagline because of the collision with Harness.io and because the term may fade. Search data on freshness and peak was not retrieved.

---

## Leads worth chasing
- Fetch the OpenAI harness-engineering post through a mirror or archive to confirm the date, the numbers and whether it describes review agents or a merge policy (very similar to the founder's design).
- Check whether GitHub Copilot or GitLab offer an **automatic** trigger on CI failure, not only a button. GitLab docs show manual only. GitHub's changelog wording suggests a button too.
- GitLab Credits pricing per flow run, and Copilot premium-request cost per "Fix with Copilot" run.
- Anthropic's harness posts ("Effective harnesses for long-running agents", about Nov 2025?) and the LangChain "Agent = Model + Harness" post, as primary sources.
- Official GitHub docs on fork secrets and `workflow_run` security (the `workflow_run` trigger runs with base-repo secrets, which is a poisoned-artifact risk).
- Buildkite AI features (not searched, budget ran out). OSS "fix failing CI" bots beyond the Codex cookbook, for example Sweep and similar (not searched).
- Azure DevOps: any Copilot or agent equivalent for fixing failed Azure Pipelines runs.
- Whether GitHub changed Actions billing in 2026 (for example a self-hosted runner platform fee). The billing page shows no date.

## Looked for but could not find
- The primary OpenAI harness-engineering page: HTTP 403.
- Pricing for Harness AI or the DevOps Agent, and any self-hosted or BYO-model option.
- BYO-model support or the model used in GitLab's Fix CI/CD Pipeline flow: not stated in the docs.
- A CI-failure-fixing example in the claude-code-action README: none on the landing page.
- Pipeline **optimization** (speed or cost) as a distinct agent task type at any vendor. Only Harness's DevOps Agent ("build, configure") comes near it, and CircleCI focuses on flaky tests. This may be white space, but that conclusion is unverified.
- Buildkite AI features and dedicated OSS fix-CI bots: not reached within the budget.
