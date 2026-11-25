# ADR-0032: One allowlist, in the orchestrator

**Status:** Accepted. Amends [ADR-0015](0015-reusable-workflow-intake.md), whose intake filtered by a file. Implemented
and tested with a fake `gh`; not run against GitHub.

## Context

Two lists named the targets the engine serves: `.github/forge/targets.txt`, read by the intake, and the variable
`FORGE_ALLOWED_TARGETS`, read by the orchestrator. They had to be kept equal by hand, a target was in the file only
after a merge, and the intake read the file **at the commit the target pinned**, so a new target also had to re-pin.
Preflight and the first-run checklist had rows to check each of these. The file existed for one reason: in a called
workflow `vars` resolve against the caller, so the intake could not read the engine's variable.

## Decision

1. **`targets.txt` is removed. The only allowlist is `FORGE_ALLOWED_TARGETS`**, checked by the orchestrator's
   `resolve` job, which runs in the engine, before any credential is created.
2. The intake forwards any label event whose repository name is well formed, and keeps its other filters: bots,
   pull requests and labels it does not handle.
3. Preflight checks the variable only, and that the commit the caller pins has the intake.

## Consequences

- Onboarding a target is one variable edit by the engine's owner. Nothing to merge, no new pin.
- A repository that is not allowed, but has the dispatch token (an organization secret reaches every repository of the
  organization), starts one short `resolve` job in the engine that skips it. No credential is created for it.
- An older caller pinned to an engine that still has `targets.txt` keeps working: its intake reads its own pinned copy,
  and the orchestrator now applies the variable.
