# ADR-0024: Claude Code is the only engine; `gh aw` and the Copilot fallback are removed

**Status:** Accepted. Supersedes [ADR-0021](0021-claude-default-copilot-fallback.md) and the engine choice of
[ADR-0020](0020-ba-engine-choice.md); amends [ADR-0009](0009-github-agentic-workflows.md) (agents as `gh aw` workflows),
[ADR-0017](0017-copilot-engine-for-phase-1.md) (Copilot as the first engine) and
[ADR-0022](0022-all-agents-on-claude-in-awf.md) (its fallback to Copilot).

## Context

Every agent already ran on Claude Code inside AWF ([ADR-0022](0022-all-agents-on-claude-in-awf.md)): the whole
chain ran on GitHub with it. The `gh aw` workflows (`forge-<agent>.md` and their compiled `.lock.yml`, 628 KB, almost
nine tenths of the repository) were kept only as the one-time fallback when a Claude run failed
([ADR-0021](0021-claude-default-copilot-fallback.md)). Keeping them cost:

- four generated lock files to recompile and review with every change, `.github/aw/actions-lock.json` and a
  `.gitattributes` for them, and a second engine to keep in step with the agent specs and the Claude scripts;
- about a dozen lines of engine and fallback logic in the orchestrator, the variables `FORGE_<AGENT>_ENGINE` and
  `FORGE_<AGENT>_FALLBACK`, and a second token (`COPILOT_GITHUB_TOKEN`) with its own preflight row;
- a path that had never run on GitHub and depends on an account that answers `429` after a few runs
  ([ADR-0017](0017-copilot-engine-for-phase-1.md)).

## Decision

1. **Claude Code inside AWF is the only engine.** The orchestrator dispatches `forge-<agent>-claude.yml` and nothing else.
2. **The `gh aw` layer is removed**: the four `forge-<agent>.md` workflows and their locks, `.github/aw/actions-lock.json`
   and `.gitattributes`.
3. **There is no fallback.** A run that does not succeed ends the stage: the request is `blocked` with the reason and a
   person re-runs it. Nothing is retried on another engine.
4. **The settings that chose an engine are gone**: `FORGE_<AGENT>_ENGINE`, `FORGE_<AGENT>_FALLBACK` and
   `COPILOT_GITHUB_TOKEN`. The preflight checks `CLAUDE_CODE_OAUTH_TOKEN` and the four `forge-<agent>-claude` workflows.
5. The agent specs stay provider-neutral ([ADR-0003](0003-neutral-agent-spec.md)): the prompts do not name Claude, only
   `.github/forge/claude/` and the `claude-awf` action do.

## Consequences

- One engine, one set of workflows, one token. The repository drops from about 92 to about 80 files, and the weight of
  the lock files disappears.
- If Claude is down, rate-limited or the token is revoked, the pipeline stops until it is back. There is no
  second engine to carry on.
- Going back to Copilot or adding another engine is a new decision. The history of `main` keeps the removed files.
- The workflows keep the suffix `-claude` (`forge-<agent>-claude.yml`) to avoid renaming runs, titles and the references
  to them; it is a name, not a choice.
