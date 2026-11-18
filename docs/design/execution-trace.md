# Workflow runtime: rules and example trace

`AgenticForge.Runtime` implements `IWorkflowOrchestrator`. It depends only on Domain, Abstractions and Application:
no GitHub, hosted LLM, persistence or provider SDK. Agents and tools are reached through `IAgentResolver` / `IToolResolver`.

## Rules the engine enforces

- **The engine owns progression.** An agent reports an *outcome*; the engine checks it against the step's `allowedOutcomes`
  and the workflow's transitions. An unknown outcome, or one with no transition, fails the step with `Validation`.
  A step with no outgoing transitions is terminal and ends the run successfully.
- **Engine-defined outcomes.** Tool steps report `completed`. Approval steps report `approved`, `rejected` or `changes_requested`,
  mapped from the human's `ApprovalDecisionKind`.
- **All state changes go through `WorkflowRun` / `StepRun` methods**, so invalid transitions throw `InvalidLifecycleTransitionException`
  (e.g. cancelling or resuming a finished run).
- **Failure vs cancellation are distinct.** A step failure that exhausts retries fails the run. Cancellation (host token or `CancelAsync`)
  cancels the active step run and the run, and never sets a `Failure`. An agent cancelling itself unasked is a `Provider` failure.
- **Retry** only when `Failure.Retryable` is true and attempts < `retry.maxAttempts`. Each attempt is a new `StepRun`.
  `retry.delay` waits on the injected `TimeProvider`. A timeout is a retryable `Timeout` failure.
- **Waiting.** `NeedsInput` pauses the run; the questions are kept in the waiting step run's `Output`. Resuming with input re-invokes the
  same attempt with the answer under `input.answer`. Approval steps pause until `ResumeRunCommand.WithDecision`.
  Resuming with the wrong payload kind is rejected and the run stays paused.
- **Step input** is `{ "input": <run input>, "steps": { "<stepId>": <latest output> } }`.
- **Capabilities** are deny-by-default: every capability an agent declares must be listed on the step; a tool step must list the tool name.
- **Run budget.** `RuntimeOptions.MaxStepRuns` (default 100) bounds rework cycles; exceeding it fails the run (`step_budget_exceeded`).
- Sequential v1: one active step per run. `StartAsync`/`ResumeAsync` return when the run is terminal or paused.
- Not yet implemented: `IdempotencyKey` de-duplication, durable concurrency control, policy evaluation and events (other issues).

## Example trace

`valid-spec-driven-delivery.json` with scripted fake agents: the plan is approved by a human, and the reviewer asks for changes once.
Produced by `ExecutionTrace.Render` and asserted in `Spec_driven_delivery_example_trace`.

```
run spec-driven-delivery@1.0.0
  step analyze attempt=1 Succeeded outcome=ready
  step approve-plan attempt=1 Succeeded outcome=approved
  step implement attempt=1 Succeeded outcome=done
  step review attempt=1 Succeeded outcome=changes-requested
  step implement attempt=1 Succeeded outcome=done
  step review attempt=1 Succeeded outcome=pass
  => Succeeded
```

(The run was paused after `analyze` until the approval decision arrived.)
