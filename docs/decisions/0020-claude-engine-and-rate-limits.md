# ADR-0020: Claude is the engine, and rate limited agent runs are retried

**Status:** Accepted for Phase 1. Supersedes in part [ADR-0017](0017-copilot-engine-for-phase-1.md) (the engine,
the model and the credential; its findings stay valid as a record). Refines [ADR-0018](0018-orchestrator-awaits-agent-runs.md)
(what the wait does with a failed run). Compiled and tested with a fake `gh`; **not run against GitHub yet**.

## Context

ADR-0017 made GitHub Copilot the engine for the first real run, with the model pinned to `gpt-4.1`. On the first
real runs agent runs failed with `429 Too Many Requests` from Copilot: after about forty minutes of
runs a Planner run failed three times in a row, and the retries `gh aw` makes by itself (5, 10 and 20 to 40 seconds)
were not enough ([ADR-0009](0009-github-agentic-workflows.md)). The orchestrator then marked the request `blocked`
and a person had to start it again. Only one model was usable on that Copilot account.

Two things needed to change: the provider, and what the pipeline does when any provider says "too many requests".
The provider-neutral design ([ADR-0002](0002-model-agnostic-runners.md), [ADR-0003](0003-neutral-agent-spec.md))
must survive both.

## Decision

1. **Engine.** The five agent workflows use `engine: claude`. The credential is the repository secret
   `ANTHROPIC_API_KEY` (an Anthropic API key; subscription OAuth tokens are not supported, ADR-0009).
   `COPILOT_GITHUB_TOKEN` is no longer required.
2. **Model is a setting, the engine is not.** Checked against `gh aw` v1.0.1:
   - `engine.model` accepts an expression. Every agent workflow uses
     `${{ vars.FORGE_MODEL || 'claude-sonnet-5-5' }}`, so the repository variable `FORGE_MODEL` switches the model of
     all five agents without a recompile, and the default is Claude Sonnet 5.5.
   - `engine.id` does **not** accept an expression (`gh aw compile` fails on `id: ${{ vars.FORGE_ENGINE }}`). The
     engine is fixed when the workflows are compiled, so there is no `FORGE_ENGINE` variable. Changing the engine
     stays what ADR-0017 described: edit `engine.id` in the five `forge-*.md` files, set that engine's secret,
     recompile and commit. A `FORGE_MODEL` value must be a model of the compiled engine.
3. **One agent run per request at a time.** Each agent workflow declares
   `concurrency: {group: forge-agent-<target>-<issue>, cancel-in-progress: false, queue: max}`. The group is shared by
   the five agents and is not the orchestrator's (`forge-request-<target>-<issue>`): the orchestrator holds its group
   while it waits for the agent, so sharing one group would deadlock. This replaces the workflow-wide group `gh aw`
   generated (`gh-aw-<workflow>`), which serialised every request of one agent but did not stop two agents of the same
   request. The orchestrator's job keeps its group and gets `queue: max`, so a burst of human events is queued instead
   of keeping only the newest.
4. **Retry on 429, and only on 429.** When an agent run the orchestrator waits for ends in failure, the orchestrator
   reads the logs of the run's failed steps (`gh run view --log-failed`). If they show a provider rate limit
   (`429 Too Many Requests`, `rate_limit_error`, an HTTP status 429), it waits and dispatches the same agent again
   with the same inputs. Anything else is not retried and blocks the request as before.
   - Exponential backoff with jitter: the wait doubles per attempt from `FORGE_RETRY_BASE` (default 60 s) up to a cap
     (600 s), and a random part of up to half of it is dropped, so requests limited together do not return together.
   - At most `FORGE_RATE_LIMIT_ATTEMPTS` runs of one stage (default 4: the first and three retries). Then the request
     is marked `blocked` with a link to the last run.
   - No retry is started that would outlive the orchestrator's token budget (ADR-0018); the request is blocked instead.
   - The 429 the gateway answers when an agent spends its `max-turns` budget (`Maximum LLM invocations exceeded`) is
     excluded: waiting does not fix it.
   - A log that cannot be read is not a rate limit (fail closed).
   - A retry is the same stage, so it does not count against `FORGE_MAX_STAGES`.
   - **Auditable:** each retry is a comment on the request issue (which run, how long the wait, attempt n of m) and a
     line in the orchestrator's log.

## Consequences

- An Anthropic account and API key are needed. The command to store the key is
  `gh secret set ANTHROPIC_API_KEY --repo LuisiitoDev/agentic-forge` (it prompts for the value).
- The agent specs, the orchestrator's routing, the intake, the safe outputs and the approval policy do not change.
  Provider vocabulary stays in the five workflow front matters and in the rate limit patterns of the orchestrator,
  which are configurable (`FORGE_RATE_LIMIT_PATTERN`, `FORGE_RATE_LIMIT_EXCLUDE`) and cover more than one provider's
  wording.
- Capabilities are not wider. With Copilot, the BA was started with every tool allowed and the others with `github`,
  `safeoutputs`, `shell` and `write`. With Claude, `gh aw` passes an explicit `--allowed-tools` list (`Bash`, `Edit`,
  `Write`, read tools, the GitHub tools and the safe outputs); the MCP gateway still enforces each server's tool list
  ([ADR-0013](0013-agent-mcp-servers.md)), and no agent holds a GitHub write token.
- Adding the new secret tripped the "safe update mode" of `gh aw compile`; the change was compiled with `--approve`
  after review. The compiled workflows still read `COPILOT_GITHUB_TOKEN` in a generic `gh aw` step that checks for
  OAuth tokens; it is optional and may stay unset.
- A request that is rate limited takes longer (up to about seven minutes of waiting with the defaults) instead of
  stopping. The orchestrator's job time limit (75 minutes) and token budget (55 minutes) are unchanged.
- The orchestrator now reads agent run logs with its own token (`actions: write`, which includes read).
- A failed run whose logs merely mention a 429 (for example an agent implementing rate limiting) could be retried.
  The attempt limit bounds that, and every retry is visible on the request.

## To verify (first run with Claude)

- That `ANTHROPIC_API_KEY` works in the `gh aw` sandbox and `claude-sonnet-5-5` is accepted as `ANTHROPIC_MODEL`.
- The exact text Claude Code and the `gh aw` firewall write to the job log on a provider 429, and that the default
  pattern matches it. Until then the pattern is an educated guess: adjust `FORGE_RATE_LIMIT_PATTERN` if it misses.
- That a run that failed on a 429 left nothing behind (no comment, no push): safe outputs run in a later job that a
  failed agent job should not reach. If one did, the retry could duplicate it (invariant 7).
- That `gh run view --log-failed` works with the workflow token for runs of the same repository.
- That GitHub honours `queue: max` in the orchestrator's job concurrency as it does in the `gh aw` compiled workflows.
- That `max-turns` is enforced the same way for the Claude engine (the `Maximum LLM invocations exceeded` 429).
- How often Anthropic rate limits this workload, and whether the defaults need changing.
