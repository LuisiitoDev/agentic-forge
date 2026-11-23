# First run: short path from a browser

The shortest route to the first `forge` label, for someone without a terminal. It is a condensed view of the
[first-run checklist](first-run-checklist.md); the checklist stays the full reference and says which **To verify**
each step settles. **Nothing here has run against GitHub yet.**

Replace `OWNER` with your GitHub account or organization.

## 0. Already checked by a spike (2026-11-20)

> Since [ADR-0020](decisions/0020-claude-engine-and-rate-limits.md) the agents use the **Claude** engine: set the
> secret `ANTHROPIC_API_KEY` instead of `COPILOT_GITHUB_TOKEN`. The Copilot notes below are kept as the record of
> that spike.

You do not need to repeat it. With a Copilot token in `COPILOT_GITHUB_TOKEN` on this repository:

- The token works and the Copilot CLI runs non-interactively. Only `gpt-4.1` was usable on that account, so the agent
  workflows pin it ([ADR-0017](decisions/0017-copilot-engine-for-phase-1.md)).
- A workflow cannot learn that an agent run it started has ended through `workflow_run`. The orchestrator waits for
  its agents instead ([ADR-0018](decisions/0018-orchestrator-awaits-agent-runs.md)).

The token steps, if you still need to create it: open
<https://github.com/settings/personal-access-tokens/new?name=COPILOT_GITHUB_TOKEN&description=Agentic+Forge+Copilot&user_copilot_requests=read>
(resource owner: your user; account permission Copilot Requests = Read; an active Copilot license), generate it, and
save it as the repository secret `COPILOT_GITHUB_TOKEN`.

## A. Accounts and secrets (about 15 minutes, GitHub web, once)

1. **Sandbox repository.** New private repository `OWNER/forge-sandbox`, with a README. Settings, Actions, enabled.
2. **GitHub App** (a settings page, in a browser): Settings, Developer settings, GitHub Apps, New. Name
   `OWNER-forge`; Webhook **off**; permissions Metadata read, Issues write, Pull requests write, Contents write, and read-only Actions, Code scanning alerts, Dependabot alerts and Secret scanning alerts (the Reviewer reads them, [ADR-0019](decisions/0019-reviewer-ci-and-security-tools.md)); only
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
   | Secret | `ANTHROPIC_API_KEY` | an Anthropic API key ([ADR-0020](decisions/0020-claude-engine-and-rate-limits.md)) |
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

## B2. Check it before you try

Engine repository, Actions tab, workflow `forge-preflight`, Run workflow, target `OWNER/forge-sandbox`. Open the run
summary: every row must be PASS. A FAIL says what to fix. You can also send me the run and I read it. This catches
a wrong App id or key, a bot login that does not match, missing labels or config, a caller with the placeholder or two
different pins, and a pin that does not yet list the sandbox.

## C. Run

1. New issue in the sandbox, with the text of [issue.md](../.github/forge/templates/sandbox/issue.md).
2. Add the label `forge`. Then follow sections 5 to 8 of the [checklist](first-run-checklist.md) and write down
   **Verified**, **Failed** or **Workaround** for each.
3. Send the engine's Actions run links when something fails. Failures are fixed with a test first (handoff W2).

## Clean up

Remove `FORGE_ALLOW_SELF_APPROVAL`, and revoke the dispatch token if you do not keep the sandbox.
