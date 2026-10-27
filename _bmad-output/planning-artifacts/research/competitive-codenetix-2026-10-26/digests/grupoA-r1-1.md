# Digest: Group A, round 1, part 1. "AI software factory" creators and concept

Accessed 2026-10-26 for every claim. Budget used: 14 web calls. Most sources were read through the fetch/search summarizer, so exact wording should be spot-checked before quoting.

## 1. Eric Risco de la Torre

| # | Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|---|
| 1.1 | He is an AI engineer at Andorra Telecom with 10+ years of experience and has taught AI/software at KeepCoding for 6+ years. | https://keepcoding.io/profesores/eric-risco-de-la-torre | KeepCoding | unknown | high | positioning |
| 1.2 | His GitHub (`ericrisco`) bio reads "AI Tech Lead @ Andorra Telecom", Andorra. It has 48 repos and roughly 157 followers. The only external link is X (@erisco_and). No YouTube, course or product links. | https://github.com/ericrisco | GitHub (own profile) | unknown | high | positioning |
| 1.3 | His flagship public repo is **rsc-harness** (npm `@ericrisco/rsc`, MIT, ~305 stars, 594 commits). It is a "meta-harness" that adds doc-based memory, tool folders, ~269 to 276 skills and rule-enforcing hooks to 17 or 18 coding assistants, including Claude Code, Codex, Copilot, Cursor, Gemini CLI, Windsurf, Cline, Aider, Jules and Kiro. Hard hook enforcement works only in Claude Code. | https://github.com/ericrisco/rsc-harness | GitHub (own repo) | unknown (copyright 2026) | high | feature |
| 1.4 | rsc-harness has a 10-phase spec-driven chain (specify, plan, tasks, implement, verify, review, ship...). The human approves the spec, and publishing always asks first. A review step sends 3 reviewers (correctness, security, tests) at a diff, then a `finding-verifier` agent tries to refute serious findings before they block a merge. This is close to the factory in the video, but it runs **inside the assistant session**. The README has no Sentry or GitHub-issue event triggers and no auto-merge. | https://github.com/ericrisco/rsc-harness | GitHub (own repo) | unknown | medium (summarizer read) | feature |
| 1.5 | No public repo is described as a "software factory", and no mention of Sentry triggers, auto-merge, or a paid tier, course or product appears on his GitHub or in the rsc-harness README. | https://github.com/ericrisco ; https://github.com/ericrisco/rsc-harness | GitHub | unknown | medium (absence of evidence) | trajectory |
| 1.6 | A LinkedIn post by Teodoro Ortiz de Artiñano credits Eric Risco (with Ruben Moral) for helping build **Saikaris**, described as an "AI-native company". This is a possible commercial vehicle, but unconfirmed. | https://es.linkedin.com/in/toash | LinkedIn (third party) | unknown | low | trajectory |
| 1.7 | The viral video (Oct 2026, 44k views, Opus spec, 4 reviewers, auto-merge policy) was **not found** by web search. | n/a | n/a | n/a | n/a | n/a |

**Unverified belief, not evidenced this run:** the video's pipeline is probably built on Claude Code plus rsc-harness skills and reviewers, wired to GitHub/Sentry events. This is inferred only from the overlap with 1.4.

## 2. The "software factory" concept, 2025-2026

| # | Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|---|
| 2.1 | Factory published "What is a software factory" on its own site (July 18, 2026). It defines a software factory as "a managed model for producing software with repeatable inputs, standardized tooling, measurable output", with Droids as the agent-native implementation. Factory owns the term in search results. | https://factory.com/articles/what-is-a-software-factory | Factory | 2026-07 | high | positioning |
| 2.2 | Factory has an articles hub titled "The agent-native software factory" and a "Factory 2.0" piece by Matan Grinberg and Eno Reyes. | https://factory.ai/articles | Factory | unknown | medium | positioning |
| 2.3 | Vercel blog "Building a software factory for AI SDK" (Aug 2026): an internal factory on the AI SDK repo. The team reports it authors 25 to 35% of merged PRs and closes 70 to 80% of issues after 4 weeks. | https://vercel.com/blog/building-a-software-factory-for-ai-sdk | Vercel | 2026-08 | medium (seen in search summary only, not fetched) | trajectory |
| 2.4 | Eric Zakariasson (Cursor) gave a talk, "Building Your Own Software Factory". Developers define intent, constraints, feedback loops and verification. | https://gist.github.com/intellectronica/2ea4f1ad8bb2df8bd203020f73b94792 | GitHub gist (third-party summary) | unknown | low | positioning |
| 2.5 | TrueFoundry published a "software factory agentic enterprise guide" (fleets of coding agents, governance of credentials, cost and quality). It is localized into 6+ languages, which suggests an SEO play on the term. | https://www.truefoundry.com/blog/software-factory-agentic-enterprise-guide | TrueFoundry | unknown | medium | positioning |
| 2.6 | Keith Rabois posted on LinkedIn about an "agent-native system that builds software". | https://www.linkedin.com/in/Keith | LinkedIn | unknown | low | sentiment |
| 2.7 | Small projects also use the label, e.g. PyPI `raceos-factory`, an "AI-native software factory" for a motorsport platform. | https://pypi.org/project/raceos-factory/1.0.1/ | PyPI | unknown | low | positioning |

**Assessment:** this is a clear 2026 trend. Factory, Vercel, Cursor (talk) and TrueFoundry all use "software factory" explicitly. The term is crowded and Factory dominates it, so it is weak as a differentiator on its own.

## 3. Factory (factory.ai, now redirecting to factory.com)

| # | Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|---|
| 3.1 | factory.ai returns 308 redirects to **factory.com**, which looks like a domain rebrand. | https://factory.ai/pricing | Factory | 2026 | high | trajectory |
| 3.2 | Pricing: Pro $20/mo, Plus $100/mo (~5x usage, Droid Computers), Max $200/mo (~10x), Teams $60/mo base + $40/seat (up to 10 seats). Business and Enterprise are custom. | https://factory.com/pricing | Factory | current (©2026) | high | pricing |
| 3.3 | Product surfaces: Desktop, CLI, SDK, cloud and local background agents, and an "agent-readiness dashboard". | https://factory.com/pricing | Factory | current | high | feature |
| 3.4 | Business adds SSO, SAML/SCIM, Zero Data Retention, audit logs and admin controls (model selection, **autonomy level**, deny lists, network policy). Enterprise adds dedicated compute, data residency and **on-premise deployment options**. On-prem is enterprise-only, sales-led. | https://factory.com/pricing | Factory | current | high | pricing |
| 3.5 | The pricing FAQ asks "Can I use my own keys?", but the answer was not visible. BYOK status is unverified. Third-party docs summaries mention free "Droid Core" models and prepaid Extra Usage credits. | https://factory.com/pricing ; https://usagepricing.com/blueprint/factory | Factory; aggregator | current | low | pricing |
| 3.6 | The deployment guide covers managed, hybrid, regional and **airgapped** patterns. | https://factory.com/articles/what-is-a-software-factory | Factory | 2026-07 | medium | feature |
| 3.7 | Review and merge: agent patches "should face the same tests, security checks, and release controls as a human-authored change", with the existing CI and review deciding readiness. The page describes no native multi-reviewer, risk-tiered merge policy. | https://factory.com/articles/what-is-a-software-factory | Factory | 2026-07 | medium | feature |
| 3.8 | Triggers: Linear integration (start a session from a Linear issue), Jira integration (recommended "Factory Droid" service account), and Sentry listing Factory as an issue-tracking integration that "pulls in Issues to help automate detecting and fixing issues". Marketing says "Point Droids at Linear, Jira, or GitHub issues and get working PRs back." | https://docs.factory.ai/software-factory/linear.md ; https://docs.factory.ai/onboarding/integrating-with-your-engineering-system/jira ; https://docs.sentry.io/organization/integrations/issue-tracking/factory ; https://factory.ai/industries/saas | Factory; Sentry | unknown | medium (search summaries, not fetched) | feature |
| 3.9 | Funding: Series C of $150M at a $1.5B post-money valuation (Apr 16, 2026), led by Khosla, with Sequoia, Insight, Blackstone, NEA and others. | https://tech-insider.org/factory-ai-150-million-series-c-khosla-coding-droids-2026/ | Tech Insider (secondary) | 2026-04 | medium | trajectory |
| 3.10 | A reported $200M round at a **$5B** valuation (Sep 15, 2026), with $400M+ total raised. A Dow Jones/Morningstar headline confirms the $200M round, but the valuation comes only from Sacra and Multiples.vc. | https://morningstar.com/news/dow-jones/202609155753/ai-software-developer-factory-gets-200-million-in-financing-round ; https://sacra.com/research/factory | Dow Jones; Sacra | 2026-09 | medium ($200M) / low ($5B) | trajectory |
| 3.11 | Sacra estimates ~$60M ARR in Mar 2026, up from ~$5M at end of 2025. This is an analyst estimate, not primary. | https://sacra.com/research/factory | Sacra | 2026 | low | trajectory |

