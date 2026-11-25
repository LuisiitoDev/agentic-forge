# First real run: checklist

Goal: label an issue `forge` in a throwaway target and watch it go through intake, orchestrator and agents on
real GitHub, recording what holds and what does not. Nothing here has run before. Every step that settles a
**To verify** says which one, and where to write the answer.

Use a **throwaway private repository** you own as the target, and a cheap, bounded task ("add a README line").
Do the stages in order; stop at the first failure, fix the cause, and record it.

## 0. Before you start

- [ ] Engine repository: `LuisiitoDev/agentic-forge`, default branch `main`, Actions enabled.
- [ ] A throwaway target repository (call it `OWNER/forge-sandbox`) with Actions enabled and the issue tracker on.
- [ ] You can create a GitHub App and fine-grained tokens, and you have the OAuth token of `claude setup-token` for `CLAUDE_CODE_OAUTH_TOKEN`.
- [ ] Decide the release ref the target will pin: for the first run use a **full commit SHA** of `main`
      (a tag can come later). Write it down: `ENGINE_REF=<sha>`.

## 1. GitHub App (acts on targets)

- [ ] Create a private App from [.github/forge/app-manifest.json](../.github/forge/app-manifest.json) (no webhook) or by hand.
      Record whether the manifest flow accepted it without a webhook.
- [ ] Permissions: Metadata read, Issues write, Pull requests write, Contents write, plus read-only Actions, Code scanning
      alerts, Dependabot alerts and Secret scanning alerts ([ADR-0019](decisions/0019-reviewer-ci-and-security-tools.md)). Webhooks off (intake does
      not use them).
- [ ] Generate a private key. Note the **client id** and the App's bot login (`<app-slug>[bot]`).
- [ ] Install it on the engine repository and on `OWNER/forge-sandbox` only.
- Settles: *App permissions needed for the collaborator lookup* ([ADR-0009](decisions/0009-github-agentic-workflows.md)).
  Note any permission the orchestrator asks for that is missing.

## 2. Engine repository configuration

Settings, Secrets and variables, Actions:

- [ ] Variable `FORGE_APP_ID` = the App's client id
- [ ] Secret `FORGE_APP_KEY` = the private key (full PEM)
- [ ] Variable `FORGE_ALLOWED_TARGETS` = `OWNER/forge-sandbox`
- [ ] Variable `FORGE_BOT_LOGIN` = `<app-slug>[bot]`
- [ ] Secret `CLAUDE_CODE_OAUTH_TOKEN`: the preflight fails without it, because every agent runs on Claude Code ([ADR-0022](decisions/0022-all-agents-on-claude-in-awf.md)).
- [ ] Optional: `FORGE_ALLOW_SELF_APPROVAL=true` **for the sandbox only**, so you can approve your own request.
      Remove it after the run; the real policy is no self-approval ([ADR-0012](decisions/0012-approval-policy.md)).
- [ ] Every agent runs on Claude Code inside AWF (`forge-<agent>-claude`, secret `CLAUDE_CODE_OAUTH_TOKEN`; [ADR-0022](decisions/0022-all-agents-on-claude-in-awf.md), [ADR-0024](decisions/0024-claude-code-only.md)).
      The App needs actions, code scanning, Dependabot and secret scanning read access for the Reviewer's token.
      Only the BA has run on this design: expect to fix the first run of each other agent.
- [ ] `ENGINE_REF` is a full commit SHA of the engine that has `.github/forge/intake.py`. The allowlist is the
      variable `FORGE_ALLOWED_TARGETS` only.

## 3. Dispatch token

- [ ] Create a **fine-grained token** scoped to the engine repository only, with `Actions: write`, and
      `Contents: read` if the engine is private ([ADR-0031](decisions/0031-least-privilege-dispatch.md)).
- [ ] Store it as secret `FORGE_DISPATCH_TOKEN`, in the organization (preferred) or in the target.
- Settles: *minimum permission of the dispatch credential* and *private engine readable with this token*
  ([intake spec](intake-spec.md) open points 2, and [ADR-0015](decisions/0015-reusable-workflow-intake.md)).
  If the dispatch is refused, record which permission was missing.

## 4. Target repository

- [ ] Allow the target to call the engine's workflows: engine repo Settings, Actions, General, Access (private
      repos only). Record the exact setting name that was needed.
- [ ] Copy [forge-caller.yml](../.github/forge/templates/forge-caller.yml) to `.github/workflows/forge.yml` and
      replace `<TAG-OR-SHA>` with `ENGINE_REF` in **both** places.
- [ ] Create the label `forge` (the engine creates the others when the request starts). No `.forge/config.yml`:
      the Coder's scope is the approved plan's paths.

## 4b. Pre-flight check

- [ ] Actions tab of the engine, workflow `forge-preflight`, Run workflow, target `OWNER/forge-sandbox`. Open the run
      summary. Every row must be PASS (INFO rows are things it cannot check). It reads the engine variables and
      secrets, creates the App token, looks at the target's labels and caller workflow, and checks
      that the ref the caller pins has the intake. Fix every FAIL before Stage A.
