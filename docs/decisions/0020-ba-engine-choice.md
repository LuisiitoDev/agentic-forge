# ADR-0020: The engine configuration chooses how the BA runs (Copilot or Claude Code)

**Status:** Accepted for the BA only. Decisions 1 to 3 (the default engine and the ban on any automatic switch) are superseded by [ADR-0021](0021-claude-default-copilot-fallback.md); the rest stands. Built and tested without GitHub; the Claude twin ran once on its own against
the sandbox ([forge-ba-claude](../../.github/workflows/forge-ba-claude.yml)). Not run through the orchestrator.
Complements [ADR-0017](0017-copilot-engine-for-phase-1.md) and keeps [ADR-0002](0002-model-agnostic-runners.md).

> **Amended by [ADR-0024](0024-claude-code-only.md):** Claude Code inside AWF is the only engine; `gh aw`, Copilot and the fallback to it were removed. Read what this record says about them as history.

## Context

- The agent workflows run on Copilot through `gh aw` with the model pinned to `gpt-4.1`. That account answers
  `429 Too Many Requests` once a per-user allowance is spent ([ADR-0017](0017-copilot-engine-for-phase-1.md)).
- `gh aw` v1.2.2 cannot run Claude with the OAuth token of `claude setup-token`: its `claude` engine only wires
  `ANTHROPIC_API_KEY`, and it keeps `CLAUDE_CODE_OAUTH_TOKEN` out of the sandbox, also with `strict: false`.
  Tried: `engine.env`, `engine.auth`, `provider.auth`, a shared engine with `behaviors`.
- `anthropics/claude-code-action` accepts that token (`claude_code_oauth_token`). `forge-ba-claude.yml` runs the BA
  with it. The agent only reads; a separate job posts the result.

## Decision

1. A repository variable of the engine, `FORGE_BA_ENGINE`, chooses the workflow the orchestrator dispatches for the
   BA: `copilot` (default, `forge-ba.lock.yml`) or `claude` (`forge-ba-claude.yml`). It is engine configuration and is
   never read from the target.
2. The choice is made by a person. **A failure or a 429 never switches the engine**: there is no automatic fallback
   ([ADR-0017](0017-copilot-engine-for-phase-1.md)).
3. Fail closed: any other value, or `claude` for an agent without a Claude twin, blocks the request with a comment.
   Nothing is dispatched and nothing falls back to Copilot.
4. The orchestrator waits for the run of the workflow it dispatched, found by that workflow's file and its own
   run-name (`forge-ba-claude OWNER/REPO#N`), so a run of the other BA workflow is never taken as its result
   ([ADR-0018](0018-orchestrator-awaits-agent-runs.md)).
5. Only the BA has a twin. The same variable pattern (`FORGE_<AGENT>_ENGINE`) is the place for others, once they
   have a workflow that keeps the same boundary: the agent has no GitHub write access.

## To verify

- A request run through the orchestrator with `FORGE_BA_ENGINE=claude` (only the workflow was run on its own).
- Whether Anthropic's terms allow using a subscription token in automation.
- That the runtime context, which mentions safe outputs, is understood next to the delivery override of the twin.

## Consequences

- A person can move the BA to Claude Code without touching code, and back, by changing one variable.
- Two BA workflows must be kept in step with `.github/forge/agents/ba.md` and the runtime context.
- The twin does not enforce `max-turns` or the sandbox of `gh aw`; its limits are `--max-turns`, a read-only tool
  list and a read-only token.
