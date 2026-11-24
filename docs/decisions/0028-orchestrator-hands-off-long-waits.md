# ADR-0028: The orchestrator hands off a long wait to a new run

**Status:** Accepted. Amends [ADR-0018](0018-orchestrator-awaits-agent-runs.md) (bounds, limits). Implemented, tested with a
fake `gh`, and checked on GitHub (see "Verified").

## Context

ADR-0018 has one orchestrator job dispatch an agent and wait for it. The target token is a GitHub App installation
token that lives one hour, so the job stopped waiting after 55 minutes (`FORGE_TOKEN_BUDGET`) and marked the request
`blocked`. After approval the job waits for the Coder (up to 60 minutes) and then the Reviewer (up to 45): a slow
Coder alone could exceed the budget, and the request was blocked while the agent was still working.

## Decision

1. **Hand off instead of blocking.** When the token budget runs out while an agent run is still going, the
   orchestrator dispatches `forge-orchestrator.yml` again (`workflow_dispatch`, workflow token) with `target`,
   `issue`, `agent`, `since` (the agent's dispatch time), `stages` and `waited` (seconds already spent on that run),
   and ends. Only if that dispatch fails is the request marked `blocked`, as before.
2. **The new run is a resume** (`FORGE_KIND=resume`). Its `resolve` job validates every input by pattern and checks the
   allowlist before any token is created, like a human event. It creates a fresh target token and continues the same
   wait: same run lookup, same freshness check of the result. It handles no human event, so it cannot start or
   approve anything.
3. **Same engine ref.** The orchestrator dispatches agents and the resume on the ref it runs on (`github.ref_name`):
   the default branch for a human event, the handing-off run's ref for a resume.
4. **Tunable budget.** The repository variable `FORGE_TOKEN_BUDGET` lowers the budget (seconds, capped at 3300), to
   test the hand-off without waiting an hour.
5. **The bounds stay global.** `stages` and `waited` are carried, so `FORGE_MAX_STAGES` and `AWAIT_MAX` bound the whole
   request, not each run.

## Alternatives considered

- **Mint the token inside the script.** Needs the App's private key in the orchestrate step and RS256 signing, which the
  standard library does not have. The key stays where it is: in `create-github-app-token`.
- **Raise the budget.** The token lifetime is fixed by GitHub.

## Consequences

- No new secret or permission: the route job already has `actions: write` to dispatch agents.
- The resume run joins the per-request concurrency group behind the run that handed off. GitHub keeps only the newest
  waiting run of a group, so a human event that arrives in the same seconds can replace the resume, or the other way
  round. The request then keeps its state label until a person relabels it, which is the recovery gap ADR-0018 accepts.
- Anyone with write access to the engine can start a resume by hand. They are already trusted with its secrets, and a
  resume only waits and reads trusted results.

## Verified

On 2026-11-24, with `FORGE_TOKEN_BUDGET=30`, on `forge-sandbox#6` from the PR branch: resume run 45436748841 waited
for BA run 45436733497 and handed off after 73 s (queue time included). Run 45436968232 read the BA result
(`complete`), labeled `planning`, dispatched the Planner (45437180618) and handed off its wait. Runs 45437255540 and
45437436120 continued it, the last with `stages=1` and `waited=77` carried. That Planner failed in delivery for an
unrelated reason (a rerun on an existing plan branch, fixed in the same PR), and the last run marked the request
`blocked` with the reason. Every run used the PR branch as its ref.
