# Target repositories

A target is any repository the engine may work in. Targets need no pipeline code.

## Access

- The engine uses a **GitHub App** installed on each target. At run time it mints a short-lived installation
  token scoped to that single repository, with only the permissions it needs (contents and pull requests).
- Agents never see this token. The workflow, not the agent, clones, pushes the plan branch and opens the PR.
- The engine keeps an **allowlist** of targets. A request for any other repository is rejected at intake.

## Onboarding a target

1. Install the GitHub App on the repository. Webhooks start flowing; no workflow is added to the target.
2. Add it to the engine's allowlist.
3. Create the labels the engine uses (`forge`, `spec-review`, `ready-for-dev`, and the others listed in
   [labels](../README.md#labels)).
4. Optionally add `.forge/config.yml` to the target (below).

## Per-target configuration

Targets describe themselves so agents do not guess. Without the file, the engine uses safe defaults
(read-only analysis, no shell until configured).

```yaml
# .forge/config.yml  (illustrative)
build:   "dotnet build"
test:    "dotnet test"
paths:
  app:   [src/, tests/]
  infra: [infra/]
conventions: docs/CONTRIBUTING.md
limits:
  shell: true
```

| Field | Use |
|---|---|
| `build`, `test` | Commands the Coder and Reviewer run |
| `paths.app`, `paths.infra` | Become the `write_paths` of Coder and Infra for this target |
| `conventions` | A file the agents read first |
| `limits` | May only **narrow** what the engine allows, never widen it |

This file lives in the target and is therefore **untrusted input**. It is read as data. It can tighten limits
but cannot grant capabilities, add writable paths outside the repository, or change approval rules.

## Ownership

If the target has a `CODEOWNERS` file, it decides who may approve a plan: the approver must own every path the
plan changes ([ADR-0008](decisions/0008-approval-authority.md)). The engine reads it from the base branch. Pair
it with branch protection that requires code owner review, so the merge is guarded by GitHub itself.

## Isolation

Each request runs in its own Actions job with its own checkout of the target. No state or credentials are
shared between requests or between targets.
