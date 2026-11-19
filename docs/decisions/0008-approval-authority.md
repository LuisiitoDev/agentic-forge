# ADR-0008: Approval authority is write access, narrowed by CODEOWNERS

**Status:** Superseded by [ADR-0011](0011-approval-by-write-access.md). The CODEOWNERS rule below was never
implemented and is no longer part of the design.

## Context

Agents plan, build and review; humans approve consequences. The engine must decide who counts as a human
approver, using what GitHub already knows about the target, without inventing a separate permission system.

## Decision

| Action | Who may do it |
|---|---|
| Start a request (`forge`), choose a provider (`forge:<provider>`) | A human with **write access** to the target |
| Approve implementation (`ready-for-dev`) | A human with write access **and**, when the target has a `CODEOWNERS` file, a **code owner** of every path the plan changes |
| Merge the PR | Whatever the target's branch protection requires. The engine never merges |

Rules:

1. **Human only.** Events caused by the App's own bot never count, whatever the label.
2. **Checked by the engine**, from the event's sender and the target's current state, never from claims in the
   dispatch payload.
3. **CODEOWNERS is read from the base branch**, not from the plan branch, so a plan cannot grant itself
   approval rights by editing `CODEOWNERS`.
4. **Fail closed.** If `CODEOWNERS` exists but ownership cannot be resolved (for example a team that cannot be
   expanded), approval is denied and the engine comments why. A person is never approved by default.
5. **No `CODEOWNERS`, no extra rule.** Write access alone applies.
6. **Paths come from the plan.** The set of paths is the files changed on the plan branch, plus the paths the
   plan declares it will change, whichever is wider.

## Alternatives considered

- **Write access only.** Simple, but any collaborator could approve changes to areas they do not own.
- **A named approver list in engine config.** Explicit, but duplicates what `CODEOWNERS` already expresses and
  drifts from it.

## Consequences

- Approval authority follows the target's own ownership model and needs no extra configuration.
- Expanding team owners may need an extra App permission to read organization membership (**to verify**).
- The merge gate is already enforced by GitHub when branch protection requires code owner review. The engine
  adds the same discipline to the earlier approval step.
