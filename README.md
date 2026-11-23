# Agentic Forge

A **central, model-agnostic engine** for agentic development workflows. In any of your repositories you open an
issue describing what to build and label it `forge`. The engine analyses, plans, asks for human approval,
implements and reviews, then delivers a pull request in that same repository. The model doing the work is
replaceable: Claude, Codex (ChatGPT) or GitHub Copilot.

> **Status:** documentation phase. No code exists yet. See the [roadmap](docs/roadmap.md).

Full documentation lives in [docs/](docs/README.md). Decisions are recorded in the [ADRs](docs/decisions/).

# How it works

Three views of the same system: the components, one request end to end, and the request's states.

## 1. Components

```mermaid
flowchart LR
    subgraph T["Target repository (caller workflow only)"]
        I["Issue + label forge"]
        PB["Plan branch + PR"]
    end

    CALLER["Caller workflow<br/>thin, no logic"]
    APP["GitHub App<br/>scoped tokens"]

    subgraph E["Engine repository (GitHub Actions)"]
        INTAKE["Intake workflow<br/>filter, allowlist, dispatch"]
        WF["Orchestrator +<br/>agent workflows (gh aw)"]
        SPEC["Agent specs"]
        SEC["Provider secrets<br/>+ target allowlist"]
    end

    subgraph R["Runners (interchangeable)"]
        C["Claude"]
        X["Codex"]
        P["Copilot"]
    end

    I -- "issues.labeled" --> CALLER
    CALLER -- "workflow_call" --> INTAKE
    INTAKE -- "repository_dispatch<br/>ids only" --> WF
    APP -. "short-lived token" .-> WF
    WF --> SPEC
    WF --> SEC
    WF -- "prompt + capabilities" --> R
    R -- "result.md" --> WF
    WF -- "comments, labels, commits<br/>token scoped to this repo" --> T
```

## 2. One request, end to end

```mermaid
sequenceDiagram
    autonumber
    actor H as Human requester
    participant T as Target repo
    participant C as Caller workflow
    participant R as Intake workflow
    participant E as Engine (Actions)
    participant M as Agent workflow and model
    actor O as Approver

    H->>T: Open issue, add label forge
    T->>C: issues.labeled event
    C->>R: workflow_call
    R->>R: Ignore bot events, validate fields, check allowlist
    R->>E: repository_dispatch (target, issue, sender, delivery id)
    E->>E: Check sender has write access
    E->>T: Re-read issue with scoped token
    E->>M: BA prompt, capabilities read and search
    M-->>E: Result: questions or complete requirements
    E->>T: Result comment on the issue, label planning

    E->>M: Planner prompt
    M-->>E: Spec, plan, tasks
    E->>T: Push plan branch, open draft PR, plan result on the issue, label spec-review

    O->>T: Review plan, add label ready-for-dev
    T->>C: issues.labeled event
    C->>R: workflow_call
    R->>E: repository_dispatch
    E->>E: Human? Write access? Not the requester? Sensitive paths need admin or maintain
    alt Approval refused
        E->>T: Remove label, comment the reason
    else Approval accepted
        E->>M: Coder prompt, capabilities read search edit shell
        M-->>E: Changes pushed to the plan branch, result posted on the PR
        opt The plan has infra tasks
            E->>M: Infra prompt
            M-->>E: Changes pushed to the plan branch, result posted on the PR
        end
        E->>E: Scope gate: every changed file is inside the paths the target allows
        E->>M: Reviewer prompt
        M-->>E: Verdict, posted on the PR
        alt pass
            E->>T: Label needs-review
            O->>T: Merge the PR
        else changes-requested
            E->>T: Comment findings, Planner replans, back to spec-review
        end
    end
```

## 3. Request states

```mermaid
stateDiagram-v2
    [*] --> Analysing: human adds forge
    Analysing --> NeedsClarification: questions
    NeedsClarification --> Analysing: requester replies
    Analysing --> Planning: requirements complete
    Planning --> AwaitingApproval: plan branch and draft PR
    AwaitingApproval --> Implementing: human approves, ready-for-dev
    Implementing --> Reviewing: Coder and Infra done, scope gate passed
    Reviewing --> Delivered: pass, human merges
    Reviewing --> Planning: changes-requested, replan
    Analysing --> Blocked
    Planning --> Blocked
    Implementing --> Blocked
    Reviewing --> Blocked
    Blocked --> Analysing: human resolves, re-adds label
    Delivered --> [*]
```

