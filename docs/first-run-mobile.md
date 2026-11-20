# First run: short path from a browser

The shortest route to the first `forge` label, for someone without a terminal. It is a condensed view of the
[first-run checklist](first-run-checklist.md); the checklist stays the full reference and says which **To verify**
each step settles. **Nothing here has run against GitHub yet.**

Replace `OWNER` with your GitHub account or organization.

## 0. Spike first (about 10 minutes, no App, no intake)

Proves the two riskiest facts before anything else: that the Copilot engine runs, and that a workflow can see the
result of an agent run started by another workflow ([ADR-0009](decisions/0009-github-agentic-workflows.md)). It
needs only the Copilot token. The spike files are `forge-spike.md`, `forge-spike-start.yml` and
`forge-spike-watch.yml`; delete them afterwards.

1. **Token.** Open the pre-filled link (it works in a phone browser; use "desktop site" if the page is cramped):
   <https://github.com/settings/personal-access-tokens/new?name=COPILOT_GITHUB_TOKEN&description=Agentic+Forge+Copilot&user_copilot_requests=read>
   - Resource owner: **your user account**, not an organization.
   - Account permissions: **Copilot Requests = Read**. Nothing else is needed.
   - Generate and copy the token. Your account must have an active Copilot license.
2. **Secret.** `OWNER/agentic-forge`, Settings, Secrets and variables, Actions, New repository secret:
   name `COPILOT_GITHUB_TOKEN`, value the token.
3. **Merge the branch** with the spike files to `main` (workflow triggers run from the default branch).
4. **Control run.** Actions tab, workflow `forge-spike`, Run workflow, any `target` (`OWNER/agentic-forge`),
   `issue` (`1`) and `model` (`gpt-4.1`, the one that worked on the author's account). Wait until it ends. This is a
   human dispatch. The run is expected to end red after the agent step: the spike has no safe output, so
   `safe_outputs` fails. What matters is the `agent` job.
5. **The real test.** Actions tab, workflow `forge-spike-start`, Run workflow, `model` = `gpt-4.1`. It starts `forge-spike` with the
   workflow token, like the orchestrator does.
6. Open `forge-spike-watch` runs. Note, for each of the two runs above, whether `forge-spike-watch` **ran at all**
   and what its summary table says.
7. Send me the repository name and say "spike done", with any error text you see. I read the runs myself.

What each result means:

| Result | Meaning |
| --- | --- |
| `forge-spike` ends green | The token works and Copilot ran non-interactively |
| `forge-spike` fails at the activation step | Token type or license; the log says which |
| Watch ran for the control but **not** for the `forge-spike-start` run | The orchestrator cannot advance as designed. Needs an ADR and a redesign, which is good to know now |
| Watch ran for both and `title parsed ... yes` | The orchestrator's trigger and its title parsing work |

Then continue with section A. Do not build the App before step 6 is green.

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
