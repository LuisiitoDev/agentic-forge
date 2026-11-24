# ADR-0022: Every agent runs on Claude Code inside AWF, with the subscription token

**Status:** Accepted. Built and tested without GitHub. Only the BA ran, on its own, with this design; the Planner,
Coder, Infra and Reviewer have not run. Extends [ADR-0021](0021-claude-default-copilot-fallback.md) from the BA to
all five agents and replaces the `claude-code-action` workflow of the BA.

> **Amended by [ADR-0023](0023-no-infra-agent.md):** the Infra agent no longer exists. Read what this record
> says about it as history.

## Context

- The owner wants every agent on Claude Code with the OAuth token of `claude setup-token`, and Copilot only as the
  fallback of [ADR-0021](0021-claude-default-copilot-fallback.md).
- `gh aw` v1.2.2 cannot do it: its docs say subscription OAuth tokens are unsupported, and `engine.env` secrets are
  kept out of the agent container or dropped. Tried and recorded: `engine.env`, `engine.auth`, `provider.auth`,
  engines imported or defined inline with `behaviors`, with and without `strict` and `sandbox.agent`.
- AWF (`github/gh-aw-firewall`, by GitHub Next, the same binary `gh aw` runs) works on its own: a domain allowlist
  for the agent's network. A BA run inside it, with the token, authenticated and finished; a request to
  `example.com` was blocked (run 45303173997).

## Decision

1. Each agent has a workflow `forge-<agent>-claude.yml` with the same inputs as `forge-<agent>`. They call one
   reusable workflow, `forge-agent-claude.yml`, with two jobs:
   - **agent**: reads the request into `request/` files (no GitHub tool for the agent), runs Claude Code inside AWF
     through the composite action `claude-awf`, and turns what changed in `target/` into a patch. No write token.
   - **deliver**: the only job that writes. It validates the result and the changes, publishes them, posts the result.
2. **The token is visible to the agent process, the network is not open.** The token reaches the container through a
   private env file, never a command line, and AWF lets only the listed domains through. This is weaker than `gh aw`,
   where the agent never sees a credential; it is the price of using a subscription token.
3. **What an agent may change** is checked by `validate_changes.py` before anything is published: never `.github/`,
   `.git`, `.forge/config.yml`, a symlink or a submodule, a path with `..`; the Planner only
   `.forge/requests/<issue>/`; the Coder and Infra only the `paths.app` and `paths.infra` of the target's
   `.forge/config.yml` on its default branch (empty means read-only); the BA and the Reviewer nothing. The
   orchestrator's scope gate still runs before the review.
4. **The patch is computed without trusting `target/.git`**: a pristine copy is taken before the run and compared
   with the final tree outside any repository, so a planted git config or hook never runs.
5. Publishing: the Planner opens or updates the draft plan PR (`[forge] ` title, `Closes #<issue>`, idempotent on a
   rerun); the others push to the plan branch only if the PR is open, from this repository, with a `[forge] ` title
   and the head the orchestrator named. A `blocked` result publishes no code. Rejected changes become a `blocked`
   result that says why.
6. The orchestrator runs every agent on Claude by default (`FORGE_<AGENT>_ENGINE`) and falls back once to Copilot
   under the rules of [ADR-0021](0021-claude-default-copilot-fallback.md) (`FORGE_<AGENT>_FALLBACK`).
7. Domains: all agents `api.anthropic.com`; the Coder and the Reviewer also the npm, NuGet, .NET and Python
   registries; the Reviewer also `api.github.com` and `github.com`, with a read-only App token as `GH_TOKEN`.

## To verify

- A Planner, Coder, Infra and Reviewer run end to end: none has run.
- That the workspace is writable from inside AWF (the BA only read), and that the domain lists are enough to
  build and test the target.
- That `gh` or `curl` inside AWF can reach the Reviewer's APIs with the token, and that the App has the actions,
  security-events and vulnerability-alerts permissions the Reviewer's token asks for.
- The `AWF_ONE_SHOT_TOKENS` protection for the OAuth token (not tried).
- `max-turns`, timeouts and the number of Claude turns against the subscription's limits; Anthropic's terms.

## Consequences

- No agent depends on a Copilot allowance unless Claude failed without a result.
- Two sets of workflows to keep in step with the agent specs; the Claude ones have no safe-outputs checks of
  `gh aw`, only the ones above.
- AWF is an exploration of GitHub Next: its version is pinned to v0.29.11 by SHA.
