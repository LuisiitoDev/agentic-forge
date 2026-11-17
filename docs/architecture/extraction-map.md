# Extraction map and staged migration

Classification of every relevant asset as **core candidate**, **adapter**,
**sample**, or **legacy**. Evidence for each row is in
[current-state.md](current-state.md). Terms follow `docs/scope.md`.

Definitions:

- **Core candidate** – the semantics become a Runtime contract or module.
- **Adapter** – implementation behind a port (GitHub, Claude Code, Spec Kit).
- **Sample** – Multiverse Explorer reference content kept as an example workflow.
- **Legacy** – stays operational during migration; retired once replaced.

## 1. Classification

### 1.1 Workflow semantics → core candidates

| Concept | Source path | Target core concept |
| --- | --- | --- |
| Pipeline BA → Planner → (approve) → Coder/Infra → Reviewer → replan | `agent-orchestrator.yml`, `docs/agent-pipeline.md` | `Workflow` definition with steps and transitions |
| Fan-out / fan-in on disjoint scopes | orchestrator `coder`, `infra`, `gate` | Parallel step group + join policy |
| Human gate `ready-for-dev` | orchestrator `resolve`; `.specify/workflows/speckit/workflow.yml` `type: gate` | `Approval` step (identity, decision, time, reason) |
| Reviewer verdict loop (`pass` / `changes-requested` / `blocked`) | orchestrator `verdict`, `reviewer.md` | Typed step outcome driving a transition |
| Clarification loop (`needs-clarification`, `round`, `questions`) | `ba.md`, `planner.md` | Pause-for-input state on `StepRun` |
| Result envelope (`status`, `verdict`, `branch`, `pr`, `artifacts`) | agent `Output` sections | `StepRun` result + `Artifact` references, schema-validated |
| Allowlisted transitions (agent cannot self-approve) | `.github/scripts/apply-result.py` | Policy: permitted outcomes per step |
| Per-agent write-scope and tool limits | wrapper `commit_paths`, `allowed_tools` | Capability/policy declaration on a step |
| Always-run finalize to a human-actionable state | orchestrator `finalize` | Run-level failure/escalation outcome |
| Attempt scoping | `started_at` filter | Attempt id on `StepRun` |
| Untrusted-input rule | `agent-run.yml` "Runtime rules" | Core security principle / input trust tagging |

### 1.2 Adapters

| Path | Adapter for | Notes |
| --- | --- | --- |
| `.github/workflows/agent-run.yml` | Execution provider (Claude Code CLI) + GitHub platform integration | Split: issue-data gathering and comment/label writing → GitHub platform adapter; `claude -p --agent` → agent/execution adapter |
| `.github/workflows/{ba,planner}.yml` triggers, `authorize` jobs | GitHub trigger + authorization | Maps `labeled` events and collaborator permission to "start run" / "approval" |
| `.github/workflows/agent-orchestrator.yml` `github-script` blocks | GitHub state/persistence | Labels and comments as a (weak) persistence provider |
| `.github/scripts/apply-result.py` | GitHub label tool | Policy half is core; `gh` calls are adapter |
| `.claude/agents/*.md` (format/frontmatter) | Agent implementation | The `.claude/agents` format is Claude-specific |
| `.specify/workflows/speckit/workflow.yml`, `.specify/extensions/`, `.specify/templates/`, `specs/` convention | Methodology adapter (Spec Kit) | Planner output format and `tasks.md` backlog |
| Secrets `CLAUDE_CODE_OAUTH_TOKEN`, `BA_ISSUE_TOKEN` | Platform integration config | Host config, never core |

### 1.3 Samples (Multiverse Explorer reference implementation)

| Path | Why sample |
| --- | --- |
| Prompt bodies of `.claude/agents/{ba,planner,coder,infra,reviewer}.md` | Stack-specific instructions (React/.NET/Bicep) |
| `.specify/memory/constitution.md` | Describes the Multiverse application |
| Wrapper settings in `coder.yml`, `infra-agent.yml`, `reviewer.yml` (`setup_*`, `commit_paths`, gates) | Toolchain and path choices |
| `.github/ISSUE_TEMPLATE/*.yml`, `.github/pull_request_template.md` | Input forms for this pipeline |
| `docs/agent-pipeline.md` | Operator guide for the GitHub deployment |
| `.specify/extensions/bug/` | Bug-triage command set for the sample |

### 1.4 Legacy (kept running during migration, retired later)

| Path | Retirement condition |
| --- | --- |
| `.github/workflows/agent-orchestrator.yml` | Replaced when the Runtime executes the same workflow with GitHub adapters and parity is demonstrated |
| `.github/workflows/agent-run.yml` | Same; becomes a thin call into the Runtime |
| Regex parsing of `status:`/`verdict:` | Replaced by schema-validated results |
| Label-as-state | Replaced by persisted run state; labels become a projection |
| `.specify/scripts/powershell/*.ps1`, `.specify/integration.json` (copilot/ps) | Not used by the runner (Claude); revisit when Spec Kit adapter is defined |