## 4. Other products sold as an end-to-end issue-to-PR "software factory"

| # | Claim | Source | Publisher | Pub date | Conf. | Class |
|---|---|---|---|---|---|---|
| 4.1 | **Vercel "eve Software Factory" ("Foreman")**: a template on Vercel's eve framework for durable agents. Triggers are a GitHub issue labelled `factory`, a bot mention, or a delegated Linear issue (no Sentry). It has 4 subagents (classifier/triage, analyst/planner on a live checkout, implementer running repo checks in a sandbox, reviewer on a **different model vendor**), with up to 2 revision cycles. | https://vercel.com/templates/other/eve-software-factory | Vercel | unknown (likely 2026) | high | feature |
| 4.2 | Foreman merge policy: unattended runs stop at a **draft PR**, and agents have no merge capability at all. Pushes to the default branch are blocked in code. It self-repairs CI on its own PRs and pauses for a human after 2 attempts. | same | Vercel | unknown | high | feature |
| 4.3 | Foreman has no standalone dashboard (approvals live in GitHub comments, Linear and a dev TUI). The code is public under vercel-labs, but the license was not visible. It is effectively Vercel-bound (Vercel Connect, Blob, OIDC token). BYOK is not mentioned. | same | Vercel | unknown | medium | feature |

## Gap signals for the founder (inference, not evidence)

- **Factory:** SaaS-first. On-prem only through Enterprise sales, BYOK unconfirmed, closed source. Nothing seen of risk-tiered auto-merge or N independent reviewers.
- **Vercel Foreman:** open and close to the brief (multi-stage, cross-vendor reviewer). But it is Vercel-hosted, never auto-merges, has no Sentry trigger and no live pipeline dashboard.
- **Risco's rsc-harness:** MIT and has a multi-reviewer pattern. But it is an in-session harness, not an event-driven, self-hosted service.
- **No .NET-native option seen:** none of the three is .NET-native. The combination of self-hosted, BYOK, a deterministic risk-based merge policy and a live dashboard was not found in a single product this run. Group B and C research may contradict this.

## Leads worth chasing

- Eric Risco's X account (@erisco_and) and his LinkedIn activity feed, to find the actual video and tool stack. Also search YouTube for "Eric Risco" directly.
- **Saikaris** (company linked to Risco): what it sells, and whether the factory is its product.
- Fetch docs.factory.ai: the `software-factory/` docs section (the URL path suggests Factory now has a product area called "Software Factory"), the BYOK FAQ answer, the Sentry trigger flow, and any automated review or "autonomy level" merge controls.
- The vercel-labs Foreman GitHub repo: license, stack, star count, and recent commits.
- The Vercel AI SDK factory blog (Aug 2026) for the merge policy and model mix.
- Other candidates not covered this run: GitHub Copilot coding agent, Devin, OpenAI Codex cloud, Cursor background agents, Sweep, Tembo, CodeRabbit-style reviewers, and open-source OpenHands resolver flows.

## Looked for but could not find

- The viral Risco video or any transcript, view count or description of it.
- Any Risco repo, course or product described as an "AI software factory", or any commercialization plan.
- Factory's BYOK answer and any self-serve self-hosting option.
- Primary confirmation of Factory's $5B valuation.
- Any product explicitly combining Sentry triggers, multiple independent reviewers, risk-tiered auto-merge and self-hosting.
