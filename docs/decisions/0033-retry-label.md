# ADR-0033: `retry` continues a blocked request from the stage that stopped

**Status:** Accepted. Amends the recovery of [ADR-0018](0018-orchestrator-awaits-agent-runs.md) and the label set of
[ADR-0029](0029-labels-are-the-only-trigger.md). Implemented and tested with a fake `gh`; not run against GitHub.

## Context

When a run failed, the request was `blocked` and the only way out was to remove and add `forge`, which starts the
request again from the BA. A failure in the Coder or the Reviewer, after a person had approved a plan, cost the whole
analysis and plan again and a second approval.

## Decision

1. **A new human label, `retry`**, on a request that is `blocked`. It is a signal like `clarified`: the orchestrator
   removes it at once. On a request that is not blocked it only does that.
2. **The stage to run is decided only from the trusted results** (comments by the engine's bot), never from the text of
   the request: no result runs the BA; the newest result names what finished, so the next stage runs (BA complete, the
   Planner; plan ready, the Coder; Coder done, the scope gate and the Reviewer; changes requested, the Planner); a
   `blocked` result runs that agent again; a pass or open questions only set their label.
3. **Approval is never skipped.** The Coder runs again only while `ready-for-dev` is still on the issue, which every
   new plan clears. Otherwise the request goes back to `spec-review`.
4. Like any label, `retry` needs a human with write access, confirmed on the issue's events (ADR-0031), and counts only
   on an issue that has `forge`. The `blocked` comment says how to use it.
5. **Nothing retries by itself.** A person reads the reason, fixes the cause and adds the label (ADR-0024 stands).

## Consequences

- A transient failure costs the stage that failed, not the request.
- `forge` removed and added again still starts over.
- A target pinned to an older intake cannot send `retry`: its intake ignores the label until the pin moves.
