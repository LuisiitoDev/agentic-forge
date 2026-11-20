# ADR-0018: The orchestrator awaits the agent runs it starts

**Status:** Accepted. Supersedes the `workflow_run` trigger of [ADR-0009](0009-github-agentic-workflows.md).
Implemented and tested without GitHub; the real-GitHub check is the spike described below.

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
   orchestration and a job timeout. Exceeding one marks the request `blocked` with a comment.
5. **Removed.** The `workflow_run` trigger and the agent branch of the workflow's `resolve` job. The agent branch of
   `orchestrate.sh` stays: the loop uses it, and the tests drive it directly.

## Alternatives considered

- **Dispatch agents with a GitHub App token** instead of the workflow token, so the run is attributed to the App and
  `workflow_run` fires. It is a hypothesis, not checked, and it needs the App that does not exist yet.
- **A final step in each agent workflow that notifies the orchestrator** (`workflow_dispatch` or
  `repository_dispatch` work with the workflow token). Whether `gh aw` allows such a step is not checked, and it
  puts orchestration inside the agent workflows.

## Consequences

- No new credential, no App and no extra event. The routing logic is unchanged.
- One orchestrator job stays open while an agent runs (up to the bounds above) and uses runner time. The job holds
  the per-request concurrency group for that time, so other events for the same request wait; GitHub keeps only
  the newest waiting run, so an older waiting event can be dropped. A re-labeling by a person recovers a request.
- If the orchestrator job is cancelled or times out while an agent runs, the request keeps its last state label until
  a person relabels it. Nothing is lost, but nothing moves by itself. This is the recovery gap
  [ADR-0001](0001-actions-as-engine.md) already accepts.

## To verify

- That the run lookup (workflow file, `event`, `created_at`, `display_title`) finds the run on real GitHub. The
  unit tests use a fake `gh`; the spike `forge-spike-start` now runs the real `dispatch` and wait code against
  `forge-spike`.
- The runner minutes one request uses end to end, and the behaviour of a dropped waiting event.
