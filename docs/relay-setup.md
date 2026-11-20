# GitHub App and optional relay setup

How to create the GitHub App and, optionally, deploy the relay. The default intake is the reusable workflow of
[ADR-0015](decisions/0015-reusable-workflow-intake.md) and needs **no webhook and no relay**. The relay
([ADR-0007](decisions/0007-webhook-relay.md)) is the later alternative and produces the same dispatch payload.
For a first run, follow the [first-run checklist](first-run-checklist.md); this page is the reference behind it.

| Piece | Default (reusable workflow) | With the relay |
| --- | --- | --- |
| GitHub App | **Needed**, to act on targets (section 1) | Needed, plus its webhook |
| App webhook and events | Off | On: `issues`, `issue_comment` |
| Relay (section 2) | Not used | Deployed |
| Dispatch credential | `FORGE_DISPATCH_TOKEN` secret in each target (or the org) | `RELAY_DISPATCH_TOKEN` in the relay |
| Engine variables (section 3) | Needed | Needed |

The relay code is in [relay/](../relay/). **Nothing here has run against GitHub yet.** Items marked
**To verify** are not checked.

## 1. Create the GitHub App (needed either way)

In the default path the App is only a source of short-lived, repository-scoped tokens: the orchestrator and the
agent workflows use `FORGE_APP_ID` and `FORGE_APP_KEY` to read the issue, label, comment, push the plan branch,
open the PR and look up a sender's permission. It receives no events.

1. Create a private App by hand, or from [relay/app-manifest.json](../relay/app-manifest.json) (replace the
   `REPLACE-*` values). **To verify:** the manifest ships the relay's webhook (`hook_attributes`,
   `default_events`). For the default path, remove those two keys or turn the webhook off in the App settings.
2. Generate a private key and note the client id.
3. Install the App on the engine repository and on each target.
4. Only with the relay: set a **webhook secret** in the App settings (the manifest flow does not set it) and
   keep it for section 2.

| Setting | Value | Status |
| --- | --- | --- |
| Repository permissions | Metadata read, Issues write, Pull requests write, Contents write | **To verify.** Includes the collaborator-permission lookup the orchestrator makes ([ADR-0009](decisions/0009-github-agentic-workflows.md)) |
| Events (relay only) | `issues`, `issue_comment` | Matches what the relay forwards |

## 2. Deploy the relay (optional; Azure Function, Python)

Settings (application settings; never commit them):

| Setting | Meaning |
| --- | --- |
| `RELAY_WEBHOOK_SECRET` | The App's webhook secret |
| `RELAY_ENGINE_REPO` | `owner/name` of this engine repository |
| `RELAY_DISPATCH_TOKEN` | Token that can send `repository_dispatch` to the engine repository and nothing else. **To verify** the minimum permission (fine-grained token, Contents write, one repository, is the expected shape) |
| `RELAY_ALLOWED_TARGETS` | Targets the relay forwards, `owner/name`, comma or newline separated |
| `RELAY_BOT_LOGIN` | The App's bot login, for example `my-forge[bot]` |

Set the App's webhook URL to `https://<function-app>/webhook`. The route is anonymous because GitHub cannot
send a function key; the HMAC signature is the authentication. Local run: copy
`relay/local.settings.example.json` to `local.settings.json` and use Azure Functions Core Tools
(**To verify** the exact version and Python runtime to deploy).

The relay returns 401 for a bad signature, 200 for an ignored event, 202 when dispatched and 502 when the
dispatch failed, so GitHub can redeliver. Stages are idempotent, so redelivery is safe.

## 3. Configure the engine repository (needed either way)

Variables and secrets are listed at the top of
[forge-orchestrator.yml](../.github/workflows/forge-orchestrator.yml): `FORGE_APP_ID`, `FORGE_APP_KEY`,
`FORGE_ALLOWED_TARGETS`, `FORGE_BOT_LOGIN`, plus the optional approval policy ([ADR-0012](decisions/0012-approval-policy.md))
and the provider credentials the agent workflows need.

Keep the allowlists equal: `FORGE_ALLOWED_TARGETS` (the orchestrator, authoritative, checked again before any
token exists) and the early filter, which is `.github/forge/targets.txt` for the reusable-workflow intake or
`RELAY_ALLOWED_TARGETS` for the relay.

## 4. Check it end to end

- Default path: follow the [first-run checklist](first-run-checklist.md).
- With the relay: send a ping from the App's advanced tab (expect 200), then label an issue `forge` in an allowed
  target. Expect a `forge-orchestrator` run, then `forge-ba`. Record the results in the **Not yet verified** list
  of [ADR-0009](decisions/0009-github-agentic-workflows.md).
