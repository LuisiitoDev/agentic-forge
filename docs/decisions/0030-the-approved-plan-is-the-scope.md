# ADR-0030: The approved plan is the Coder's scope; targets have no configuration

**Status:** Accepted. Replaces the per-target configuration of [ADR-0006](0006-target-access-and-config.md) and the
`paths.app` scope of the delivery job and the scope gate. Implemented and tested with a fake `gh`; not run against
GitHub.

## Context

Each target needed `.forge/config.yml` with `paths.app`, or the Coder could change nothing. Of the documented fields
only `paths.app` was ever read. It was one more file for every consumer, it allowed only directories at first, and it
duplicated what the plan already says: the Planner lists the paths every task changes, and a human approves them.

## Decision

1. **The Coder's scope is the `paths` of the approved plan**: the latest Planner result posted by the engine's bot,
   read with the orchestrator's YAML reader. Files and directories both count.
2. The delivery job refuses any Coder change outside those paths, and any change to `.forge/`. The scope gate checks
   the pushed files against the same paths before review. No result, or a list that cannot be read, means nothing is
   allowed.
3. A plan that declares no paths is not approved: the orchestrator refuses `ready-for-dev` with the reason.
4. The engine reads no target configuration. `.forge/config.yml` is ignored if present.
5. Unchanged: `.github/`, `.git`, symlinks and submodules are never published; `FORGE_SENSITIVE_PATHS` needs an admin
   or maintain approver at approval and is checked again on the real changes.

## Consequences

- A consumer adds no file for the engine besides the caller workflow.
- The scope is exactly what a person approved, per request, instead of one wide list per repository.
- A broad plan path such as `src/` gives the Coder all of `src/`. The approver sees it in the plan; the Planner is told
  to prefer narrow paths. Sensitive paths under it still need the stronger approver.
- Build and test commands were never read from the config; the Coder and Reviewer find them in the repository.
