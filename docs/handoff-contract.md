# Handoff contract: remaining work

Contract for whoever continues this project in a new session (a person or an AI agent). It states what is done,
what is left, the rules to follow and how to prove each item is finished. Read [AGENTS.md](../AGENTS.md) first;
this file does not repeat its invariants.

**Snapshot:** 2026-11-19, branch `main` after PR #37, plus the removal of the relay (ADR-0016).

## 1. State

**Done and tested without GitHub** (`.github/forge/tests/run.sh`: 18, 46, 82 and 13 checks; `gh aw compile`: 5
workflows, 0 warnings):

| Piece | Where |
| --- | --- |
| Agent specs (BA, Planner, Coder, Infra, Reviewer) | `.github/forge/agents/` |
| Agent workflows (Claude, `gh aw`) and shared context | `.github/workflows/forge-*.md`, `.github/forge/runtime/` |
| Orchestrator (deterministic) | `.github/workflows/forge-orchestrator.yml`, `.github/forge/orchestrate.sh` |
| Intake (spec, logic, workflow, caller template, allowlist) | `docs/intake-spec.md`, `.github/forge/intake.sh`, `.github/workflows/forge-intake.yml`, `.github/forge/templates/forge-caller.yml`, `.github/forge/targets.txt` |
| App manifest (no webhook), setup guide, first-run checklist | `.github/forge/app-manifest.json`, `docs/github-app-setup.md`, `docs/first-run-checklist.md` |

**Decided** (ADRs 0001 to 0012, 0015, 0016): central engine on Actions, intake through a reusable workflow that
targets call, no relay, the App only supplies tokens, humans approve, agents have no GitHub access.
**Proposed, not decided:** ADR-0004 (runtime service), ADR-0006 (target access and config), ADR-0013 (MCP
servers), ADR-0014 (Jira and Azure DevOps sources).

**Not done, and never run:** nothing has executed against real GitHub. There is no GitHub App, no dispatch
token and no onboarded target. Every statement about GitHub behaviour is **To verify**.

## 2. Rules for the next session

1. Documentation and specs come first. Write or update the spec or ADR, then the tests, then the code.
2. A decision or a reversal is a new numbered ADR in `docs/decisions/` plus a row in the `docs/README.md` status
   table. Do not rewrite an accepted ADR; mark it superseded.
3. Never state a third-party fact (GitHub, `gh aw`, Claude Code, Jira) as true without checking it. Write
   **To verify**, and turn it into a fact only after a real run.
4. Edit the `.md` source of an agent workflow, never its `.lock.yml`. Run `gh aw compile` and commit both.
5. Work on a branch; commit and push only when the user asks. Never commit secrets.
6. Do not reintroduce a relay, a hosted service or a webhook without a new ADR (see ADR-0016).
7. Before saying a change is done: `.github/forge/tests/run.sh` passes and `gh aw compile` reports 0 warnings.
   Say plainly that nothing has run end to end until it has.
8. Documentation is English, Markdown, Mermaid for diagrams, relative links that resolve.

## 3. Work items

Do them in order. Each has an acceptance test; an item is not done until it passes.

### W1. First real run (needs the user; accounts, secrets and a throwaway repository)

Follow [first-run-checklist.md](first-run-checklist.md).

- **Acceptance:** labeling an issue `forge` in a throwaway target ends in a reviewed pull request there, on Claude,
  with a human approving the plan.
- **Also required:** every **To verify** touched by the run is rewritten as a fact, a decision or a workaround in
  the places the checklist names (ADR-0009, the intake spec section 8, ADR-0015).
- **Highest risk, record first:** whether `workflow_run` fires for runs started by `workflow_dispatch` with the
  workflow token, and whether `display_title` carries the `run-name`. If not, the orchestrator cannot advance;
  that needs a design change and an ADR.

### W2. Fix what W1 breaks

- **Acceptance:** each failure has a failing test first when it can be reproduced without GitHub (extend
  `intake.test.sh`, `flow.test.sh` or `workflow.test.sh`), then a fix, then the real run passes.
- Likely candidates: the allowlist file read at the pinned ref, the dispatch token scope, the engine access
  setting for a private repository, the `engine-ref` that must equal the caller's pin, App permissions.

### W3. Phase 1 exit and hardening

- **Acceptance:** the Phase 1 exit in [roadmap.md](roadmap.md) is met; re-running a stage creates no duplicate
  branch, PR or comment; a request from someone without write access, and an approval by the requester (policy
  default), are refused with a comment; a change outside the allowed paths fails the scope gate.
- Update AGENTS.md and the roadmap so they say what is verified.

### W4. Phase 2: per-target configuration (`.forge/config.yml`)

- Spec first, then tests that prove "can only narrow" (a target config can never widen limits or allowed paths),
  then code. Accept or revise ADR-0006 first, since it is still Proposed.
- **Acceptance:** a second, different repository is onboarded by following [github-app-setup.md](github-app-setup.md)
  without touching engine code.

### W5. Phase 3 and 4: Codex and Copilot runners

- For each: verify non-interactive mode, permission model and result extraction against the tool itself; fill the
  capability table; refuse agents whose capabilities cannot be enforced ([runner-contract.md](runner-contract.md)).
- **Acceptance:** a request runs on the provider end to end, or the documented limits say which agents it can run.

### W6. Later, only if the user asks

- ADR-0013 (read-only MCP servers for agents) and ADR-0014 (Jira and Azure DevOps request sources): resolve their
  open points, accept or reject, then build. They depend on W1 to W3.
- Phase 5: operations (quotas, secret rotation, troubleshooting). ADR-0004 (a service) only if evidence of real cost.

## 4. Out of scope

Anything not listed above, in particular: a relay or any hosted service for intake, a web UI, multi-tenancy,
automatic model selection, merging or approving on behalf of a human, and repositories outside the allowlist.

## 5. How to verify

```bash
.github/forge/tests/run.sh      # orchestrator, intake and workflow checks (needs jq, python3 with PyYAML)
gh aw compile                   # 0 warnings; commit the .lock.yml files
```

Links check: every relative link in `*.md` must resolve to an existing file.

## 6. Known gaps and cautions

- Two allowlists must stay equal: `FORGE_ALLOWED_TARGETS` (orchestrator, authoritative) and
  `.github/forge/targets.txt` (intake early filter, read at the ref the target pins).
- The target's caller pins the engine twice (`uses` and `engine-ref`); nothing enforces that they match.
- The engine repository is hardcoded as `LuisiitoDev/agentic-forge` in the intake workflow and the caller template.
- `FORGE_ALLOW_SELF_APPROVAL` is for a sandbox only; the product policy is no self-approval.
- Tests need PyYAML for `workflow.test.sh`; there is no CI yet. Adding CI is allowed only if the user asks.
- ADR text mentioning "the relay" (0001, 0005, 0010, 0012, 0015) is historical; read it as "the intake workflow".

## 7. Handoff protocol

At the end of a session, update this file: move finished items to "Done" with the evidence (run link or test
output), keep the snapshot date and the first unfinished item accurate, and list new **To verify** items found.