- Settles: *App permissions needed for the collaborator lookup* and that the App token can be made
  ([ADR-0009](decisions/0009-github-agentic-workflows.md)), and the output `app-slug` of the token action (**To verify**:
  if the row `bot login` says the slug is unknown, the action does not provide it).
- It cannot check the dispatch token (it lives in the target); Stage A does.

## 5. Stage A: intake only

Open an issue in the target ("Add a line to the README"), then add the label `forge`.

- [ ] The target's Actions tab shows a `forge` run, and it **starts** (the call to the engine was accepted).
- [ ] The intake job log shows `dispatch {...}` then `dispatched`, with the payload you expect.
- [ ] The engine's Actions tab shows a `forge-orchestrator` run for event `workflow_dispatch`.
- [ ] Negative checks, each must produce **no** dispatch:
  - [ ] Comment on the issue before it has the label `forge` (ignored: not a request)
  - [ ] Comment on a pull request
  - [ ] Label from an account that is a bot (skip if you have none)
  - [ ] A repository that is not in `FORGE_ALLOWED_TARGETS` (the intake forwards it; the orchestrator skips it)
- Settles: *`github.event`, `sender.type`, `issue.pull_request` present in the called workflow*, *`vars`/`secrets`
  resolving against the caller*, and *spoofing of `github.repository`*
  ([intake spec](intake-spec.md) section 8). Record each answer there.

## 6. Stage B: orchestrator and the first agent

- [ ] The orchestrator resolves the event, passes the allowlist, checks the sender has write access, and
      dispatches `forge-ba` (labels move to `analysing`).
- [ ] `forge-ba` runs, ends, and its run title is `forge-ba OWNER/forge-sandbox#<n>`.
- [ ] **The same** orchestrator run finds that run (log line `waiting for ba run <id>`), waits for it, reads its
      result and dispatches the planner, without a second orchestrator run ([ADR-0018](decisions/0018-orchestrator-awaits-agent-runs.md)).
- Settles: the run lookup and wait inside the real orchestrator job, and the 55 minute limit of one orchestration.
  `workflow_run` is not used: it does not fire for runs started with the workflow token.
- [ ] `runtime-import` accepted the agent spec (the BA result looks sane, no frontmatter garbage in the prompt).

## 7. Stage C: through the pipeline

- [ ] BA result comment posted by the bot; labels follow `planning`.
- [ ] If the BA asked questions, answer in a comment, add the label `clarified`, and check the flow resumes and
      the engine removes `clarified`.
- [ ] Planner opens a draft plan PR; label `spec-review`.
- [ ] Approve: add `ready-for-dev` as a **different** human with write access (or use self-approval for the
      sandbox). Check an approval by someone without write access is **refused** with a comment.
- [ ] The Coder produces commits only inside the allowed paths; the scope gate fails a change outside them.
- [ ] Reviewer runs; either it approves, or findings send the work back to the Planner and approval is needed again.
- [ ] You review and merge the PR yourself. The engine never merges.

## 8. Idempotency checks

- [ ] Re-run the failed or finished `forge` intake run from the Actions UI: no duplicate branch, PR or comment.
- [ ] Remove and re-add the label `forge` on a request in progress: the engine handles it without a second PR.
- [ ] On a blocked request, add `retry`: it continues from the stage that stopped, and the engine removes `retry`.

## 9. Record the results

For every item above, write **Verified**, **Failed (why)** or **Workaround**:

- [ ] Update the "Not yet verified" list in [ADR-0009](decisions/0009-github-agentic-workflows.md).
- [ ] Update section 8 of the [intake spec](intake-spec.md) and the open points of
      [ADR-0015](decisions/0015-reusable-workflow-intake.md); turn each **To verify** into a fact or a decision.
- [ ] Update the status in the [roadmap](roadmap.md) and AGENTS.md (it currently says nothing has run).
- [ ] Remove `FORGE_ALLOW_SELF_APPROVAL` if you set it, and revoke the sandbox tokens if you do not keep them.

## If something fails

| Symptom | First thing to check |
| --- | --- |
| Target `forge` run fails at `uses:` | Engine access setting (step 4), the pin exists, repository is spelled as in the template |
| Intake checkout fails | Dispatch token cannot read the engine; or `ENGINE_REF` does not contain `.github/forge/intake.py` |
| Dispatch returns 403 or 404 | Dispatch token scope, repository, `Contents: write` |
| Orchestrator skipped | `FORGE_ALLOWED_TARGETS` (the only allowlist), malformed sender |
| Request `blocked` with "did not start" or "did not finish" | The orchestrator's log (`waiting for ... run`), the agent run's title (`forge-<agent>-claude OWNER/REPO#N`), the 55 minute limit |
| Agent cannot start | `CLAUDE_CODE_OAUTH_TOKEN`; the `forge-<agent>-claude` workflows are on the default branch |
