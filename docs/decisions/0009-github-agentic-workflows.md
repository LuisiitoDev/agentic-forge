# ADR-0009: Agents run as GitHub Agentic Workflows (`gh aw`)

**Status:** Accepted for Phase 1. The engine was Claude; [ADR-0017](0017-copilot-engine-for-phase-1.md) changes it to Copilot. The `workflow_run` trigger described below does not work with the workflow token and is replaced by [ADR-0018](0018-orchestrator-awaits-agent-runs.md). Not yet run end to end against GitHub.

> **Amended by [ADR-0024](0024-claude-code-only.md):** Claude Code inside AWF is the only engine; `gh aw`, Copilot and the fallback to it were removed. Read what this record says about them as history.

> **Amended by [ADR-0023](0023-no-infra-agent.md):** the Infra agent no longer exists. Read what this record
> says about it as history.

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
   `orchestrate.py`. Routing must be deterministic, testable and free of a model.
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

## First real run (2026-11-23)

Found by running the BA on a target repository:

- **The App token defaults to the engine repository.** With `github-app` and no `repositories`, `gh aw` mints the token
  for the workflow's own repository, so the checkout of the target returned 404. Every `github-app` block of the agent
  workflows now has `repositories: ["${{ inputs.target_repo_name }}"]`, a new input (the name without the owner) that
  the orchestrator passes on every dispatch. `repositories: ["*"]` would have worked but gives each run a token for every
  repository the App is installed on, which breaks the one-target scope.
- **`gh aw` opens its own issue when an agent fails** and, to do it, asks the App for `checks: read`, a permission the
  App did not have. The orchestrator already says "blocked" on the request issue, so the agent workflows set
  `report-failure-as-issue: false`. The App does not need `checks`.
- A declared MCP server that cannot connect stops the agent ([ADR-0013](0013-agent-mcp-servers.md)).
- **The GitHub tools drop the engine bot's own comments.** The engine repository is public, so `gh aw` sets
  `min-integrity: approved` on the GitHub tools by default, and a comment authored by an App bot is below `approved`.
  The Planner could not have read the BA's result. The agent workflows set `min-integrity: approved` and
  `trusted-users: ["${{ vars.FORGE_BOT_LOGIN }}"]` (the same variable the orchestrator trusts). Raising it to `none`
  would also let any commenter on a public target steer the agents. Whether the forced `repos: public` scope the same
  guard reports for a public engine repository blocks reads of a private target is **To verify**: the issue body of the
  private sandbox was readable.
- **`gh aw` asks the App for `administration: read`** whenever a workflow pushes to a pull request branch (Planner,
  Coder, Infra), to check branch protection before the push, and the safe-outputs job fails to start if the App does not
  have it. Those workflows set `check-branch-protection: false`, which removes the request: GitHub still enforces
  protection when it receives the push, and the App keeps no access to repository settings. A test forbids the permission.
- **Copilot rate limits are a real constraint.** After about forty minutes of runs on one account (four BA and two
  Planner runs), a Planner run failed with `429 Too Many Requests` three times in a row; `gh aw` retries with 5, 10 and
  20 to 40 second waits, which was not enough. Re-running the stage later works. The orchestrator marks the request
  `blocked` and a person re-labels it. Whether a larger plan or a different model avoids this is not known.
- **The call budget is enforced.** `max-turns` reached the Copilot gateway as a cap on model calls (`429 Maximum LLM
  invocations exceeded (20 / 20)`), so it is no longer "To verify" that the Copilot engine honours it. With `gpt-4.1`
  the BA once spent the whole budget repeating one search eleven times, and finished within it on another run. The
  shared runtime context now tells every agent not to repeat a call with the same arguments.
- **`gh aw` strips HTML comments from the body an agent publishes.** The orchestrator recognised a result by a
  first line `<!-- <agent>:result -->`, which never survived: the BA's comment arrived as `## BA result: complete`.
  A result is now recognised by its author (the engine's bot, as before) and its first line `## <Agent> result:`
  (`BA`, `Planner`, `Coder`, `Infra`, `Reviewer`). The specs, the prompts and the tests use that format. The
  orchestrator's own comments are written by the orchestrator, not by an agent, and keep their HTML marker.

## Not yet verified

- ~~That `workflow_run` fires for runs started by a `workflow_dispatch` made with the workflow token, and that
  `display_title` carries the `run-name` the orchestrator parses.~~ Checked on 2026-11-20: `display_title` carries the
  `run-name`; `workflow_run` does **not** fire for a run started with the workflow token ([ADR-0018](0018-orchestrator-awaits-agent-runs.md)).
- The App permissions the orchestrator needs for the collaborator-permission lookup.
- That `runtime-import` strips or tolerates the specs' frontmatter.
- The whole chain, with a real App, intake workflow, secrets and target.
