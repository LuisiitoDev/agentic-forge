# Domain model and public contracts (issue #5)

Sources of truth: `docs/vision.md`, `docs/scope.md`, `docs/architecture/current-state.md`,
`docs/architecture/extraction-map.md` (stage 1, "Contracts"). Code lives in `src/`; examples in
[`examples/`](examples) are exercised by tests.

## 1. Design in brief

* Three projects, one-way dependencies: `Abstractions → Domain ← Application`. `Domain` has **no** package
  or project references, so it cannot reference a vendor, SDK, or persistence technology.
* **Definition** (what should happen) is data: `WorkflowDefinition`. **Execution records** (what happened)
  are `WorkflowRun` and `StepRun`. **Work** is done by ports (`IAgent`, `IToolProvider`). **Orchestration**
  (what runs next) belongs to a future engine behind `IWorkflowOrchestrator`. No contract mixes these.
* Lifecycle objects (`WorkflowRun`, `StepRun`) are small state machines over an immutable, JSON-serializable
  `*Snapshot` record. Time is passed in (`now`), never read from a clock, to keep orchestration deterministic.
* No engine, persistence adapter, governance engine, or provider is implemented here.

## 2. Diagrams

```mermaid
classDiagram
  direction LR
  class WorkflowDefinition { id, version, start, steps, transitions }
  class StepDefinition { id, kind, target, capabilities, allowedOutcomes, retry, timeout }
  class Transition { from, outcome, to? }
  class WorkflowRun { id, workflowId, workflowVersion, status, correlationId }
  class StepRun { id, runId, stepId, attempt, status, outcome, output, failure }
  class Failure
  class ArtifactRef
  WorkflowDefinition "1" *-- "many" StepDefinition
  WorkflowDefinition "1" *-- "many" Transition
  WorkflowRun ..> WorkflowDefinition : pinned to id+version
  StepRun ..> WorkflowRun : runId (by id only)
  StepRun ..> StepDefinition : stepId
  StepRun --> Failure
  StepRun --> ArtifactRef
  WorkflowRun --> ArtifactRef
```

```mermaid
stateDiagram-v2
  direction LR
  state "WorkflowRun" as W {
    [*] --> Pending
    Pending --> Running: Start
    Running --> Paused: Pause
    Paused --> Running: Resume
    Running --> Succeeded
    Running --> Failed
    Paused --> Failed
    Pending --> Cancelled
    Running --> Cancelled
    Paused --> Cancelled
  }
```

```mermaid
stateDiagram-v2
  direction LR
  state "StepRun (one attempt)" as S {
    [*] --> Pending
    Pending --> Running: Start
    Running --> AwaitingInput
    Running --> AwaitingApproval
    AwaitingInput --> Running: Resume
    AwaitingApproval --> Running: Resume
    Running --> Succeeded: outcome
    Running --> Failed
    AwaitingInput --> Failed
    AwaitingApproval --> Failed
    Pending --> Cancelled
    Running --> Cancelled
    AwaitingInput --> Cancelled
    AwaitingApproval --> Cancelled
  }
```

Retry = a **new** `StepRun` with `attempt + 1`; a failed attempt stays as audit history. Terminal states are final.

```mermaid
sequenceDiagram
  participant Host
  participant Orch as IWorkflowOrchestrator (future engine)
  participant Agent as IAgent (adapter)
  participant Store as IRunStore
  Host->>Orch: StartRunCommand
  Orch->>Store: save WorkflowRun(Running), StepRun(Pending→Running)
  Orch->>Agent: AgentInvocationRequest (context, input)
  Agent-->>Orch: AgentInvocationResult (status, outcome, output | failure)
  Orch->>Orch: match outcome to Transition (deterministic)
  Orch->>Store: save StepRun(Succeeded), next StepRun
```

## 3. Contract placement and justification

