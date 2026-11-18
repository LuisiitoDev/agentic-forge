# Sample: provider-neutral software engineering workflow

A runnable reference that exercises the whole runtime: **Planner -> Approval -> Implementer -> Reviewer**, with a bounded
rework loop, human escalation, durable state and an auditable history. It runs on fake adapters through the same public
contracts an external consumer uses. It needs no GitHub Actions, no GitHub API and no model account.

* Definition: [`samples/EngineeringWorkflow/engineering-workflow.json`](../../samples/EngineeringWorkflow/engineering-workflow.json)
* Host: [`samples/EngineeringWorkflow/`](../../samples/EngineeringWorkflow/) (`EngineeringHost` composition root, `SampleRunner` scenario, `FakeAdapters`)
* Tests: [`tests/AgenticForge.Samples.Tests/`](../../tests/AgenticForge.Samples.Tests/)

```
dotnet run --project samples/EngineeringWorkflow                       # one rework, then pass
dotnet run --project samples/EngineeringWorkflow -- --review-rounds 0  # first review passes
dotnet run --project samples/EngineeringWorkflow -- --review-rounds 2  # rework exhausted: a human decides
```

## The workflow

```
plan --planned--> approve-plan --approved--> implement --implemented--> review --pass--> publish
                      |                                                    |
                  rejected (end)                                  changes-requested
                                                                           v
        publish <--pass-- final-review <--implemented-- rework
        publish <--approved-- escalate <--changes-requested-- final-review
                                  |
                              rejected (end)
```

| Step | Kind | Target | Capabilities |
|---|---|---|---|
| `plan` | Agent | `planner` | `read-requirements` |
| `approve-plan` | Approval | none | none |
| `implement`, `rework` | Agent | `implementer` | `read-repo`, `write-repo` |
| `review`, `final-review` | Agent | `reviewer` | `read-repo` |
| `escalate` | Approval | none | none |
| `publish` | Tool | `publish-change` | `publish-change` |

The definition names only logical agents and tools. Nothing in it refers to a vendor, a platform or a model.

## How each requirement is met

**Pause/resume at approval.** `approve-plan` pauses the run (`Paused`, step run `AwaitingApproval`). The engine computes an
`ApprovalBinding` over the run, step, workflow version, the digests of every artifact produced so far, and the step input. A
decision resumes the run only if it names that binding digest and comes from an allowed, non-automated identity
([governance.md](governance.md)). A wrong digest, an unlisted approver or an agent id is denied, audited, and leaves the run
paused. The pause is durable: the sample stops the process at the pause and resumes in a new one from SQLite.

**Bounded rework.** Rework is bounded by the *shape* of the definition, not by a counter the agent could influence. A first
review that requests changes routes to `rework`, then to `final-review`. If `final-review` still requests changes, the only
route is `escalate`, a human approval. There is no edge back to `implement`, so the graph is acyclic and no step can run
twice (a test asserts there is no cycle). `RuntimeOptions.MaxStepRuns` stays as a second guard: a test loosens the definition
into a loop and the run fails with `step_budget_exceeded`.

**Traceable transitions and artifacts.** Every step run, outcome and routing decision is stored (step runs and checkpoints),
and emitted as audit events (`ApprovalRequested`, `ApprovalDecided` with the approver, `PolicyDecided` for denials, step
start/end, `RunSucceeded`). The trace below can be rebuilt from audit events alone (`ExecutionTrace.RenderFromEvents`).
Every artifact (`plan`, `patch-r1`, `review-r1`, `patch-r2`, `review-r2`, `change-request-1`) is a reference with a content
digest, listed by step run and in the event history.

**Same public contracts as external consumers.** The host uses `IWorkflowOrchestrator`, `IAgent`, `IToolProvider`,
`IRunStore`, `IExecutionEventSink`/`IExecutionHistory`, `AdapterRegistry` and `WorkflowLoader`. It does not reference engine
internals.

## Architecture walkthrough

