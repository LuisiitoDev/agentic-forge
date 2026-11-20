# ADR-0017: GitHub Copilot is the engine for the first real run

**Status:** Accepted for Phase 1. Refines [ADR-0009](0009-github-agentic-workflows.md), whose "(Claude)" scope it
replaces, and [ADR-0002](0002-model-agnostic-runners.md). Not run against GitHub yet.

## Context

ADR-0009 chose the `claude` engine for Phase 1, which needs an `ANTHROPIC_API_KEY`. The owner does not use
Anthropic for this project and has GitHub Copilot. With `gh aw` the engine is fixed when each agent workflow is
compiled, so changing provider means changing the workflow, not the core
([ADR-0002](0002-model-agnostic-runners.md)).

## Decision

1. The five agent workflows use `engine: copilot`. The Claude choice is not removed from the design: switching back
   is one line per workflow and a recompile.
2. The credential is the secret `COPILOT_GITHUB_TOKEN`. `ANTHROPIC_API_KEY` is no longer needed. What kind of token
   it must be and which Copilot plan it needs are **To verify** against `gh aw`'s engine reference; `gh aw` validates
   the secret at the start of each run.
3. Nothing else changes: the agent specs name capabilities, the orchestrator and the intake are provider-neutral
   ([ADR-0003](0003-neutral-agent-spec.md)), and the safe outputs, the allowlists and the approval policy are the same.

## What was checked

`gh aw` v1.0.1 compiles the five workflows with `engine: copilot`, 0 warnings. In the compiled workflows:

- the only provider secret is `COPILOT_GITHUB_TOKEN`;
- the Copilot CLI is started with `--allow-tool` entries for `github`, `safeoutputs`, `shell` and `write`. Claude had
  the equivalent `Bash`, `Edit` and `Write` by default, so the capabilities are not wider;
- the MCP gateway still carries each server's tool list ([ADR-0013](0013-agent-mcp-servers.md)).

## To verify (first real run)

- The type of token and Copilot plan `COPILOT_GITHUB_TOKEN` needs, and whether it works from a workflow on a private
  repository.
- That the `max-turns` values in the workflows are honoured by the Copilot CLI. They compile, and the value reaches
  `GH_AW_MAX_TURNS`, but whether the engine enforces them is not checked.
- That the Copilot CLI also receives `--allow-tool` for the whole MCP server (`atlassian`, `azure-devops`) next to
  the single tools. The gateway lists the allowed tools, so the per-tool limit is expected to hold; check it.
- Result quality of the Copilot model for BA, Planner, Coder, Infra and Reviewer. Nothing is known yet.
- Which model Copilot uses and whether it can be chosen.

## Consequences

- No Anthropic account is needed to run the pipeline.
- The first run is the first evidence about Copilot as a non-interactive engine. If a step cannot run on it, the
  documented limits say which agent can ([runner contract](../runner-contract.md)).
