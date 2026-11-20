# Relay and GitHub App setup

How to stand up the **optional relay** and the GitHub App. The default intake is the reusable workflow of
[ADR-0015](decisions/0015-reusable-workflow-intake.md), which needs no relay (not built yet); the relay
([ADR-0007](decisions/0007-webhook-relay.md)) is the later alternative and produces the same dispatch payload.
The GitHub App (section 1) and the engine variables (section 3) are needed either way. The relay code is in
[relay/](../relay/). **Nothing here has run against GitHub yet.** Items marked **To verify** are not checked.

## 1. Create the GitHub App

1. Create a private App from [relay/app-manifest.json](../relay/app-manifest.json) (replace the `REPLACE-*`
   values) or by hand with the same settings.
2. Set a **webhook secret** in the App settings (the manifest flow does not set it). Keep it for step 2.
3. Generate a private key.
4. Install the App on the engine repository and on each target.

| Setting | Value | Status |
| --- | --- | --- |
| Events | `issues`, `issue_comment` | Matches what the relay forwards |
| Repository permissions | Metadata read, Issues write, Pull requests write, Contents write | **To verify.** Includes the collaborator-permission lookup the orchestrator makes ([ADR-0009](decisions/0009-github-agentic-workflows.md)) |

## 2. Deploy the relay (Azure Function, Python)

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

## 3. Configure the engine repository

Variables and secrets are listed at the top of
[forge-orchestrator.yml](../.github/workflows/forge-orchestrator.yml): `FORGE_APP_ID`, `FORGE_APP_KEY`,
`FORGE_ALLOWED_TARGETS`, `FORGE_BOT_LOGIN`, plus the optional approval policy ([ADR-0012](decisions/0012-approval-policy.md))
and the provider credentials the agent workflows need. `RELAY_ALLOWED_TARGETS` and `FORGE_ALLOWED_TARGETS`
should list the same targets: the relay filters early, the orchestrator checks again before any token exists.

## 4. Check it end to end

1. Send a ping from the App's advanced tab. Expect 200.
2. Label an issue `forge` in an allowed target. Expect a `forge-orchestrator` run, then `forge-ba`.
3. Work through the **Not yet verified** list in ADR-0009 and record the results there.