| Contract | Project | Public? | Why |
| --- | --- | --- | --- |
| Identifiers, `WorkflowVersion` | Domain | yes | Shared vocabulary in every record and example |
| `WorkflowDefinition`, `StepDefinition`, `Transition`, `RetryPolicy` | Domain | yes | Pure data; validated without any runtime |
| `WorkflowRun`, `StepRun` + snapshots | Domain | yes | Business invariants (legal transitions) with no I/O; snapshots are the persistence format |
| `Failure`, `ArtifactRef`, `Correlation`, `ApprovalDecision` | Domain | yes | Stored in run records and carried across every port |
| `IAgent`, `AgentDescriptor`, request/result, `StepExecutionContext` | Abstractions | **yes (extension point)** | Implemented by adapters; the one place agent implementations plug in |
| `IToolProvider`, `ToolDescriptor`, request/result | Abstractions | **yes (extension point)** | Same, for tools |
| `IWorkflowOrchestrator`, commands | Application | yes (consumer-facing, not implemented by adapters) | Host entry point; engine internals stay internal |
| `IRunStore`, `IWorkflowCatalog` | Application | yes (extension points) | Persistence/definition-source ports; they speak in Domain snapshots, so no storage type leaks |

Why `IRunStore` is in Application rather than Abstractions: it is a driven port of the orchestrator, defined in
terms of Domain snapshots, not something agent authors implement. Abstractions is kept to what *agent and tool
authors* reference, so they do not take a dependency on orchestration.

**Deliberately not public (yet):** the scheduler/engine, retry executor, policy evaluation, event bus, and
state-transition coordinator are internal to the future engine. **Deferred ports** (no contract until a use
exists, per scope "avoid premature extension points"): LLM provider (it sits *behind* an agent implementation,
and `IAgent` already covers "typed task context in, result out"), execution provider (cancel/monitor remote
work, needed by stage 3), approval-identity/auth, notification, and event/log sinks. `ApprovalDecision` exists
as data because the run record requires it (scope principle 4).

## 4. Acceptance criteria mapping

| Criterion | How it is met |
| --- | --- |
| No vendor types | Domain has zero references; Abstractions/Application reference only Domain and BCL (`System.Text.Json.JsonElement` for opaque payloads) |
| Invocation ≠ orchestration | `IAgent` performs one unit of work and returns an `outcome`; only the orchestrator maps outcomes to `Transition`s. Agents cannot choose next steps; `AllowedOutcomes` rejects invented ones |
| WorkflowRun ≠ StepRun | Separate types, statuses, state machines, and snapshots; `StepRun` holds only `runId` |
| Explicit I/O, failure, cancellation | `Input`/`Output` as JSON; `Failure` (kind, code, retryable); `InvocationStatus` has `Cancelled` and `NeedsInput`, separate from `Failed`; `Cancel` on runs/steps is distinct from `Fail` |
| Testable without LLM/account | `tests/` use a scripted agent and echo tool; 31 tests, no network |
| Public vs internal justified | Section 3 |

## 5. Design decisions and trade-offs

1. **Snapshot + state machine instead of anemic or event-sourced models.** Easy to persist and compare in shadow
   mode (extraction map §3.1); event sourcing was judged premature.
2. **Payloads are `JsonElement`, not generics.** Keeps contracts non-generic and schema-validatable (replaces
   today's regex parsing); costs compile-time typing, which a later typed-helper layer can add.
3. **Outcome string routes transitions** (mirrors the reviewer verdict loop). Cheap and explicit; typed outcomes can
   be layered on later.
4. **Step failure fails the run in v1.** Failure transitions/compensation are not defined; `Transition` has no
   failure route. Flag for the engine issue if the Multiverse "finalize" semantics need it.
5. **Retry is data (`RetryPolicy`), executed by the engine.** Bounded by construction (`MaxAttempts ≥ 1`).
6. **Parallel fan-out/fan-in and join policy are not modeled.** Extraction map lists them as a core candidate, but
   modeling them now would be speculative; add `StepKind`/group later as a minor version change.
7. **Optimistic concurrency / locks** are left to the persistence issue; `IRunStore.SaveAsync` is an upsert.
8. **Approval is a step kind** whose `StepRun` waits in `AwaitingApproval`; the decision is supplied via
   `ResumeRunCommand`. Authorization of the approver is a host concern (deferred auth port).
9. **Ambiguity for the maintainer:** whether workflow `Version` must be strict SemVer and whether "latest"
   resolution (`StartRunCommand.Version == null`) is allowed in production. Implemented as `major.minor.patch`, pinned at start.

## 6. Examples and how to run the tests

JSON examples: [`examples/`](examples). Run `dotnet test AgenticForge.slnx`.
