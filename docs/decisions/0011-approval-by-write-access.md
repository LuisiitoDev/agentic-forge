# ADR-0011: Approval is a human with write access (CODEOWNERS omitted)

**Status:** Accepted. Supersedes [ADR-0008](0008-approval-authority.md). Refined by
[ADR-0012](0012-approval-policy.md), which adds no self-approval and a stronger approver for sensitive paths.

## Context

ADR-0008 also required the approver to own every path in the plan when the target has a `CODEOWNERS` file.
Implementing that needs a gitignore-style matcher, team resolution with an extra App permission, and decisions
about unowned paths and about several approvers. It was never built, and while unbuilt it failed closed, which
blocked every target that has a `CODEOWNERS` file.

## Decision

| Action | Who may do it |
|---|---|
| Start a request (`forge`), choose a provider (`forge:<provider>`) | A human with **write access** to the target |
| Approve implementation (`ready-for-dev`) | A human with **write access** to the target |
| Merge the PR | Whatever the target's branch protection requires. The engine never merges |

Rules kept from ADR-0008:

1. **Human only.** Events caused by the App's own bot never count, whatever the label.
2. **Checked by the engine**, from the event's sender and the target's current state, never from claims in the
   dispatch payload.

The engine does **not** read `CODEOWNERS`.

## Consequences

- Any target works, with or without a `CODEOWNERS` file.
- Anyone with write access can approve a plan, including for paths they do not own.
- Ownership is still enforceable where it matters most: at **merge**. Targets that care should enable branch
  protection with *Require review from Code Owners*, which GitHub applies to the plan PR. Nothing the engine does
  can bypass it.
- `gh aw` still refuses to let an agent edit `CODEOWNERS` without a review request
  ([ADR-0009](0009-github-agentic-workflows.md)).
- If approval by ownership is wanted later, it needs a new ADR and the work listed in ADR-0008.
