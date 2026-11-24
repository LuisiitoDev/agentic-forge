# ADR-0021: The BA runs on Claude by default, with one retry on Copilot

**Status:** Accepted. Applied to the BA here and to every agent by [ADR-0022](0022-all-agents-on-claude-in-awf.md). Built and tested without GitHub; not run through the orchestrator.
Supersedes the default and the "no automatic fallback" rule of [ADR-0020](0020-ba-engine-choice.md) (decisions 1 to 3).

## Context

[ADR-0020](0020-ba-engine-choice.md) let a person choose how the BA runs and forbade any automatic switch. The
owner now wants Claude Code as the main engine and Copilot as the fallback. Copilot answers `429` once a per-user
allowance is spent ([ADR-0017](0017-copilot-engine-for-phase-1.md)); a Claude run can also fail for its own reasons
(token, quota, outage).

## Decision

1. `FORGE_BA_ENGINE` defaults to `claude` (`forge-ba-claude.yml`). `copilot` (`forge-ba.lock.yml`) is still allowed.
2. `FORGE_BA_FALLBACK` names the engine tried once after a failure: `copilot` (default when the BA runs on Claude),
   `claude`, or `none`. When the BA runs on Copilot there is no fallback unless the variable says so.
3. The orchestrator falls back **only** when the run that just ended did not succeed (`failure`, `timed_out`,
   `cancelled`) **and** left no fresh result comment. Consequences:
   - a result the agent wrote is never second-guessed, whatever its status (`blocked` included);
   - a run that succeeded without a result is a broken agent, not an outage: the request is blocked;
   - a run that never appeared, or that is still going, is not a fallback case.
4. At most one retry per agent in one orchestration, and never to the same engine. The target gets a comment that
   says which engine failed and which one is tried.
5. An invalid value (`FORGE_BA_ENGINE` or `FORGE_BA_FALLBACK`) never changes the engine: an invalid engine blocks the
   request, an invalid fallback means no fallback.
6. A runner still never switches model by itself: the retry is a decision of the orchestrator, visible in the request.

## To verify

- A request through the orchestrator on Claude, and one where Claude fails and Copilot takes over.
- That a Copilot `429` on the fallback is reported as blocked, as any failed run is.
- The cost of the Claude token against the subscription's limits, and whether Anthropic's terms allow it.

## Consequences

- Copilot's allowance is spent only when Claude cannot do the job, which keeps the `gpt-4.1` quota for those cases.
- A failure that is the prompt's fault (it fails on both engines) costs two runs before it blocks.
- Only the BA has a Claude twin; the other agents stay on Copilot.
