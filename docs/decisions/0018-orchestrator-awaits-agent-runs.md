# ADR-0018: The orchestrator awaits the agent runs it starts

**Status:** Accepted. Supersedes the `workflow_run` trigger of [ADR-0009](0009-github-agentic-workflows.md).
Implemented, tested with a fake `gh`, and its run lookup and wait checked on real GitHub (see "Verified").

> **Amended by [ADR-0028](0028-orchestrator-hands-off-long-waits.md):** when the token budget runs out, the wait is
> handed off to a new orchestrator run with a fresh token instead of blocking the request. Read the 55-minute limit
> below as history.

## Context

ADR-0009 had the orchestrator start an agent workflow with `workflow_dispatch` and learn that it finished from a
`workflow_run` event. Its "Not yet verified" list named that as the first risk. The spike on this repository
(`forge-spike`, `forge-spike-start`, `forge-spike-watch`, 2026-11-20) measured it:

| `forge-spike` run | Started by | `workflow_run` watcher fired |
| --- | --- | --- |
| 44788514225 | a person, through the API | yes, 4 seconds after the run ended |
| 44788537827 | the workflow token (`github-actions[bot]`), like the orchestrator | **no**, still nothing 2 minutes after it ended |

Both runs ended `failure` for the same reason, so the conclusion is not the cause. The dispatch itself worked: the
run was created. Only the completion event was not delivered. This is consistent with GitHub's rule that events
caused by `GITHUB_TOKEN` do not start workflows. The pipeline could not advance past the first agent as designed.

## Decision

1. **The orchestrator waits for the agent it dispatched, in the same job.** After `dispatch`, it finds that run,
   polls it until it completes, reads the result exactly as before (`handle_agent`), and continues with the next
   stage. It repeats until no agent is pending.
2. **How the run is found.** By workflow file, `event=workflow_dispatch`, creation time after the dispatch and a
   `display_title` equal to the `run-name` of the agent workflows (`forge-<agent> OWNER/REPO#NUMBER`). The title is
   built from validated values only (target, issue number and a fixed agent name).
3. **Human gates end the chain.** `spec-review`, `needs-clarification`, `needs-review` and `blocked` set a state and
   dispatch nothing, so the job ends and a person acts, as today.
4. **Bounds.** A wait for the run to appear, a maximum wait for each agent run, a maximum number of stages per
   orchestration and a token budget. Exceeding one marks the request `blocked` with a comment. The token budget
   exists because the target token is a GitHub App installation token that expires after one hour: the
   orchestrator stops waiting after 55 minutes (`FORGE_TOKEN_BUDGET`), while the token still works and can say so.
   The job's own timeout (75 minutes) is only a backstop.
5. **Removed.** The `workflow_run` trigger and the agent branch of the workflow's `resolve` job. The agent branch of
   `orchestrate.py` stays: the loop uses it, and the tests drive it directly.

## Alternatives considered

- **Dispatch agents with a GitHub App token** instead of the workflow token, so the run is attributed to the App and
  `workflow_run` fires. It is a hypothesis, not checked, and it needs the App that does not exist yet.
- **A final step in each agent workflow that notifies the orchestrator** (`workflow_dispatch` or
  `repository_dispatch` work with the workflow token). Whether `gh aw` allows such a step is not checked, and it
  puts orchestration inside the agent workflows.

## Consequences

- No new credential, no App and no extra event. The routing logic is unchanged.
- One orchestrator job stays open while an agent runs (up to 55 minutes in all) and uses runner time. The job holds
  the per-request concurrency group for that time, so other events for the same request wait; GitHub keeps only
  the newest waiting run, so an older waiting event can be dropped. A re-labeling by a person recovers a request.
- If the orchestrator job is cancelled or times out while an agent runs, the request keeps its last state label until
  a person relabels it. Nothing is lost, but nothing moves by itself. This is the recovery gap
  [ADR-0001](0001-actions-as-engine.md) already accepts.

## Verified

On 2026-11-20, run 44792731025 of a temporary workflow ran the real `dispatch` and `await_run` from
`orchestrate.py` with the workflow token against a test agent workflow. It dispatched the agent, found the run
(44792760246) by its title about 15 seconds later, waited 2 minutes 25 seconds, and returned code 0 with the right
conclusion (`failure`, expected there) and `run_started_at`. The temporary workflows were then removed.

## Limits and To verify

- **One orchestration lasts about 55 minutes at most.** A chain that needs longer (for example a long BA, planner
  and coder in a row) is marked `blocked` while its agent keeps running, and a person starts the request again. The
  fix is to mint the token again between stages (a fresh token per stage, or a token refresh in the script), which
  needs the GitHub App and is not done. It is the first thing to revisit if requests hit the limit.
- The runner minutes one request uses end to end.
- What happens to an event that waits behind a long orchestration (GitHub keeps only the newest waiting run of a
  concurrency group).
- The lookup finds the run by title, so two runs of the same agent for the same request at once would be
  ambiguous. The per-request concurrency group prevents it; it is not otherwise enforced.
