# Observability: execution events, artifacts and audit trail

The engine emits versioned, provider-neutral `ExecutionEvent`s (`AgenticForge.Domain`) to an `IExecutionEventSink`
(`AgenticForge.Application`). History is read back through `IExecutionHistory`. `AgenticForge.Runtime` ships
`InMemoryEventLog` (both interfaces) for tests and local runs; durable audit persistence belongs to the persistence issue.
Without a sink the engine uses `NullEventSink`.

## Contract

| Field | Meaning |
|---|---|
| `schemaVersion` | Contract version (`ContractSchema.Current`). Adding kinds or optional fields is compatible. |
| `eventId`, `sequence` | Unique id; per-run order starting at 1, assigned by the sink on append. |
| `timestamp` | From the injected `TimeProvider`. |
| `kind`, `channel` | What happened; `Audit` or `Telemetry`. |
| `runId`, `correlationId`, `stepRunId`, `stepId`, `attempt` | Correlation. Step events point at the persisted step runs. |
| `workflowId`, `workflowVersion` | The pinned version; step definitions are versioned with their workflow. |
| `outcome`, `failure`, `actor` | Step outcome / approval decision; redacted failure; approver identity. |
| `artifacts` | `ArtifactRef`s: id, kind, storage `uri`, media type, `digest`. Never artifact content. |
| `attributes` | Small non-sensitive facts (`agent`/`tool` name, `status`, `reason`, `nextAttempt`). |

Kinds: `RunStarted/Paused/Resumed/Succeeded/Failed/Cancelled/Recovered`, `StepStarted/AwaitingInput/AwaitingApproval/Succeeded/Failed/Cancelled/RetryScheduled`,
`AgentInvoked/Completed`, `ToolInvoked/Completed`, `ApprovalRequested/Decided`.

## Audit vs telemetry

`ExecutionEventKinds.ChannelOf` maps each kind. **Audit** events are durable evidence (run and step results, approvals, cancellations);
a run can be reconstructed from the audit channel alone (`ExecutionTrace.RenderFromEvents`). **Telemetry** events (agent/tool call detail,
retry scheduling, input requests) may be sampled or expired. Sinks should keep audit events and may drop telemetry.

## Artifact integrity

`ArtifactRef.Digest` is `sha256:<lowercase hex>`. `ArtifactDigest.Compute` hashes content; `ArtifactDigest.Verify(ref, bytes)` checks it
(an artifact without a digest is not verifiable and fails verification). Adapters must not put credentials in `uri`.

## Data minimization and redaction

- Events never carry run input, step input/output, prompts, approval reasons or agent/tool exception text.
- Failure `message`/`details`, `actor` and `attributes` pass through `EventRedactor` before reaching the sink: bearer/basic credentials,
  well-known token shapes (GitHub, `sk-`, AWS, Slack), and `key=value` / `"key": "value"` pairs whose key names a secret are replaced by `[REDACTED]`;
  attribute values under secret-looking names are masked entirely; text is truncated to 500 characters.
- Redaction is best-effort pattern matching, a second line of defence. Agents and tools must still not put secrets in failures.
- A sink that throws never changes a run's result (events are recorded after state is saved).

## Example execution history

Two-step run `a -> b` (`ok`, `done`), audit (A) and telemetry (T) events in sequence order:

```
 1 A RunStarted       run-1 wf@1.0.0
 2 A StepStarted      step-2 a attempt=1
 3 T AgentInvoked     agent=agent-a
 4 T AgentCompleted   agent=agent-a status=Succeeded outcome=ok
 5 A StepSucceeded    step-2 a outcome=ok
 6 A StepStarted      step-3 b attempt=1
 7 T AgentInvoked     agent=agent-b
 8 T AgentCompleted   agent=agent-b status=Succeeded outcome=done
 9 A StepSucceeded    step-3 b outcome=done
10 A RunSucceeded     outcome=done
```

All ten share the run's `correlationId`. Out of scope: dashboards, analytics warehouse, distributed tracing backend.
