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
- **Hostile target content.** A target's files are untrusted data.

## Controls

| Control | How |
|---|---|
| Untrusted input | Request and PR data are passed as files. The prompt states it is data, never instructions, and tells the agent to report an injected instruction under `Suspicious content` ([ADR-0026](decisions/0026-prompt-injection-defenses.md)) |
| Hidden text | HTML comments and invisible or bidirectional characters are removed from everything a person wrote before the agent reads it |
| Output scan | A credential in an agent's result or in the lines its patch adds blocks the publication. The comment is rebuilt as `blocked` and never shows the value |
| Trojan Source | Invisible or bidirectional characters in added code are rejected like a path outside the allowed ones |
| Posted comments | Images, fetching HTML and `@mentions` are removed or broken before the comment is posted, so the page cannot leak data or notify strangers |
| Risky files | A change to dependencies, builds, CI, hooks or scripts is listed in the comment for the human to read first |
| Tool rules | Every agent denies the web and the easy ways to read the process environment. Only the Reviewer may call `curl` |
| Target allowlist | Intake rejects any `target` not on the engine's allowlist |
| Scoped access | A short-lived token per run, limited to the one target repository and the permissions needed |
| The approved plan is the scope | The Coder may change only the `paths` of the plan a human approved, read from the bot's Planner result; a plan without paths is not approved ([ADR-0030](decisions/0030-the-approved-plan-is-the-scope.md)) |
| Isolation | One job and one checkout per request. No state or credentials shared across requests or targets |
| Read-only agents, safe outputs | The agent job has no write access. Its outputs are checked by a detection step and applied by a separate job ([ADR-0009](decisions/0009-github-agentic-workflows.md)) |
| Fixed destination | `target`, `issue` and the checkout come from the dispatch inputs. The agent cannot choose another repository |
| Protected files | The delivery job never publishes a change to `.github/`, `.git`, `.forge/` (the Coder; the Planner only its plan directory), a symlink or a submodule, whatever the agent was allowed to write ([ADR-0022](decisions/0022-all-agents-on-claude-in-awf.md)) |
| Scope gate | Before review, every changed file must be in the plan directory or the paths of the approved plan. Applied after the push, so it blocks rather than prevents |
| Validation before credentials | The orchestrator validates the event and the allowlist before it creates any token |
| No GitHub write access for agents | The agent never receives a GitHub token. The workflow posts comments, applies labels and pushes. Read-only MCP tools are declared per agent, with an allowlist of tools and engine-held credentials ([ADR-0013](decisions/0013-agent-mcp-servers.md)) |
| Label allowlist | Only listed labels can be applied from a result. `ready-for-dev` is not on the list |
| Write scope | Each agent may commit only its `write_paths`. Everything else in the working tree is discarded |
| Capability restriction | Agents declare capabilities. The workflow maps them to the tools Claude Code may use (`configure_agent.py`) and to a network allowlist |
| Human approval | A human adds `ready-for-dev` and merges. Reviewer findings send work back to the Planner, and the revised plan needs approval again |
| Trusted authors | The orchestrator reads result comments only from the Actions bot |
| Intake is not a boundary | The caller workflow in a target is editable by that target, so the engine re-checks the allowlist, the sender and approval ([ADR-0015](decisions/0015-reusable-workflow-intake.md)). |
| Minimal dispatch | The dispatch carries only identifiers. The engine re-reads the issue with a scoped token and never treats the payload as content |
| Submitter authority | `forge` counts only when added by a human with write access to the target, checked by the engine from the sender, not from the payload's claims |
| Approver authority | `ready-for-dev` needs a human with write access ([ADR-0011](decisions/0011-approval-by-write-access.md)). Ownership is enforced at merge by the target's branch protection, not by the engine |
| No self-approval | Whoever opened the request cannot approve its plan, unless `FORGE_ALLOW_SELF_APPROVAL` is set ([ADR-0012](decisions/0012-approval-policy.md)) |
| Sensitive paths | A plan touching `FORGE_SENSITIVE_PATHS` needs an approver with the admin or maintain role. Checks the plan's paths and the real changed files, by whole segment, case-insensitively. Checked again after the Coder: a changed sensitive file the approved plan did not declare blocks the request. A `paths` list that cannot be read fails closed |
| Loop prevention | Events caused by the App's own bot are ignored, so an agent can never start or approve a stage by labeling |
| Idempotent stages | A repeated event (a re-run, a label added again) is safe: each stage checks existing labels and results first |
| Bounded runs | `max_turns`, `timeout_minutes` and one orchestration per issue |
| Output hygiene | The run script never writes the token, a host name or an address to the job log; the firewall summary is counts only |

## Secrets

- The Claude token is an Actions secret. It reaches the agent's container through a private file that is deleted afterwards.
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

## The agent can read its own token

The OAuth token is visible to the agent process: AWF limits where it can be sent (only the Anthropic API is reachable
from the agent's commands, checked with a probe), not who can read it. The sandbox of `gh aw` would keep it out of
reach, but it cannot use a subscription token ([ADR-0022](decisions/0022-all-agents-on-claude-in-awf.md)).
