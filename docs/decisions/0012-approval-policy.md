# ADR-0012: Approval policy: no self-approval, and sensitive paths need a stronger approver

**Status:** Accepted. Refines [ADR-0011](0011-approval-by-write-access.md).

## Context

ADR-0011 lets any human with write access approve a plan. That means someone can approve their own request, and
someone can approve a plan for areas that deserve more care. Approval only starts work on a branch; the merge is
still guarded by the target's branch protection. The risk is mainly cost and accountability, not production.

## Decision

Two rules are checked when `ready-for-dev` is added. Both are engine configuration. Neither is read from the
target, because the target's contents are untrusted.

**1. No self-approval.** The person who opened the request issue may not approve its plan. Another person with
write access must. Set `FORGE_ALLOW_SELF_APPROVAL=true` to allow it, for example in a team of one.

**2. Sensitive paths need the admin or maintain role.** `FORGE_SENSITIVE_PATHS` lists path prefixes (comma or
newline separated, for example `infra/,.github/workflows/`). If the plan touches any of them, the approver must
have the `admin` or `maintain` role on the target. With the setting empty, this rule is off.

How the second rule decides:

- The paths checked are the **union** of the paths the plan declares and the files actually changed on the plan
  branch. A plan cannot hide a path by leaving it out of its own list.
- Matching is by **whole path segment**, so `infra/` matches `infra/x` but not `infra-docs/x`.
- Matching is **case-insensitive**, which is stricter and safe on any filesystem.
- A path containing `..` counts as a hit, because it could point outside the prefix it appears under.

Both rules **fail closed**: if the requester, the changed files or the approver's role cannot be read, the
approval is denied and the engine says why. A refusal removes `ready-for-dev` and keeps `spec-review`, so a
different person can approve.

## Alternatives considered

- **CODEOWNERS** ([ADR-0008](0008-approval-authority.md)). Needs a gitignore-style matcher and team lookups. Omitted.
- **Approving with the PR review and reading `reviewDecision`.** Reuses GitHub's own ownership logic, but needs
  the relay to forward pull request events. Left for later.
- **A named approver list per target.** Duplicates ownership and needs upkeep. Not chosen.

## Consequences

- Two people are involved in every change by default, which a team of one must opt out of explicitly.
- The sensitive list is **global**, not per target, and matches prefixes only (no patterns).
- The role comes from the collaborator permission API's `role_name` field (**to verify**), so a custom
  repository role is not recognised as `maintain` and is denied.
- The check runs when the label is added. Files added to the plan branch afterwards are caught by the scope gate
  before review, not by this check.
