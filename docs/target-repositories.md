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
   engine release, and add the secret `FORGE_DISPATCH_TOKEN`. It runs only when a label is added, never on comments
   ([ADR-0029](decisions/0029-labels-are-the-only-trigger.md)).
4. Add `.forge/config.yml` with `paths.app` (below). Without it the Coder may change nothing.

The labels need no setup: the engine creates them when the first request starts.

## Per-target configuration

Targets describe themselves so agents do not guess. Without the file, the engine uses safe defaults
(read-only analysis, no shell until configured).

```yaml
# .forge/config.yml  (illustrative)
build:   "dotnet build"
test:    "dotnet test"
paths:
  app:   [src/, tests/]
conventions: docs/CONTRIBUTING.md
limits:
  shell: true
```

| Field | Use |
|---|---|
| `build`, `test` | Commands the Coder and Reviewer run |
| `paths.app` | Becomes the `write_paths` of the Coder for this target |
| `conventions` | A file the agents read first |
| `limits` | May only **narrow** what the engine allows, never widen it |

This file lives in the target and is therefore **untrusted input**. It is read as data. It can tighten limits
but cannot grant capabilities, add writable paths outside the repository, or change approval rules.

## Ownership

The engine does not read `CODEOWNERS`: any human with write access may approve a plan
([ADR-0011](decisions/0011-approval-by-write-access.md)). To enforce ownership, enable branch protection with
*Require review from Code Owners* on the target. GitHub then applies it to the plan PR at merge.

## Isolation

Each request runs in its own Actions job with its own checkout of the target. No state or credentials are
shared between requests or between targets.