### 1.5 Coverage check

All 5 agents, 7 workflows, 1 script, issue/PR templates, docs, and `.specify`
trees are classified above. Items needing a decision: `.specify/integration.json`
(`copilot`) vs. the runner (`claude`) is an unresolved inconsistency; owner
should confirm whether the Copilot integration is in use.

## 2. GitHub mechanisms vs. workflow semantics

| Semantic (core) | GitHub mechanism today (adapter) | Portable replacement |
| --- | --- | --- |
| Start a run | `issues.labeled` event | `StartRun(workflow, inputs)` command |
| Run identity | issue number + branch `planning/issue-<n>` | `WorkflowRun.id`; external reference as metadata |
| Step state | label set | `StepRun.status` |
| Step result/handoff | marker comment + YAML | `StepRun.result` (typed, validated) |
| Approval | label by user with write role | `Approval` record with identity from an auth port |
| Pause for input | `needs-clarification` + comment reply | `Paused(awaiting-input)` + `Resume(input)` |
| Mutual exclusion | Actions `concurrency` | Per-run lock in persistence port |
| Artifact | file on branch / PR | `Artifact{id, kind, ref}` |
| Notification to humans | issue comment | Event → notification adapter |
| Result authenticity | bot login check | Results written by the runtime, not parsed from user-visible text |

## 3. Staged migration (no big-bang)

Principle: every stage leaves the Multiverse pipeline working. Each is
independently shippable and reversible.

| Stage | Change | Multiverse impact | Exit criterion |
| --- | --- | --- | --- |
| 0. Baseline | Freeze current behavior: document label/comment contracts (this audit) and capture one real run's comments as fixtures | None | Fixtures committed |
| 1. Contracts | Define `Workflow`, `WorkflowRun`, `StepRun`, `Approval`, `Artifact`, result schema in the core, as library code with no GitHub reference. Express the Multiverse pipeline as a workflow definition (data) | None; unused | Definition validates; fixtures parse against the schema |
| 2. Shadow mode | Runtime consumes recorded GitHub events/comments (fixtures, then live read-only) and computes the state/transition the orchestrator would take; compare with actual | None (read-only) | Parity on recorded runs, divergences explained |
| 3. Adapters | Implement GitHub platform adapter (comments, labels, PRs, authz) and Claude Code execution adapter by extracting from `agent-run.yml`; keep the workflows calling a small script | Workflows unchanged in behavior | Same issue yields same labels/comments/PR |
| 4. Persistence and policy | Add durable run state, idempotency keys, policy allowlist (from `apply-result.py` and `commit_paths`); labels written as a projection of state | Additive; labels still present for humans | Re-run of a step is idempotent; crashed run resumes |
| 5. Cut over per step | Replace one job at a time (start with Reviewer verdict, then gate, then orchestrator `resolve`) with Runtime calls; feature-flag per issue label or repo variable | Rollback = flag off | Each step cut over with parity and no manual intervention |
| 6. Retire legacy | Remove replaced workflow logic; keep Multiverse content as a sample workflow under `samples/` | Pipeline runs on the Runtime | Legacy list in §1.4 empty |

Dependencies between stages: 1 before 2 and 3; 3 before 5; 4 before 5 for any
step that must resume. Stage 2 can start as soon as stage 1 defines the schema.

## 4. Risks and migration dependencies

| Risk / dependency | Mitigation |
| --- | --- |
| Hidden behavior in `github-script` blocks (permission checks, branch-suffix handling, PR resolution) is not documented elsewhere | Stage 0 fixtures; port each rule with a test before cutover |
| Parity cannot be proven without live runs (`docs/agent-pipeline.md` Validation) | Shadow mode (stage 2) plus replay of recorded comments |
| GitHub bot-event rule forces PAT use | Keep PAT in the adapter; core must not assume it |
| Agent prompts embed stack content and GitHub assumptions | Separate prompt templates from stack parameters in stage 3; fix `planner.md` GitHub-tools wording |
| Spec Kit coupling (`tasks.md` as queue) | Treat as methodology adapter; core backlog is an explicit step output |
| Unbounded replan loop | Introduce an optional max-rounds policy; the human approval gate stays the default brake |
| Scope creep into Framework/Platform | Each stage must satisfy `docs/scope.md` ("Boundary for subsequent work") |
| Open question: language/runtime for the core is not decided | Decide before stage 1; not in this audit's scope |
| `.specify/integration.json` says `copilot`, runner uses Claude | Owner decision recorded in §1.5 |

## 5. Dependencies

Depends on the vision and scope (`docs/vision.md`, `docs/scope.md`, merged in
PR #13). Follow-up issues should be created per stage in §3.
