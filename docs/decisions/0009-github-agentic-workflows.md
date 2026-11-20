# ADR-0009: Agents run as GitHub Agentic Workflows (`gh aw`)

**Status:** Accepted for Phase 1. The engine was Claude; [ADR-0017](0017-copilot-engine-for-phase-1.md) changes it to Copilot. The `workflow_run` trigger described below does not work with the workflow token and is replaced by [ADR-0018](0018-orchestrator-awaits-agent-runs.md). Not yet run end to end against GitHub.

## Context

[ADR-0001](0001-actions-as-engine.md) put the engine on GitHub Actions and [ADR-0002](0002-model-agnostic-runners.md)
described a hand-built "runner" step per provider. `gh aw` (GitHub Agentic Workflows) is a CLI that compiles
Markdown workflows into hardened Actions workflows, with several engines built in (`claude`, `codex`, `copilot`,
`gemini`, ...). It already enforces most of what we wanted to build by hand.

## Decision

1. **Each agent is a `gh aw` workflow**: `.github/workflows/forge-<agent>.md`, compiled to `.lock.yml`.
2. **The prompt stays the neutral spec**, imported at run time from `.github/forge/agents/<agent>.md`, plus a
   shared runtime context.
3. **The orchestrator is plain Actions code**, not an agentic workflow: `forge-orchestrator.yml` and
   `orchestrate.sh`. Routing must be deterministic, testable and free of a model.
4. **Agents never name labels.** The orchestrator maps an agent's `status` to labels and to the next stage.
5. **Coder and Infra run one after the other**, not in parallel.
6. **A scope gate** in the orchestrator checks the changed files before review.

## What was verified (by compiling with `gh aw` v1.0.1)

| Finding | Consequence |
|---|---|
| The agent job is read-only; writes go through `safe-outputs`, checked by a detection step, applied by a separate job | Matches "agents have no GitHub access" without custom code |
| `target-repo`, the issue `target` and the checkout `repository` accept `${{ inputs.* }}`, and the compiled workflow reads them from the dispatch inputs | The repository and issue are fixed by the orchestrator, not chosen by the agent. Do not use `target-repo: "*"`, which would let the agent choose |
| A `github-app` can be set on `checkout`, `tools.github` and `safe-outputs`; the App owner is derived from the target repository | A token scoped to the one target repository |
| **`engine` is fixed at compile time.** An expression such as `${{ vars.X }}` is rejected | Choosing a provider per request means a workflow per engine, and the orchestrator dispatches the right one. Today only Claude exists |
| `imports` and `runtime-import` only accept files inside `.github/` | Specs moved from `agents/` to `.github/forge/agents/` |
| `create-pull-request` and `push-to-pull-request-branch` protect a set of files by default (`CODEOWNERS`, `README.md`, `AGENTS.md`, lockfiles, manifests) | The agents cannot edit those without a review request. Plans or code that need them will not go through silently |
| `claude` authenticates with `ANTHROPIC_API_KEY`. Subscription OAuth tokens are unsupported | The previous pipeline's OAuth token does not carry over |
| Two agents pushing to the same PR branch race, and `gh aw` then opens a fallback PR | Coder and Infra are sequential |

Static `allowed-files` is available for `create-pull-request`, so the Planner is limited to
`.forge/requests/**`. It cannot be set per target at run time, which is why Coder and Infra are checked after the
fact by the scope gate.

## Consequences

- Adding Codex or Copilot means new workflows with another `engine:` and a way for the orchestrator to pick
  them (for example `forge:codex`). Their capability and permission mapping is still **to verify**.
- The specs' frontmatter (`capabilities`, `limits`, `write_paths`) is documentation for the workflow author. It
  is enforced by the workflow's tools, `max-turns`, `timeout-minutes` and safe outputs, not read by `gh aw`.
- The scope gate runs after the agent has pushed, so out-of-scope changes reach the plan branch (never `main`)
  and are blocked before review and merge, rather than prevented.
- The compiled `.lock.yml` files are generated and large. They are committed and must be regenerated whenever a
  `.md` source changes.

## Not yet verified

- ~~That `workflow_run` fires for runs started by a `workflow_dispatch` made with the workflow token, and that
  `display_title` carries the `run-name` the orchestrator parses.~~ Checked on 2026-11-20: `display_title` carries the
  `run-name`; `workflow_run` does **not** fire for a run started with the workflow token ([ADR-0018](0018-orchestrator-awaits-agent-runs.md)).
- The App permissions the orchestrator needs for the collaborator-permission lookup.
- That `runtime-import` strips or tolerates the specs' frontmatter.
- The whole chain, with a real App, intake workflow, secrets and target.
