# Security and governance

## Threat model

Issue titles, bodies and comments are written by people, possibly untrusted. Agents execute with tools. The
risks to design against:

- **Prompt injection** through issue content that tries to steer an agent.
- **Privilege escalation** by an agent granting itself approval or reaching beyond its job.
- **Secret exposure** through tool output, logs or comments.
- **Unreviewed code** reaching the main branch.

## Multi-repository risks

- **Wrong target.** A request must not reach a repository outside the allowlist.
- **Cross-target leakage.** Credentials or content from one target must never reach another.
- **Hostile target content.** A target's files, including `.forge/config.yml`, are untrusted data.

## Controls

| Control | How |
|---|---|
| Untrusted input | Request and PR data are passed as a JSON data block. The prompt states it is data, never instructions |
| Target allowlist | Intake rejects any `target` not on the engine's allowlist |
| Scoped access | A short-lived token per run, limited to the one target repository and the permissions needed |
| Config can only narrow | `.forge/config.yml` may tighten limits; it can never add capabilities, paths or approval bypasses |
| Isolation | One job and one checkout per request. No state or credentials shared across requests or targets |
| Read-only agents, safe outputs | The agent job has no write access. Its outputs are checked by a detection step and applied by a separate job ([ADR-0009](decisions/0009-github-agentic-workflows.md)) |
| Fixed destination | `target`, `issue` and the checkout come from the dispatch inputs. The agent cannot choose another repository |
| Protected files | `gh aw` refuses to change `CODEOWNERS`, `README.md`, `AGENTS.md`, lockfiles and manifests without a review request |
| Scope gate | Before review, every changed file must be in the plan directory or the paths the target declares. Applied after the push, so it blocks rather than prevents |
| Validation before credentials | The orchestrator validates the event and the allowlist before it creates any token |
| No GitHub access for agents | The agent never receives a GitHub token. The workflow posts comments, applies labels and pushes |
| Label allowlist | Only listed labels can be applied from a result. `ready-for-dev` is not on the list |
| Write scope | Each agent may commit only its `write_paths`. Everything else in the working tree is discarded |
| Capability restriction | Agents declare capabilities. Runners enforce them or refuse to run |
| Human approval | A human adds `ready-for-dev` and merges. Reviewer findings send work back to the Planner, and the revised plan needs approval again |
| Trusted authors | The orchestrator reads result comments only from the Actions bot |
| Webhook authenticity | The relay verifies the webhook signature and drops anything invalid |
| Minimal dispatch | The dispatch carries only identifiers. The engine re-reads the issue with a scoped token and never treats the payload as content |
| Submitter authority | `forge` and `forge:<provider>` count only when added by a human with write access to the target, checked by the engine from the sender, not from the payload's claims |
| Approver authority | `ready-for-dev` needs a human with write access ([ADR-0011](decisions/0011-approval-by-write-access.md)). Ownership is enforced at merge by the target's branch protection, not by the engine |
| No self-approval | Whoever opened the request cannot approve its plan, unless `FORGE_ALLOW_SELF_APPROVAL` is set ([ADR-0012](decisions/0012-approval-policy.md)) |
| Sensitive paths | A plan touching `FORGE_SENSITIVE_PATHS` needs an approver with the admin or maintain role. Checks the plan's paths and the real changed files, by whole segment, case-insensitively. Fails closed |
| Loop prevention | Events caused by the App's own bot are ignored, so an agent can never start or approve a stage by labeling |
| Idempotent stages | Redelivered webhooks are safe: each stage checks existing labels and results first |
| Bounded runs | `max_turns`, `timeout_minutes` and one orchestration per issue |
| Output hygiene | Runners never write provider error bodies or credentials to results or logs |

## Secrets

- One secret per provider, stored as Actions secrets. A runner reads only its own.
- The GitHub App private key is the most sensitive secret: it can mint tokens for every installed target.
  Store it as an Actions secret and never expose it to agents.
- A token that applies labels must be a user or App token, because labels added by the default token do not
  start other workflows. Scope it to issues on the engine repository only.
- Secrets are never written to prompts, comments or artifacts.

## Governance decisions

- Agents recommend; humans decide. A Reviewer verdict is advice, not approval.
- An agent that is blocked says so in its result, and the workflow hands the issue to a human.
- Changing the label allowlist, the write scope or the capability model requires a documented decision
  (see [decisions](decisions/)).

## Provider-specific caveat

Sandboxing differs between providers. Until a runner's capability mapping is verified (see
[runner contract](runner-contract.md#capability-mapping)), that runner must not be enabled for agents that
can edit or run commands.
