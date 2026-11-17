# Current state audit

Evidence-based inventory of the repository as of commit `54aa62a`. Nothing here
proposes deleting or refactoring existing assets (issue #4 out of scope).
Vocabulary (Runtime, `WorkflowRun`, `StepRun`, Adapter, Artifact, Approval)
follows `docs/scope.md`.

## 1. Inventory

### 1.1 Agents (`.claude/agents/`)

| Agent | File | Tools | Consumes | Produces | Multiverse-specific content |
| --- | --- | --- | --- | --- | --- |
| BA | `.claude/agents/ba.md` | Read, Grep, Glob, TodoWrite, Bash | Issue body/comments, `ba:result` | `ba:result` comment; labels `analysis`→`planning` / `needs-clarification` | Intro names React/.NET/Azure; trace to issue-form fields (`.github/ISSUE_TEMPLATE/*.yml`) |
| Planner | `.claude/agents/planner.md` | Read, Grep, Glob, Edit, Write, Bash, WebFetch, WebSearch, TodoWrite | `ba:result`, `reviewer:result`, `planner:result` | `specs/<dir>/{spec,plan,tasks}.md`, `.specify/feature.json`, `.specify/bugs/<slug>/assessment.md`, `planner:result` | Stack intro; fetches issue "with the GitHub tools" (contradicts runtime rule, see §3) |
| Coder | `.claude/agents/coder.md` | Read, Grep, Glob, Edit, Write, Bash, TodoWrite | `planner:result`, `tasks.md`/assessment | Code in `frontend/`, `backend/`; PR body; `coder:result` | Paths `frontend/`, `backend/`; npm/.NET verification |
| Infra | `.claude/agents/infra.md` | same as Coder | `planner:result`, `tasks.md` | `infra/` Bicep; `infra:result` | Entire agent is Azure Bicep |
| Reviewer | `.claude/agents/reviewer.md` | Read, Grep, Glob, Bash, TodoWrite | planner/coder/infra results, PR | `reviewer:result` with `verdict: pass\|changes-requested\|blocked`, findings | Gates: tests, 80% coverage, CodeQL, smoke, Bicep |

### 1.2 Workflows (`.github/workflows/`)

| File | Trigger | Role |
| --- | --- | --- |
| `ba.yml` | `issues.labeled` = `analysis`; `issue_comment.created` while `needs-clarification` | Authorize actor, call `agent-run.yml` for BA |
| `planner.yml` | `issues.labeled` = `planning` (also called by orchestrator) | Authorize, run Planner; `plan_branch: create`, commit `specs .specify/feature.json .specify/bugs`, open draft PR |
| `agent-orchestrator.yml` | `issues.labeled` = `ready-for-dev` | Jobs `resolve` → `coder`/`infra` (parallel) → `gate` → `reviewer` → `verdict` → `replan` → `finalize`; concurrency group per issue |
| `coder.yml`, `infra-agent.yml`, `reviewer.yml` | `workflow_call` | Thin wrappers pinning tools, turns, timeouts, `commit_paths`, toolchain setup |
| `agent-run.yml` | `workflow_call` | **The de-facto agent execution adapter**: gather issue JSON, build prompt, `claude -p --agent`, post result comment, apply labels, commit, open PR |

### 1.3 Scripts and spec tooling

| Path | Role |
| --- | --- |
| `.github/scripts/apply-result.py` | Parses `added:`/`removed:`/`status:` from result text; applies allowlisted labels via `gh`; `gh pr ready` on `ready-for-review`. This is the **policy enforcement point** (allowlist excludes `ready-for-dev`) |
| `.specify/workflows/speckit/workflow.yml` | Declarative workflow: specify → gate → plan → gate → tasks → implement. Closest existing artifact to a workflow definition (steps, `gate` type, `on_reject: abort`) |
| `.specify/extensions/bug/` | Spec Kit extension: bug assess/fix/test commands |
| `.specify/templates/*.md`, `.specify/memory/constitution.md` | Spec/plan/tasks templates; Multiverse Explorer constitution |
| `.specify/scripts/powershell/*.ps1` | Spec Kit helper scripts (PowerShell only, `integration.json` sets `script: ps`) |
| `.specify/integration.json`, `integrations/speckit.manifest.json` | Spec Kit integration state (`copilot`) |

### 1.4 Docs and templates

`docs/agent-pipeline.md` (label reference, secrets, limits), `docs/vision.md`,
`docs/scope.md`, `.github/pull_request_template.md`,
`.github/ISSUE_TEMPLATE/{bug-report,feature-request,config}.yml`.

## 2. State representations today

There is **no state store**. State is spread across GitHub objects:

| State | Representation | Written by | Read by |
| --- | --- | --- | --- |
| Pipeline stage | Issue labels: `analysis`, `planning`, `spec-review`, `ready-for-dev`, `needs-clarification`, `needs-review`, `bug` | `apply-result.py`, orchestrator `github-script`, humans | Workflow `if:` guards, agents |
| Step result / handoff | Issue comment starting `<!-- <agent>:result -->` + YAML (`status`, `labels`, `branch`, `pr`, `feature_dir`, `type`, `artifacts`, `round`, `questions`, `verdict`) | `agent-run.yml` ("Post the result comment") | Orchestrator regexes, downstream agents |
| Run identity | Issue number; branch `planning/issue-<n>`; one PR labeled `planning-output` | `agent-run.yml` | Everything |
| Attempt scoping | Comment timestamp ≥ `started_at` | orchestrator `resolve` | `gate`, `verdict` |
| Work backlog | Unticked `- [ ]` lines in `specs/<dir>/tasks.md`; bugs use `assessment.md` | Planner, Coder | `resolve` |
| Approval | A human adds `ready-for-dev`; actor permission checked via REST | Human | `resolve` |
| Artifacts | Files committed to the plan branch; PR body | `agent-run.yml` commit step | Reviewer, humans |
| Events/observability | GitHub Actions logs and step summaries (`core.summary`) | Workflows | Humans |

## 3. Platform coupling (dependency map)

```
Agentic workflow semantics (to extract)
  steps / transitions / loop ──────────────┐
  approval gate (ready-for-dev) ───────────┤   currently expressed as
  result contract (status, verdict) ───────┤        │
  retry/replan loop ───────────────────────┘        ▼
                                          GitHub labels + issue comments
                                                    │ triggers
                                          GitHub Actions (issues.labeled, workflow_call, concurrency)
                                                    │ executes
                                          agent-run.yml ──► npm i @anthropic-ai/claude-code@2.1.310
                                                    │            claude -p --agent <name> --allowedTools
                                                    ├──► gh CLI + GITHUB_TOKEN / BA_ISSUE_TOKEN
                                                    └──► actions/github-script@v7 (Octokit REST/GraphQL)
Domain content (sample, not core)
  Multiverse Explorer stack: frontend/ backend/ infra/ (React, .NET, Azure Bicep)
  Spec Kit: specs/, .specify/ (copilot integration, PowerShell scripts)
```

| Coupling | Evidence | Kind |
| --- | --- | --- |
| Trigger = GitHub label event | `ba.yml`, `planner.yml`, `agent-orchestrator.yml` `on: issues: types: [labeled]` | Platform |
| Authorization = repo collaborator permission | `authorize` jobs; `getCollaboratorPermissionLevel` in orchestrator `resolve` | Platform |
| Anti-forgery of results = `user.login == 'github-actions[bot]'` | orchestrator `isPlannerResult`, `gate`, `verdict` | Platform (security-relevant) |
| Bot-triggered-workflow limitation | `docs/agent-pipeline.md` (`BA_ISSUE_TOKEN` PAT), orchestrator comment on `replan` calling Planner directly | Platform workaround |
| Mutual-exclusion = Actions `concurrency` groups | `agent-orchestrator.yml`, `ba.yml`, gate job | Platform |
| Agent runtime = Claude Code CLI, pinned version | `agent-run.yml` "Run the agent" | Provider |
| Agent identity = file in `.claude/agents/` | `--agent "$AGENT"` | Provider |
| Tool policy = Claude `--allowedTools` strings | each wrapper `allowed_tools` | Provider + policy |
| Write scope policy = `commit_paths` | wrappers, `agent-run.yml` commit step | Policy (portable concept) |
| Secrets | `CLAUDE_CODE_OAUTH_TOKEN`, `BA_ISSUE_TOKEN` | Platform/provider |
| Prompt content names GitHub tooling | `planner.md` "with the GitHub tools" vs `agent-run.yml` "You have no GitHub access" | Inconsistency |
| Toolchains | `setup_node`, `setup_dotnet`, `setup_bicep` inputs | Sample (Multiverse) |
| Domain paths | `commit_paths` `frontend backend infra docker-compose.yml`; constitution | Sample |
| Spec Kit coupling | `.specify/`, `specs/`, `planner.md`, orchestrator reads `tasks.md` / `assessment.md` | Methodology adapter |

## 4. Reusable behaviors (semantics independent of GitHub)

| Behavior | Where it lives today |
| --- | --- |
| Requirements clarification loop with numbered questions and rounds (`round`, `questions`) | `ba.md`, `planner.md` |
| Plan → human approval → implement → review → replan loop; human is the brake, not a counter | `agent-orchestrator.yml` header and jobs |
| Approval gate with identity check (write permission) | orchestrator `resolve`; `.specify/workflows/speckit/workflow.yml` `review-*` gates |
| Parallel fan-out on disjoint scopes, fan-in gate | `coder`/`infra` → `gate` |
| Structured step result (`status`, `verdict`, findings with file/line, evidence table) | agent `Output` sections |
| Allowlisted state transitions (agent cannot self-approve) | `.github/scripts/apply-result.py` |
| Least-privilege tools and write paths per agent | wrapper inputs |
| Untrusted-input handling (issue text is data) | `agent-run.yml` "Runtime rules" |
| Always-run finalize that leaves a human-actionable state | `finalize` job |
| Evidence-first review (`not-run` is a finding) | `reviewer.md` |

## 5. Missing runtime capabilities

Measured against `docs/scope.md` MVP and principles.

| Capability | Today | Gap |
| --- | --- | --- |
| Persistent run/step state | Labels + comments, reconstructed by regex | No `WorkflowRun`/`StepRun`, no schema, no version |
| Recovery / resume | Human re-adds a label; `round`/`questions` re-parsed from comments | No checkpoint; crash mid-agent loses the attempt, only the "blocked" comment remains |
| Idempotency | Commit step skips empty diffs; PR open checks existing PR | Comments are not idempotent (duplicates on re-run); label events re-fire; no run/attempt id |
| Concurrency | Per-issue Actions groups, `cancel-in-progress: false` | Queueing only; no cross-workflow lock between BA/Planner/orchestrator on one issue |
| Policy enforcement | Label allowlist script; `commit_paths`; tool allowlists | Not declarative or testable; scattered across YAML/Python; commit paths not enforced against the diff if tools write elsewhere |
| Retries | None automatic; loop is human-driven | No retry limits/backoff; no bounded-loop guard ("no iteration counter" by design) |
| Observability | Step summaries, issue comments | No structured events, correlation ids, timing, token usage |
| Result validation | Regex on markdown (`^status:\s*`) | No schema validation; malformed result → `missing` |
| Authenticity of results | Bot-login check | Comment body is the control channel; forgeable by anyone able to act as the bot |
| Approval record | Label event + actor lookup (not persisted) | Reason/decision/time not stored |
| Artifact addressing | Paths on a branch | No stable ids, hashes, or typed references |
| Testability | Only live runs (`docs/agent-pipeline.md` Validation) | Cannot run the workflow without GitHub and a model |

## 6. Risks observed

1. **Label-as-state race**: two events on one issue can interleave; correctness relies on `concurrency` and on removing labels in `finalize`.
2. **Regex contracts**: `status:`/`verdict:` parsing in YAML-in-markdown breaks silently.
3. **PAT dependence** (`BA_ISSUE_TOKEN`) is a security and operability cost caused by GitHub's bot-event rule.
4. **Pinned CLI** `@anthropic-ai/claude-code@2.1.310` is installed on every run (supply-chain surface and drift when the pin ages).
5. **Prompt/runtime drift**: agent prompts mention GitHub tools while the runner forbids them.
6. **Domain leakage**: every agent prompt and wrapper embeds Multiverse paths and toolchains.
7. **Spec Kit state mixes with runtime state**: `tasks.md` checkboxes act as the work queue.
