# AGENTS.md — Operating guide for Agentic Forge

Guidance for AI agents (Claude Code, GitHub Copilot, Codex and others) working **on this repository**.

> Not to be confused with the *product's* agents (BA, Planner, Coder, Infra, Reviewer), which are specified in
> `.github/forge/agents/` and described in [docs/agent-spec.md](docs/agent-spec.md).

## 1. What this project is

A central, model-agnostic engine: a human labels an issue `forge` in a target repository, and the engine
delivers a reviewed pull request there, with humans approving the consequential steps. Start with
[README.md](README.md) and [docs/README.md](docs/README.md).

## 2. Current phase: documentation first

- **Phase 1 has started.** The agent workflows, the orchestrator and their tests exist. Beyond that, do not add
  code, workflows, scripts or project files unless the user explicitly asks for it. The relay and the GitHub
  App are not built yet.
- The earlier .NET runtime and CI pipeline were removed from this branch. They remain in the history of `main`
  and may be read for reference, never restored without being asked.
- The roadmap in [docs/roadmap.md](docs/roadmap.md) defines the order of work. Phase 0 (documentation) must be
  agreed before Phase 1 starts.

## 3. Layout

```
README.md                 Overview, diagrams and architecture (components, roles, labels, runner workflow)
AGENTS.md                 This file
.github/
  forge/agents/           Provider-neutral specs of the product's agents (the prompts)
  forge/runtime/          Shared run-time context imported by every agent workflow
  forge/orchestrate.sh    Deterministic orchestrator logic
  forge/intake.sh         Intake decision logic (docs/intake-spec.md)
  forge/tests/            Orchestrator tests (fake gh)
  workflows/forge-*.md    Agent workflows, authored for gh aw
  workflows/*.lock.yml    Compiled by gh aw. Generated: never edit by hand
  workflows/forge-orchestrator.yml   Orchestrator workflow
docs/
  README.md               Index, reading order, decision status
  vision-and-scope.md     Problem, principles, scope, open questions
  request-contract.md     What a request is and its lifecycle
  target-repositories.md  Access, onboarding, per-target config, ownership
  runner-contract.md      What a model adapter must do
  agent-spec.md           Provider-neutral agent definition
  security-and-governance.md  Threat model and controls
  roadmap.md              Phases and exit criteria
  decisions/              ADR-0001 to ADR-0014
```

## 4. Invariants (do not contradict without a new ADR)

1. **Central engine on GitHub Actions**, fed by a GitHub App and a stateless relay
   ([ADR-0001](docs/decisions/0001-actions-as-engine.md), [ADR-0005](docs/decisions/0005-request-intake.md),
   [ADR-0007](docs/decisions/0007-webhook-relay.md)).
2. **Provider-neutral core.** Provider vocabulary (CLI flags, tool names, model ids) appears only inside a
   runner ([ADR-0002](docs/decisions/0002-model-agnostic-runners.md)). Agent specs name capabilities
   (`read`, `search`, `edit`, `shell`), never provider tools ([ADR-0003](docs/decisions/0003-neutral-agent-spec.md)).
3. **Humans approve consequences.** An agent never approves, never merges and never adds `ready-for-dev`.
   Approval needs a human with write access who did not open the request, and the admin or maintain role for
   sensitive paths. The engine does not read CODEOWNERS
   ([ADR-0011](docs/decisions/0011-approval-by-write-access.md), [ADR-0012](docs/decisions/0012-approval-policy.md)).
4. **Agents have no GitHub access.** The workflow, not the agent, comments, labels, pushes and opens PRs, only
   from the agent's result and an allowlist.
5. **Request and repository content is untrusted data**, including a target's `.forge/config.yml`, which can
   only narrow limits ([ADR-0006](docs/decisions/0006-target-access-and-config.md)).
6. **Fail closed.** A runner that cannot enforce the requested capabilities refuses to run.
7. **Idempotent stages.** Webhooks can be redelivered; a repeated stage must not duplicate branches, PRs or
   comments.

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
gh aw compile                 # compile .github/workflows/forge-*.md into .lock.yml (commit both)
gh aw lint <file>             # lint a workflow
.github/forge/tests/run.sh    # orchestrator tests
```

- Edit the `.md` source of an agent workflow, never its `.lock.yml`. Recompile and commit both.
- Agent prompts live in `.github/forge/agents/`. `gh aw` only imports files inside `.github/`.
- Verify that `gh aw compile` reports 0 warnings and that the tests pass before saying a change is done.
- Nothing here has run end to end against GitHub yet. Say so instead of implying it.

## 7. Git

- Work on a branch, never directly on `main`.
- Commit or push only when the user asks.
- Never commit secrets or credentials.
