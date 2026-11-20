# First run: short path from a browser

The shortest route to the first `forge` label, for someone without a terminal. It is a condensed view of the
[first-run checklist](first-run-checklist.md); the checklist stays the full reference and says which **To verify**
each step settles. **Nothing here has run against GitHub yet.**

Replace `OWNER` with your GitHub account or organization.

## A. Accounts and secrets (about 15 minutes, GitHub web, once)

1. **Sandbox repository.** New private repository `OWNER/forge-sandbox`, with a README. Settings, Actions, enabled.
2. **GitHub App** (a settings page, in a browser): Settings, Developer settings, GitHub Apps, New. Name
   `OWNER-forge`; Webhook **off**; permissions Metadata read, Issues write, Pull requests write, Contents write; only
   on this account. Create it, note the **Client ID**, generate a **private key** (a `.pem` download).
   The manifest flow needs a form post, so create it by hand. Record whether GitHub accepted an App with no webhook.
3. **Install the App** on `OWNER/agentic-forge` and on `OWNER/forge-sandbox` only.
4. **Dispatch token.** Settings, Developer settings, Fine-grained tokens, New. Repository access: only
   `OWNER/agentic-forge`. Permission: Contents read and write. Copy it.
5. **Engine repository** (`OWNER/agentic-forge`, Settings, Secrets and variables, Actions):

   | Kind | Name | Value |
   | --- | --- | --- |
   | Variable | `FORGE_APP_ID` | the Client ID |
   | Secret | `FORGE_APP_KEY` | the whole `.pem` text |
   | Variable | `FORGE_ALLOWED_TARGETS` | `OWNER/forge-sandbox` |
   | Variable | `FORGE_BOT_LOGIN` | `<app-slug>[bot]` (the slug is in the App's URL) |
   | Variable | `FORGE_ALLOW_SELF_APPROVAL` | `true` (sandbox only, remove later) |
   | Secret | `COPILOT_GITHUB_TOKEN` | a token with Copilot access (type **To verify**, see [ADR-0017](decisions/0017-copilot-engine-for-phase-1.md)) |
   | Secret | `FORGE_ATLASSIAN_AUTH` | any placeholder, unless you use Jira |
   | Secret | `FORGE_ADO_PAT` | any placeholder, unless you use Azure DevOps |
   | Variable | `FORGE_ADO_ORG` | any placeholder, unless you use Azure DevOps |

   The MCP secrets are placeholders because BA and Planner declare those servers ([ADR-0013](decisions/0013-agent-mcp-servers.md)).
   With placeholders, those tools fail; note whether the agent still runs.
6. **Engine allowlist file.** Add the line `OWNER/forge-sandbox` to [.github/forge/targets.txt](../.github/forge/targets.txt),
   merge it to `main`, and copy the **full commit SHA** of that merge. This is `ENGINE_REF`.
   If the engine repository is private: Settings, Actions, General, Access, allow access from repositories in your
   account. Record the exact wording.

## B. Sandbox repository (about 5 minutes)

1. Secret `FORGE_DISPATCH_TOKEN` = the token from step 4.
2. Add the three files in [.github/forge/templates/sandbox/](../.github/forge/templates/sandbox/README.md):
   `forge.yml` as `.github/workflows/forge.yml` with `ENGINE_REF` in both places, and `.forge/config.yml`.
3. Create the labels `forge` and `ready-for-dev`.

## C. Run

1. New issue in the sandbox, with the text of [issue.md](../.github/forge/templates/sandbox/issue.md).
2. Add the label `forge`. Then follow sections 5 to 8 of the [checklist](first-run-checklist.md) and write down
   **Verified**, **Failed** or **Workaround** for each.
3. Send the engine's Actions run links when something fails. Failures are fixed with a test first (handoff W2).

## Clean up

Remove `FORGE_ALLOW_SELF_APPROVAL`, and revoke the dispatch token if you do not keep the sandbox.
