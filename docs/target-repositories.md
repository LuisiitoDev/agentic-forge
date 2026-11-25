# Target repositories

A target is any repository the engine may work in. Targets need no pipeline code.

## Access

- The engine uses a **GitHub App** installed on each target. At run time it mints a short-lived installation
  token scoped to that single repository, with only the permissions it needs (contents and pull requests).
- Agents never see this token. The workflow, not the agent, clones, pushes the plan branch and opens the PR.
- The engine keeps an **allowlist** of targets. A request for any other repository is rejected at intake.

## Onboarding a target

1. Install the GitHub App on the repository.
2. Add it to the engine's allowlist (`.github/forge/targets.txt` and `FORGE_ALLOWED_TARGETS`).
3. Copy the caller [`forge.yml`](../.github/forge/templates/forge-caller.yml) to `.github/workflows/`, pinned to an
   engine release. It runs only when a label is added, never on comments
   ([ADR-0029](decisions/0029-labels-are-the-only-trigger.md)).
4. The secret `FORGE_DISPATCH_TOKEN`: a fine-grained token on the engine repository with Actions: write (and Contents:
   read if the engine is private). Nothing to do when the organization already has it as an organization secret
   ([ADR-0031](decisions/0031-least-privilege-dispatch.md)).

The labels need no setup: the engine creates them when the first request starts.

## What the Coder may change

Nothing to configure ([ADR-0030](decisions/0030-the-approved-plan-is-the-scope.md)). The Planner lists the `paths` every task changes, files or directories, and a
human approves that plan. Those paths are the Coder's whole scope: the delivery job refuses any other file, and the
scope gate checks the pushed changes again before review. A plan that declares no paths is not approved.

Whatever the plan says, the engine never publishes a change to `.github/`, `.git`, `.forge/` (outside the plan
directory, which only the Planner writes), a symlink or a submodule. Paths in `FORGE_SENSITIVE_PATHS` still need an
admin or maintain approver.

## Ownership

The engine does not read `CODEOWNERS`: any human with write access may approve a plan
([ADR-0011](decisions/0011-approval-by-write-access.md)). To enforce ownership, enable branch protection with
*Require review from Code Owners* on the target. GitHub then applies it to the plan PR at merge.

## Isolation

Each request runs in its own Actions job with its own checkout of the target. No state or credentials are
shared between requests or between targets.
