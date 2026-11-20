# Request contract

A **request** is the unit of work the engine accepts: "do this in that repository". A request is an **issue in
the target repository** that a human has labeled `forge`. There is no CLI and nothing to install in the target.

## Creating a request

1. Open an issue in an onboarded target repository. Title and body are the request.
2. Add the label `forge`.

A short caller workflow in the target runs on the `issues.labeled` event and hands it to the engine's intake
workflow ([ADR-0015](decisions/0015-reusable-workflow-intake.md)), which dispatches it to the engine.
The label is only honoured when it is added by a human with write access to the target.

## Fields

| Field | Source | Meaning |
|---|---|---|
| `target` | The repository the event came from | Must be on the engine's allowlist |
| `title`, `body` | The issue | What to build: goal, constraints, acceptance criteria |
| `kind` | Issue label `bug`; otherwise `feature` | Changes how the Planner works |
| `base` | `.forge/config.yml`, else the default branch | Base branch for the plan branch and PR |
| `provider` | Label `forge:claude`, `forge:codex` or `forge:copilot`; else the target config; else the engine default | Which runner does the work |
| `requester` | The user who added `forge` | Recorded for audit |

Provider rules: a provider label counts only when added by a human with write access. If several provider
labels are present, the request is `blocked` with a comment asking the requester to keep one. A provider that
the engine has not enabled, or whose runner cannot enforce an agent's capabilities, also blocks the request
instead of falling back silently.

The engine re-reads the issue itself. It never trusts the webhook payload for anything beyond identifying the
issue.

## Lifecycle

State is shown as labels on the issue in the target, so the requester sees progress where they already work.

```
forge ─► analysing ─► planning ─► awaiting-approval ─► implementing ─► reviewing ─► delivered
            │                                                              │
            └─► needs-clarification (waits for the requester)              └─► replanning ─► awaiting-approval
                                                     any state ─► blocked | cancelled
```

| State | Who acts | Visible as |
|---|---|---|
| `analysing` | BA agent | Result comment on the issue |
| `needs-clarification` | Requester | Label and the agent's questions |
| `planning` | Planner agent | Label |
| `awaiting-approval` | **Human** | Label `spec-review`; the plan is a draft PR in the target |
| `implementing` | Coder and Infra agents | Label |
| `reviewing` | Reviewer agent | Label |
| `delivered` | **Human merges** the PR | Issue closed |
| `blocked` | Human | An agent could not proceed; the reason is in the result comment |

Labels and what they mean are listed in [the README](../README.md#labels).

## Idempotency

A request is identified by `target#issue-number`. Webhooks can be delivered more than once, so every stage must
be safe to repeat: re-running a stage never creates a second plan branch or a second PR.

## Result

On delivery the engine comments on the issue with the PR link, the Reviewer's verdict and a summary. The PR
lives in the same repository.
