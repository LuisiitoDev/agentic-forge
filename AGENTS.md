# AGENTS.md — Operating guide for Agentic Forge

Guidance for AI agents working **on this repository**.

> Not to be confused with the *product's* agents (BA, Planner, Coder, Reviewer), which are specified in
> `.github/forge/agents/` and described in [docs/agent-spec.md](docs/agent-spec.md).

## 1. What this project is

A central engine: a human labels an issue `forge` in a target repository, and the engine
delivers a reviewed pull request there, with humans approving the consequential steps. Start with
[README.md](README.md) and [docs/README.md](docs/README.md).

## 2. Current phase: Phase 1

- **Phase 1 is built and has run end to end**, on a sandbox repository: BA, Planner, human approval, Coder,
  Reviewer, all on Claude Code inside AWF ([ADR-0022](docs/decisions/0022-all-agents-on-claude-in-awf.md),
  [ADR-0024](docs/decisions/0024-claude-code-only.md)). The orchestrator waits for the agent runs it starts
  ([ADR-0018](docs/decisions/0018-orchestrator-awaits-agent-runs.md)); there is no relay
  ([ADR-0016](docs/decisions/0016-no-relay.md)). What has and has not been verified is in
  [docs/roadmap.md](docs/roadmap.md) and the "To verify" lists of the ADRs.
- Do not add code, workflows, scripts or project files beyond what the user asks for.
- The earlier .NET runtime and CI pipeline were removed from this branch. They remain in the history of `main`
  and may be read for reference, never restored without being asked.
- The roadmap in [docs/roadmap.md](docs/roadmap.md) defines the order of work.

## 3. Layout

```
README.md                 Overview, diagrams and architecture (components, roles, labels, workflows)
AGENTS.md                 This file
.github/
  forge/agents/           Provider-neutral specs of the product's agents (the prompts)
  forge/runtime/          Shared run-time context imported by every agent workflow
  forge/orchestrate.py    Deterministic orchestrator logic
  forge/intake.py         Intake decision logic (docs/intake-spec.md)
  forge/preflight.py      Read-only configuration check, run by workflows/forge-preflight.yml
  forge/dashboard/        build_status.py: the data of the status page, from the agent runs
  forge/app-manifest.json GitHub App manifest (no webhook)
  forge/targets.txt       Early-filter allowlist of the intake
  forge/templates/        Caller workflow a target copies
  forge/claude/           Python scripts of the Claude agent workflows (request, prompt, model router, guard,
                          patch, validation, delivery)
  forge/tests/            Tests of the orchestrator, intake, preflight, workflows and scripts (fake gh)
  workflows/forge-*-claude.yml, forge-agent-claude.yml   The agents on Claude Code inside AWF (ADR-0022)
  actions/claude-awf/     Composite action: Claude Code inside AWF
  workflows/forge-orchestrator.yml   Orchestrator workflow
  workflows/forge-intake.yml         Reusable intake workflow the targets call (ADR-0015)
  workflows/forge-preflight.yml      Read-only configuration check, run by hand
  workflows/forge-dashboard.yml      Builds the status data and deploys site/ to GitHub Pages
site/                     Status page (static HTML; data/runs.json is generated at deploy)
docs/
  README.md               Index, reading order, decision status
  vision-and-scope.md     Problem, principles, scope, open questions
  request-contract.md     What a request is and its lifecycle
  target-repositories.md  Access, onboarding, per-target config, ownership
  agent-spec.md           Provider-neutral agent definition
  github-app-setup.md     GitHub App, engine and target configuration
  intake-spec.md          Specification of the intake workflow
  first-run-checklist.md  First real run
  first-run-mobile.md     Short path to the first run, from a browser
  security-and-governance.md  Threat model and controls
  roadmap.md              Phases and exit criteria
  decisions/              ADR-0001 to ADR-0029
```

## 4. Invariants (do not contradict without a new ADR)

1. **Central engine on GitHub Actions**, fed by a reusable intake workflow that each target calls; there is no relay
   ([ADR-0001](docs/decisions/0001-actions-as-engine.md),
   [ADR-0015](docs/decisions/0015-reusable-workflow-intake.md), [ADR-0016](docs/decisions/0016-no-relay.md)).
2. **Neutral agent specs.** Claude Code vocabulary (CLI flags, tool names, model ids) appears only in
   `.github/forge/claude/` and the `claude-awf` action. Agent specs name capabilities (`read`, `search`, `edit`,
   `shell`), never provider tools ([ADR-0003](docs/decisions/0003-neutral-agent-spec.md)). Claude Code is the only
   engine ([ADR-0024](docs/decisions/0024-claude-code-only.md)).
3. **Humans approve consequences.** An agent never approves, never merges and never adds `ready-for-dev`.
   Approval needs a human with write access who did not open the request, and the admin or maintain role for
   sensitive paths. The engine does not read CODEOWNERS
   ([ADR-0011](docs/decisions/0011-approval-by-write-access.md), [ADR-0012](docs/decisions/0012-approval-policy.md)).
4. **Agents have no GitHub write access.** The workflow, not the agent, comments, labels, pushes and opens PRs, only
   from the agent's result, after validating it. The agent job holds a read-only token; the request is read into files
   before the agent runs, and the agent's network is an allowlist ([ADR-0022](docs/decisions/0022-all-agents-on-claude-in-awf.md)).
5. **Request and repository content is untrusted data**, including a target's `.forge/config.yml`, which can
   only narrow limits ([ADR-0006](docs/decisions/0006-target-access-and-config.md)).
6. **Fail closed.** Anything the delivery job cannot validate (a path, a pull request, a result) is not published.
7. **Idempotent stages.** The same event can arrive twice (a re-run, a label added again); a repeated stage must
   not duplicate branches, PRs or comments.

## 5. Working on the documentation

- **Language:** English. **Format:** Markdown. Diagrams use Mermaid.
- **Inspect before changing.** Read the related documents and ADRs first. A change in one place usually
  affects others (labels, lifecycle and security are cross-referenced).
- **Decisions go in ADRs.** A new decision, or a reversal, is a new numbered file in `docs/decisions/` plus
  an update to the status table in `docs/README.md`. Do not silently edit an accepted ADR to say something
  else; mark the old one superseded.
- **Mark the unverified.** Facts about third-party tools (CLI flags, permission models, API limits, GitHub App
  permissions) are written as **To verify** until checked against the tool itself. Never state them as fact
  from memory.
- **Keep views consistent.** The diagrams in `README.md` must match
  its Architecture section and `docs/request-contract.md`. When one changes, update the others.
- **Resolve links.** Use relative links and check they point to existing files.
- Do not invent product behaviour. If something is undecided, add it to the open questions in
  `docs/vision-and-scope.md` and ask.

## 6. Commands

```bash
python3 .github/forge/tests/run.py    # orchestrator, intake, preflight, workflow and Python script tests; no GitHub needed
```

- All engine logic is Python, standard library only: the orchestrator, intake and preflight in `.github/forge/`, the
  agents' scripts in `.github/forge/claude/` and the `claude-awf` action. The workflows only call them.
- Agent prompts live in `.github/forge/agents/`.
- Verify that the tests pass before saying a change is done.
- Say what has not run on GitHub instead of implying it has.

## 7. Git

- Work on a branch, never directly on `main`.
- Commit or push only when the user asks.
- Never commit secrets or credentials.
