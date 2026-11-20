# GitHub App and engine configuration

How to create the GitHub App and configure the engine repository and the targets. Intake is the reusable
workflow of [ADR-0015](decisions/0015-reusable-workflow-intake.md): there is no webhook and no service to host.
For the first run, follow the [first-run checklist](first-run-checklist.md); this page is the reference behind
it. **Nothing here has run against GitHub yet.** Items marked **To verify** are not checked.

## 1. Create the GitHub App

The App is only a source of short-lived, repository-scoped tokens: the orchestrator and the agent workflows use
`FORGE_APP_ID` and `FORGE_APP_KEY` to read the issue, label, comment, push the plan branch, open the PR and look
up a sender's permission. It receives no events.

1. Create a private App by hand, or from [.github/forge/app-manifest.json](../.github/forge/app-manifest.json)
   (replace the `REPLACE-*` values). The manifest has no webhook. **To verify:** that GitHub's manifest flow
   accepts a manifest without `hook_attributes` and `default_events`. If it does not, create the App by hand
   with the webhook off.
2. Generate a private key and note the client id.
3. Install the App on the engine repository and on each target.

| Setting | Value | Status |
| --- | --- | --- |
| Repository permissions | Metadata read, Issues write, Pull requests write, Contents write; and read-only Actions, Code scanning alerts, Dependabot alerts, Secret scanning alerts ([ADR-0019](decisions/0019-reviewer-ci-and-security-tools.md)) | **To verify.** Includes the collaborator-permission lookup the orchestrator makes ([ADR-0009](decisions/0009-github-agentic-workflows.md)) |
| Webhook | Off | Intake does not use it |

## 2. Configure the engine repository

Variables and secrets are listed at the top of
[forge-orchestrator.yml](../.github/workflows/forge-orchestrator.yml): `FORGE_APP_ID`, `FORGE_APP_KEY`,
`FORGE_ALLOWED_TARGETS`, `FORGE_BOT_LOGIN`, plus the optional approval policy ([ADR-0012](decisions/0012-approval-policy.md))
and the provider credentials the agent workflows need.

Keep two allowlists equal: `FORGE_ALLOWED_TARGETS` (the orchestrator, authoritative, checked again before any
token exists) and [.github/forge/targets.txt](../.github/forge/targets.txt) (the intake's early filter, read at
the ref the target pins).

## 3. Dispatch credential

Each target needs a credential that can send `repository_dispatch` to the engine repository, stored as the secret
`FORGE_DISPATCH_TOKEN` (an organization secret works). The intake also uses it to check out the engine's
`.github/forge`. **To verify** the minimum permission (a fine-grained token on the engine repository only, with
Contents write, is the expected shape) and whether a private engine repository can be read with it.

## 4. Onboard a target

1. Install the App on the target.
2. Add the target to `.github/forge/targets.txt` and to `FORGE_ALLOWED_TARGETS`.
3. Copy [forge-caller.yml](../.github/forge/templates/forge-caller.yml) to `.github/workflows/forge.yml` in the
   target, replacing `<TAG-OR-SHA>` in both places with the same engine release, and add `FORGE_DISPATCH_TOKEN`.
4. Add `.forge/config.yml` with the paths a plan may touch.

Then follow the [first-run checklist](first-run-checklist.md) to verify it end to end.