## What each side may do

| | Human | Agent | Engine workflow |
|---|---|---|---|
| Start a request | Yes (write access) | No | Verifies |
| Approve implementation | Yes (write access, not the requester, admin or maintain for sensitive paths) | **Never** | Verifies, can refuse |
| Read the issue and repo | Yes | Only data it is handed | Yes, with a scoped token |
| Comment, label, push, open PR | Yes | **No GitHub access** | Yes, only from the agent's result and an allowlist |
| Merge | Yes | **Never** | **Never** |

# Architecture

The engine lives in **one central repository** and runs on GitHub Actions. A request is an issue in a **target
repository**; its state is that issue's labels and result comments. Work is delivered as a pull request in the
same target. The diagrams above show the flow; this section defines the parts.

## Building blocks

| Component | Responsibility |
|---|---|
| **GitHub App** | Installed on targets; supplies short-lived, repo-scoped tokens |
| **Caller workflow** | A thin file in each target that passes the event to the engine ([ADR-0015](docs/decisions/0015-reusable-workflow-intake.md)) |
| **Intake workflow** | In the engine: filters, checks the allowlist and dispatches ([ADR-0015](docs/decisions/0015-reusable-workflow-intake.md)) |
| **Engine repo** | Workflows, agent specs, provider secrets, the allowlist |
| **Target repo** | The issue, the plan branch, the PR. Contains only the caller workflow, no pipeline logic |

## Roles

| Agent | Starts on | Produces |
|---|---|---|
| **BA** | Label `forge` | Clarifying questions, then complete requirements |
| **Planner** | Label `planning` | Spec, plan and tasks on a plan branch in the target, plus a draft PR |
| **Coder** | Orchestrator, after approval | Implementation of application tasks |
| **Infra** | Orchestrator, after the Coder | Implementation of infrastructure tasks. Runs after the Coder, never in parallel, because both push to the same plan branch |
| **Reviewer** | Orchestrator | Verdict: `pass`, `changes-requested` or `blocked`. Never approves or merges |

The loop ends only when the Reviewer passes and a human merges, or when a human stops approving another pass.
There is no iteration counter; the human gate is the brake.

## Labels

Labels live on the request issue, in the target.

| Label | Meaning | Added by |
|---|---|---|
| `forge` | Creates the request and starts BA | Human |
| `forge:claude`, `forge:codex`, `forge:copilot` | Choose the provider | Human |
| `needs-clarification` | An agent needs input from the requester | Engine |
| `planning` | The Planner is working, or replanning after review | Engine |
| `spec-review` | Plan awaits human review | Engine |
| `ready-for-dev` | **Human approval.** Starts implementation | **Human only** |
| `needs-review` | Reviewer passed; a human decides the merge | Engine |
| `blocked` | An agent could not proceed | Engine |

`ready-for-dev` is honoured only when added by a human with write access ([ADR-0011](docs/decisions/0011-approval-by-write-access.md)),
who did not open the request, and who has the admin or maintain role if the plan touches a sensitive path
([ADR-0012](docs/decisions/0012-approval-policy.md)). Events caused by the App's own bot never count as approval (see [security and governance](docs/security-and-governance.md)). If
the approval is refused, the engine removes the label and comments with the reason.

## Workflows

The engine is made of two kinds of workflow in this repository. See [ADR-0009](docs/decisions/0009-github-agentic-workflows.md).

**Agent workflows** (`.github/workflows/forge-<agent>.md`, compiled by `gh aw` into `.lock.yml`) run one agent
each. The source is Markdown with frontmatter; the prompt is the neutral spec from
`.github/forge/agents/<agent>.md`, imported at run time. Every one takes `target`, `issue` and `thread` as inputs, so the
repository, the request and where it posts are fixed by the dispatch, never chosen by the agent. Each run:

