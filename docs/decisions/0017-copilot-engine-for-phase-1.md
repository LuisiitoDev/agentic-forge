# ADR-0017: GitHub Copilot is the engine for the first real run

**Status:** Accepted for Phase 1. Refines [ADR-0009](0009-github-agentic-workflows.md), whose "(Claude)" scope it
replaces, and [ADR-0002](0002-model-agnostic-runners.md). Not run against GitHub yet. Refined by
[ADR-0020](0020-rate-limits-and-concurrency.md): rate limited runs are retried, and the model stays pinned.

## Context

ADR-0009 chose the `claude` engine for Phase 1, which needs an `ANTHROPIC_API_KEY`. The owner does not use
Anthropic for this project and has GitHub Copilot. With `gh aw` the engine is fixed when each agent workflow is
compiled, so changing provider means changing the workflow, not the core
([ADR-0002](0002-model-agnostic-runners.md)).

## Decision

1. The five agent workflows use `engine: copilot` with the model pinned to `gpt-4.1`. The default (`auto`) resolved to
   a model this account is not offered (see "Verified on a real run"); the pin is a setting of the account, to be
   revisited when more models are enabled. The Claude choice is not removed from the design: switching back
   is one line per workflow and a recompile.
2. The credential is the secret `COPILOT_GITHUB_TOKEN`. `ANTHROPIC_API_KEY` is no longer needed. Per the `gh aw`
   authentication reference (read on its `main` branch; the compiler used here is v1.0.1), it is a **fine-grained
   personal access token** owned by a user, with the account permission **Copilot Requests: Read**; the user needs an
   active Copilot license; OAuth tokens (`gho_`) are rejected, and GitHub Apps tokens are not supported. The same
   reference offers `permissions: copilot-requests: write` with the Actions token for organizations with Copilot
   billing; it is not used here. All of this stays **To verify** until a real run.
3. Nothing else changes: the agent specs name capabilities, the orchestrator and the intake are provider-neutral
   ([ADR-0003](0003-neutral-agent-spec.md)), and the safe outputs, the allowlists and the approval policy are the same.

## What was checked

`gh aw` v1.0.1 compiles the five workflows with `engine: copilot`, 0 warnings. In the compiled workflows:

- the only provider secret is `COPILOT_GITHUB_TOKEN`;
- the Copilot CLI is started with `--allow-tool` entries for `github`, `safeoutputs`, `shell` and `write`. Claude had
  the equivalent `Bash`, `Edit` and `Write` by default, so the capabilities are not wider;
- the MCP gateway still carries each server's tool list ([ADR-0013](0013-agent-mcp-servers.md)).

## Verified on a real run (2026-11-20, the `forge-spike` workflow)

Run on this repository with a personal fine-grained token, with a temporary workflow (`forge-spike`, since removed).
Evidence: the run logs and the `awf-reflect.json` artifact of runs 44784888572, 44785493344 and 44785499005, and four
more.

- `COPILOT_GITHUB_TOKEN` as described above **works**: the activation job validated it and the Copilot CLI ran
  inference with it.
- The Copilot CLI runs non-interactively inside the `gh aw` sandbox. With `gpt-4.1` the agent step ended with
  exit code 0 and `premiumRequests=0`.
- `auto` resolved to `claude-sonnet-5.5`, and the API answered `400 The requested model is not supported`. The
  account's model catalog (42 entries) lists that model anyway, so **the catalog is not proof that a model can be
  used**.
- Tried explicitly, same prompt: `gpt-4.1` works. `claude-sonnet-5.5`, `claude-haiku-5.5`, `claude-sonnet-5`,
  `gpt-5.3-codex`, `gpt-6-sol` and `gpt-5.6-luna` all fail with the same 400. Why (plan, model policy in the
  account's Copilot settings, or the CLI version) is **not known**.
- `display_title` of a run started by a human `workflow_dispatch` carries the `run-name`
  (`forge-spike OWNER/REPO#1`).
- A `workflow_run` watcher does **not** fire for a run started with the workflow token, so the orchestrator waits for
  its agents instead ([ADR-0018](0018-orchestrator-awaits-agent-runs.md)).
- Side effect: when an agent run fails, `gh aw` opens issues in the repository by itself (`[agentic-workflows] [aw]
  ...`, issues 40 and 41 here). The agent workflows should say what they want.

## To verify (first real run)

- That the token works on a private target repository (the spike ran on this repository only).
- Why six models in the catalog are refused, and which Copilot setting or plan enables them.
- ~~That the `max-turns` values are honoured by the Copilot CLI.~~ They are: the gateway caps model calls (ADR-0009, first real run).
- That the Copilot CLI also receives `--allow-tool` for the whole MCP server (`atlassian`, `azure-devops`) next to
  the single tools. The gateway lists the allowed tools, so the per-tool limit is expected to hold; check it.
- How often Copilot answers `429 Too Many Requests` under normal use, and whether the retries should wait longer
  (seen once after about forty minutes of repeated runs, see ADR-0009).
- Result quality of `gpt-4.1` for BA, Planner, Coder, Infra and Reviewer. Nothing is known yet.

## Consequences

- No Anthropic account is needed to run the pipeline.
- The first run is the first evidence about Copilot as a non-interactive engine. If a step cannot run on it, the
  documented limits say which agent can ([runner contract](../runner-contract.md)).
