# ADR-0020: Rate limited agent runs are retried, and one agent runs per request at a time

**Status:** Accepted for Phase 1. Refines [ADR-0017](0017-copilot-engine-for-phase-1.md) (Copilot stays the engine)
and [ADR-0018](0018-orchestrator-awaits-agent-runs.md) (what the wait does with a failed run). Compiled and tested
with a fake `gh`; **not run against GitHub yet**.

## Context

On the first real runs agent runs failed with `429 Too Many Requests` from Copilot. After about forty minutes of
runs, a Planner run failed three times in a row. The retries `gh aw` makes by itself (5, 10 and 20 to 40 seconds)
were not enough ([ADR-0009](0009-github-agentic-workflows.md)). The orchestrator then marked the request `blocked`,
and a person had to start it again.

Moving to the `claude` engine was considered and dropped. In `gh aw` v1.0.1 it accepts only `ANTHROPIC_API_KEY`
(the compiled workflow validates that secret alone), and the owner has a Claude subscription, not an API key.
Subscription OAuth tokens are not supported (ADR-0009). So Copilot stays the engine, and the pipeline has to live
with any provider's rate limits.

## Decision

1. **The engine and the model stay as ADR-0017 set them:** `engine: copilot`, `model: gpt-4.1`, secret
   `COPILOT_GITHUB_TOKEN`. Neither one can usefully come from a repository variable. Both points were checked against
   `gh aw` v1.0.1:
   - `engine.id` does not accept an expression: `gh aw compile` fails on `id: ${{ vars.FORGE_ENGINE }}`.
   - `engine.model` accepts one, but the compiled workflow then changes in two ways:
     - It drops the model's price from the proxy's AI-credit catalog. Per the `gh aw` schema, an unpriced model is
       refused with HTTP 400 (`unknown_model_ai_credits`) while the credit budget is on, and it is on by default.
     - It adds a fallback to `auto`, which resolved to a model this account refuses (ADR-0017).

     Either could break the one configuration that has worked on a real run, so the model stays a literal.
   Changing the engine or the model is still one edit in the five `forge-*.md` files and a recompile. A test pins
   this: the engine is Copilot, the model is priced, and there is no fallback.
2. **One agent run per request at a time.** Each agent workflow declares
   `concurrency: {group: forge-agent-<target>-<issue>, cancel-in-progress: false, queue: max}`.
   - The five agents share the group.
   - It is not the orchestrator's group (`forge-request-<target>-<issue>`). The orchestrator holds its own group while
     it waits for the agent, so one shared group would deadlock.
   - It replaces the workflow-wide group that `gh aw` generated (`gh-aw-<workflow>`). That group serialised every
     request of one agent, but did not stop two agents of the same request.
   - The orchestrator's job keeps its group and gets `queue: max`, so a burst of human events is queued instead of
     only the newest being kept.
3. **Retry on 429, and only on 429.** When an agent run the orchestrator waits for ends in failure, the orchestrator
   reads the logs of the run's failed steps (`gh run view --log-failed`). If the logs show a provider rate limit, it
   waits and dispatches the same agent again with the same inputs. A rate limit is `429 Too Many Requests`,
   `rate_limit_error` or an HTTP status 429; the pattern is configurable with `FORGE_RATE_LIMIT_PATTERN`. Any other
   failure is not retried and blocks the request, as before.
   - **Backoff:** exponential, with jitter. The wait starts at `FORGE_RETRY_BASE` (default 60 s), doubles with each
     attempt and is capped at 600 s. A random part, up to half of it, is dropped, so requests that were limited
     together do not return together.
   - **Attempt limit:** at most `FORGE_RATE_LIMIT_ATTEMPTS` runs of one stage (default 4: the first run and three
     retries). After that the request is marked `blocked`, with a link to the last run.
   - **Token budget:** no retry is started if it would outlive the orchestrator's token budget (ADR-0018). The request
     is blocked instead.
   - **Spent `max-turns` budget:** when an agent spends its budget, the gateway answers with a 429
     (`Maximum LLM invocations exceeded`). That one is excluded, because waiting does not fix it.
   - **Unreadable log:** a log that cannot be read does not count as a rate limit (fail closed).
   - **Stage limit:** a retry repeats the same stage, so it does not count against `FORGE_MAX_STAGES`.
   - **Auditable:** each retry posts a comment on the request issue (which run, how long the wait, attempt n of m) and
     writes a line in the orchestrator's log.

## Consequences

- No new secret. The agent specs, the orchestrator's routing, the intake, the safe outputs and the approval policy
  do not change. The rate limit patterns are the only provider wording in the orchestrator, and they are
  configurable.
- A rate limited request takes longer instead of stopping: up to about seven minutes of waiting with the defaults.
  The orchestrator's job time limit (75 minutes) and token budget (55 minutes) are unchanged.
- The orchestrator now reads agent run logs with its own token. It has `actions: write`, which includes read.
- A failed run whose logs only mention a 429 could be retried, for example an agent that is implementing rate
  limiting. The attempt limit bounds this, and every retry is visible on the request.
- Retrying does not raise the provider's limit. If Copilot keeps refusing for longer than the backoff covers, the
  request still ends `blocked` and has to be started again later.

## To verify (next real run)

- That the default pattern matches the log line a Copilot 429 leaves in the failed step. ADR-0009 recorded the text
  `429 Too Many Requests`, but not the exact line.
- That a run that failed on a 429 left nothing behind, such as a comment or a push. Safe outputs run in a later job,
  which a failed agent job should not reach. If one did run, the retry could duplicate it (invariant 7).
- That `gh run view --log-failed` works with the workflow token for runs of the same repository.
- That GitHub honours `queue: max` in the orchestrator's job concurrency, as it does in the workflows `gh aw` compiles.
- Whether the defaults (4 attempts, 60 s base) are enough against Copilot's limits, or need to be longer.