1. Checks out the target at `target/` using a short-lived, repo-scoped App token.
2. Runs the agent **read-only**. It reads the issue and the repository; it has no write access to GitHub.
3. Takes the agent's output as **safe outputs** (a comment, a draft PR, a push to the plan branch). A separate
   detection step checks them and a separate job applies them, with the App token.

**Orchestrator** (`.github/workflows/forge-orchestrator.yml` and `.github/forge/orchestrate.sh`) is plain,
deterministic Actions code. No model runs in it. It:

1. Validates the event and checks the target against the allowlist **before** any credential exists.
2. For a human event: checks the sender is a human with write access, then starts BA, approves, or resumes.
3. For a finished agent: reads its result and applies the transition below. It sets the labels. Agents never do.

| Agent result | Orchestrator action |
|---|---|
| `ba` `complete` | Label `planning`, run the Planner |
| `ba` or `planner` `needs-clarification` | Label `needs-clarification`; a human reply resumes that agent |
| `planner` `ready-for-review` | Require the plan PR, label `spec-review`, clear any earlier approval |
| `ready-for-dev` by an authorized human | Run the Coder (or Infra if there are no app tasks) |
| `coder` `ready-for-review` | Run Infra if the plan has infra tasks, otherwise the scope gate |
| `infra` `ready-for-review` | The scope gate |
| Scope gate passed | Run the Reviewer |
| Scope gate failed | Label `blocked`, list the files outside the allowed paths |
| `reviewer` `pass` | Label `needs-review` |
| `reviewer` `changes-requested` | Clear approval, label `planning`, run the Planner on the same PR |
| any `blocked`, a failed run, or no result | Label `blocked` and comment the reason |

The **scope gate** compares every file changed on the plan branch with the paths the target declares
(`paths.app`, `paths.infra` in `.forge/config.yml`) and the plan directory. Without a config nothing is allowed.

### Configuration

| Name | Kind | Meaning |
|---|---|---|
| `FORGE_APP_ID` | variable | Client id of the GitHub App installed on targets |
| `FORGE_APP_KEY` | secret | The App's private key. The most sensitive secret in the engine |
| `FORGE_ALLOWED_TARGETS` | variable | Targets the engine may serve (`owner/name`, comma or newline separated) |
| `FORGE_BOT_LOGIN` | variable | Login of the App's bot. Only its comments are trusted as results |
| `FORGE_ALLOW_SELF_APPROVAL` | variable, optional | `true` lets whoever opened a request approve its plan. Default: off |
| `FORGE_SENSITIVE_PATHS` | variable, optional | Path prefixes (comma or newline separated). A plan that touches one needs an approver with the admin or maintain role |
| `COPILOT_GITHUB_TOKEN` | secret | For the Copilot agent workflows ([ADR-0017](docs/decisions/0017-copilot-engine-for-phase-1.md)). Token type **To verify** |

### Working on the workflows

```bash
gh aw compile                       # regenerate the .lock.yml files; commit them
gh aw lint .github/workflows/forge-orchestrator.yml
.github/forge/tests/run.sh          # orchestrator tests, no GitHub needed
```

## Result protocol

The issue is the request; the plan pull request is the working surface ([ADR-0010](docs/decisions/0010-shared-plan-pr.md)).
BA, which runs before the PR exists, and the first plan result are posted on the **issue**. Replans and the
Coder, Infra and Reviewer results are posted on the **PR**. The orchestrator reads both.

Each agent ends with a comment whose first line is `## <Agent> result: <status>` (for example `## BA result: complete`), followed by a YAML block (`status`, and
agent-specific fields such as `branch`, `pr`, `verdict`). The orchestrator reads these comments to decide what
runs next. Only comments authored by the engine's App bot (`FORGE_BOT_LOGIN`) are trusted.

## Concurrency

One orchestration per request, grouped by `target#issue`. A second event for the same request waits and never
races the first. Different requests run in parallel, each isolated.

## Idempotency and redelivery

Webhooks may be redelivered. Every stage first checks the current labels and result comments, and does nothing
if its work is already done.

## State and recovery

State is labels, result comments, the plan branch and the PR. If a run fails, a human re-adds the trigger
label. This is acceptable at the current scale and is the main limitation addressed by
[ADR-0004](docs/decisions/0004-runtime-as-future-engine.md).