1. `EngineeringHost.CreateAsync` is the composition root. It registers `FakePlanner`, `FakeImplementer`, `FakeReviewer` and
   `FakePublisher` in an `AdapterRegistry` (which is the engine's agent and tool resolver), sets governance (capability
   ceiling, one allowed approver), and builds a `WorkflowOrchestrator` over the chosen `IRunStore`.
2. `StartAsync` loads and validates the definition, pins the version, persists the run, and drives steps one at a time. After
   every transition the run and step run are saved before anything is acknowledged.
3. The planner returns the `planned` outcome and a `plan` artifact. The engine checks the outcome against `allowedOutcomes`
   and the transitions, then moves to `approve-plan` and pauses.
4. `DecideAsync` reads the pending request's binding digest (what an approval UI would show) and resumes. The engine
   authorizes the decision, records who/what/when, and continues.
5. Each agent receives `{ input, steps: { <stepId>: <latest output> } }`. That is how the rework sees the reviewer's
   findings without any shared state; the fakes are stateless, so a restart cannot change their behavior.
6. `publish` is a tool step with an idempotency key (`runId/stepRunId`), so a retry or recovery does not repeat the effect.

## Execution trace

One rework, then pass (`--review-rounds 1`, the default). The run was paused after `plan` until the approval arrived.

```
run engineering-workflow@1.0.0
  step plan attempt=1 Succeeded outcome=planned
  step approve-plan attempt=1 Succeeded outcome=approved
  step implement attempt=1 Succeeded outcome=implemented
  step review attempt=1 Succeeded outcome=changes-requested
  step rework attempt=1 Succeeded outcome=implemented
  step final-review attempt=1 Succeeded outcome=pass
  step publish attempt=1 Succeeded outcome=completed
  => Succeeded
```

Rework exhausted, human approves (`--review-rounds 2`). The run paused again at `escalate`.

```
run engineering-workflow@1.0.0
  step plan attempt=1 Succeeded outcome=planned
  step approve-plan attempt=1 Succeeded outcome=approved
  step implement attempt=1 Succeeded outcome=implemented
  step review attempt=1 Succeeded outcome=changes-requested
  step rework attempt=1 Succeeded outcome=implemented
  step final-review attempt=1 Succeeded outcome=changes-requested
  step escalate attempt=1 Succeeded outcome=approved
  step publish attempt=1 Succeeded outcome=completed
  => Succeeded
```

Both traces are asserted by `Documented_execution_trace_matches_the_real_one`, so this page cannot drift from the code.

Notes: a rejected plan or rejected escalation ends the run `Succeeded` without publishing (rejection is a valid terminal
outcome in schemaVersion 1, which has no failure transitions). The in-memory event log is per process, so after the
simulated restart the *event* history starts at the resume; the run, step runs, checkpoints and artifacts are complete in
SQLite. A durable event sink is out of scope here.

## Mapping a future GitHub adapter (no workflow change)

The workflow only needs three seams, all existing ports. A GitHub adapter implements them; the definition and the host
logic do not change.

| Seam | Sample (fake) | GitHub adapter |
|---|---|---|
| Intake | `StartAsync(title, body, externalReference, idempotencyKey)` | An issue labeled for planning calls `StartAsync` with the issue title/body, `externalReference = issue URL`, `idempotencyKey = issue node id + event id`. A redelivered webhook returns the same run. |
| Human approval | `DecideAsync` with the pending binding digest | A comment, review or label by an allowed user on the issue/PR maps to `ResumeRunCommand.WithDecision`. The adapter authenticates the user and passes that identity as `Approver`, and quotes the binding digest it showed. |
| Effects | `FakePublisher` implementing `IToolProvider` for `publish-change` | A tool provider registered under the same name creates the branch and pull request through the GitHub API and returns the PR as an `ArtifactRef` (`kind: pull-request`). |
| Agents | Fake agents | Any `IAgent` (for example model-backed, see [provider-adapters.md](provider-adapters.md)) registered under `planner`, `implementer`, `reviewer`. |
| Progress | `ExecutionEventKind` events | An `IExecutionEventSink` posts status comments or check runs from audit events. |

GitHub concepts (issue numbers, labels, Actions) live only in that adapter and in its configuration; none of them is in the
definition, the engine, or this sample.
