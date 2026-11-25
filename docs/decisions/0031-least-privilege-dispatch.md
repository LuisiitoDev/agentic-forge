# ADR-0031: A least-privilege dispatch token, and label events checked on the issue

**Status:** Accepted. Amends [ADR-0015](0015-reusable-workflow-intake.md) (how the intake reaches the engine).
Implemented and tested with a fake `gh`; not run against GitHub.

## Context

The intake reached the engine with `repository_dispatch`, which needs a token with **Contents: write** on the engine
repository: whoever held a target's `FORGE_DISPATCH_TOKEN` could push code to the engine. The token cannot be removed:
a workflow's own `GITHUB_TOKEN` can never start anything in another repository, and the engine has no hosted service
(ADR-0016) and does not poll.

Worse, the orchestrator believed the payload's `sender` and `label`. It checked that the claimed sender had write
access, not that they added the label. A holder of any target's token could dispatch "a maintainer of target B
added `ready-for-dev`" and approve a plan in B.

## Decision

1. **The intake starts the orchestrator with `workflow_dispatch`** (inputs `target`, `issue`, `label`, `sender`,
   `delivery`), which needs only **Actions: write**, plus Contents: read for the intake checkout when the engine is
   private. The orchestrator keeps `repository_dispatch` for callers pinned to an older intake.
2. **The orchestrator checks every human event on the issue itself**: the label is on the issue, and the latest
   `labeled` event for that label in `GET /repos/{target}/issues/{n}/events` is by the claimed sender. Otherwise
   nothing happens. Only `forge`, `ready-for-dev` and `clarified` are human events.
3. **An organization secret is the documented default**, so a new target in that organization configures nothing.

## Consequences

- A leaked dispatch token can start orchestrator runs that do nothing unless the issue agrees, and cannot touch the
  engine's code.
- One more API call per human event.
- A token made for the older intake (Contents: write, no Actions: write) is refused with 403 on `workflow_dispatch`.
  The intake then sends the same event as `repository_dispatch` and prints a warning to add Actions: write. Any
  other failure, and a failed fallback, fails the run with GitHub's `HTTP <code>: <message>` (never the token).
  Seen on the sandbox on 2026-11-25: the first request after re-pinning failed with only "dispatch failed".
- The two ways in stay until every caller is re-pinned; `repository_dispatch` can then be removed, and with it the
  need for Contents: write.
