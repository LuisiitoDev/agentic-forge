# Persistence, checkpoints and recovery

Scope: durable run state behind `IRunStore`, the SQLite adapter, idempotent starts, optimistic concurrency, checkpoints
and recovery after a restart. Out of scope: distributed consensus, multi-region stores, production queue infrastructure,
a durable event/audit sink (events are still `InMemoryEventLog`; a durable `IExecutionEventSink` is a separate adapter).

## Decision: SQLite as the first adapter (ADR)

* **Context.** Runs must survive restarts and may wait days for an approval. We need atomic compare-and-set, a unique
  index for idempotency keys, and no extra infrastructure for local use.
* **Decision.** `AgenticForge.Persistence.Sqlite.SqliteRunStore` on `Microsoft.Data.Sqlite`. WAL journal and
  `synchronous=FULL`, so a save that returned survives a crash. Each write is a `BEGIN IMMEDIATE` transaction.
* **Consequences.** Several workers (threads or processes) can share one file; writers are serialized by SQLite's file
  lock, which is fine at local scale and is the explicit limit. Other stores (Postgres, a document store) implement the
  same port and must pass the same contract tests (`RunStoreContractTests`). Runtime and Domain do not reference SQLite.
* **Alternatives rejected.** JSON files (no atomic multi-record check or unique index); an embedded KV store (extra
  dependency, same guarantees); event sourcing (rejected earlier in `domain-model.md`).

## What is stored

Snapshots are stored whole as JSON (`ContractJson`, so the stored format is the published contract) with a few indexed
columns. Tables: `runs` (unique `(workflow_id, idempotency_key)` where the key is set; index on `status`), `step_runs`
(insertion order is preserved), `checkpoints` (`(run_id, sequence)`), `meta` (store schema version; a newer one is rejected).
Artifacts are stored as `ArtifactRef` values inside run and step snapshots (content stays with its owning adapter).

## Durability contract

`IRunStore.SaveAsync` returns only after the change is committed. The engine calls it after every state change and
only then emits events, returns to the caller or invokes the next step, so a transition is never acknowledged before it
is durable. A store failure surfaces to the caller; it is not swallowed.

## Concurrency control

Both snapshots carry a store-owned `Revision`: `0` = never stored. A save succeeds only if the snapshot's revision equals
the stored one (insert needs `0`); the store writes `revision + 1` and returns that snapshot, which the engine continues
from. Otherwise `RunConcurrencyException` is thrown and nothing is written. So:

* a stale writer can never overwrite newer state (for example a late "resume" after a cancel);
* two workers racing on one run: the first save wins, the second fails and must re-read;
* the engine does not retry on conflict: callers see the exception (`ClaimedElsewhere` in recovery).

## Idempotency

`StartRunCommand.IdempotencyKey` is scoped per workflow id (not per version). Starting with a key that already has a run
returns that run's current snapshot, in whatever state, and executes nothing; other fields of the repeated command
(input, version) are ignored. Racing starts are resolved by the unique index: the loser receives the winner's run
(`DuplicateIdempotencyKeyException` is handled inside `StartAsync`). Without a key every start creates a run.
Runs are never deleted, so a key is never reused.

## Checkpoints

`RunCheckpoint` is an append-only durable marker (per-run sequence assigned by the store) written after the run's save:
`StepCompleted` (step run, outcome, next step), `Waiting` (the run paused), `Recovered` (recovery took over). They carry no step
input/output. Recovery does not depend on them: step run snapshots are the source of truth, because a checkpoint written
after a state save can itself be lost to a crash. Checkpoints give operators and tools a durable, ordered view of progress.

## Recovery

`IRunRecovery` (implemented by `WorkflowOrchestrator`) is called by the host on startup (`RecoverAllAsync`) or per run.
It looks at runs in `Pending`, `Running` and `Paused`; terminal runs and runs genuinely waiting are left alone.

| Found in the store | Recovery does |
| --- | --- |
| Run `Paused`, last step run `AwaitingInput`/`AwaitingApproval` | Nothing (`NotInterrupted`). It is waiting; `ResumeAsync` works after restart. |
| Run `Pending` (created, never started) | Starts it. Nothing had executed. |
| Run `Running`, no step run for the current step | Executes the current step. |
| Last step run `Succeeded` for the current step | **Does not execute it again**: applies its recorded outcome and moves on (with the stored output as input to the next step). |
| Last step run `Pending` | Starts that attempt (it never ran). |
| Last step run `Running` (interrupted) | Marks it `Failed` with retryable `Internal/interrupted_by_restart`. If the step declares a retry policy with attempts left, a new attempt runs; otherwise the run fails. The effects of the interrupted attempt are unknown, so it is never silently repeated. |
| Last step run `Failed` or `Cancelled` but the run not updated | Applies the same retry/fail rule, or cancels the run. |
| Run `Paused` but the step is no longer waiting (a half-applied resume) | Resumes the run and continues as above. |

The first step of recovery saves the run with `RecoveryCount + 1`; that save is the **claim**. A competing worker that
read the same revision gets a conflict and reports `ClaimedElsewhere`; exactly one worker continues a run. Recovery emits
the audit event `RunRecovered` (attribute `previousStatus`) and a `Recovered` checkpoint.
If the pinned workflow version is missing from the catalog, the result is `Failed` and the run is left untouched for a later attempt.

Limits: execution of a step is at-least-once only where the author opted in with a retry policy; a tool should use its
idempotency key (`runId/stepRunId`) to deduplicate. Recovering a `Pending` run races with a live `StartAsync` that has not
started it yet; recovery should run at startup before accepting new starts.

## Tests

`tests/AgenticForge.Persistence.Sqlite.Tests`: store contract (in-memory and SQLite), restart and crash simulations on a
real database file (a crash is a store that throws after a given save), duplicate and racing starts, competing recoverers.
