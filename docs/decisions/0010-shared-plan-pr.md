# ADR-0010: One shared plan PR is the working surface

**Status:** Accepted

## Context

The code already lived in a pull request, but every agent's result was a comment on the request issue, so the
conversation and the work were in different places. A reviewer of the PR could not see what the agents had said.

## Decision

- The **issue** is the request: its text, its state labels and the human approval label (`ready-for-dev`).
- The **plan pull request** is the working surface. The Planner opens it as a draft. The Coder, Infra and
  Reviewer commit to it and post their results on it, and so do replans.
- **BA is the exception.** It runs before the pull request exists, so it posts on the issue. The first plan
  result is also on the issue, because the Planner opens the PR in the same run.
- There is **one shared PR**, not one per agent. Agents run in sequence on the same branch.
- The orchestrator tells each agent where to post with an explicit `thread` input, and reads results from both
  places, trusting only comments authored by the engine's bot.

## Alternatives considered

- **One PR per agent, stacked.** Would allow parallel Coder and Infra and a per-agent scope check, but means
  several PRs to merge in order. Not chosen for now.
- **Open the PR at the start.** Lets BA post on it too, but the PR would exist before there are approved
  requirements. Not chosen.

## Consequences

- Anyone reviewing the PR sees the plan, the implementation notes and the Reviewer's verdict in one place.
- Results are split across two locations, so every reader (the orchestrator and the agents) must read both.
- The scope check stays per PR, not per agent, because Coder and Infra share the branch
  ([README](../../README.md#workflows)).
- Approval is still given on the issue. Approving on the PR itself would need the relay to forward pull request
  events, and is left for later.
