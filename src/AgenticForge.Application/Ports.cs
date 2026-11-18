using System.Text.Json;
using AgenticForge.Domain;

namespace AgenticForge.Application;

/// <summary>Command to start a run. <paramref name="Version"/> null means "latest registered version", resolved once and then pinned.</summary>
/// <param name="IdempotencyKey">Repeating a start with the same key returns the existing run.</param>
/// <param name="ExternalReference">Opaque pointer to the originating request (e.g. an issue URL); metadata only.</param>
public sealed record StartRunCommand(
    WorkflowId Workflow,
    JsonElement? Input = null,
    WorkflowVersion? Version = null,
    string? IdempotencyKey = null,
    string? ExternalReference = null);

/// <summary>
/// Supplies the data a paused run is waiting for. Exactly one of <see cref="Input"/> (for a step in
/// AwaitingInput) or <see cref="Decision"/> (for an approval step in AwaitingApproval) must be set.
/// The invariant is enforced in the constructor, which System.Text.Json also uses, so a malformed command cannot
/// be constructed or deserialized. Properties are get-only so <c>with</c> cannot bypass it.
/// </summary>
public sealed record ResumeRunCommand
{
    public ResumeRunCommand(WorkflowRunId run, JsonElement? input = null, ApprovalDecision? decision = null)
    {
        if ((input is null) == (decision is null))
            throw new ArgumentException("Exactly one of input or decision must be supplied.");
        Run = run;
        Input = input;
        Decision = decision;
    }

    public WorkflowRunId Run { get; }
    public JsonElement? Input { get; }
    public ApprovalDecision? Decision { get; }

    public static ResumeRunCommand WithInput(WorkflowRunId run, JsonElement input) => new(run, input: input);
    public static ResumeRunCommand WithDecision(WorkflowRunId run, ApprovalDecision decision) => new(run, decision: decision);
}

/// <summary>
/// Public entry point to the (future) workflow engine. This is the only orchestration surface exposed to
/// hosts; how steps are scheduled is internal. Distinct from IAgent: agents do work, the orchestrator decides what runs next.
/// </summary>
public interface IWorkflowOrchestrator
{
    ValueTask<WorkflowRunSnapshot> StartAsync(StartRunCommand command, CancellationToken cancellationToken);
    ValueTask<WorkflowRunSnapshot> ResumeAsync(ResumeRunCommand command, CancellationToken cancellationToken);
    ValueTask<WorkflowRunSnapshot> CancelAsync(WorkflowRunId run, string? reason, CancellationToken cancellationToken);
}

/// <summary>Loads workflow definitions by id and version.</summary>
public interface IWorkflowCatalog
{
    ValueTask<WorkflowDefinition?> FindAsync(WorkflowId id, WorkflowVersion? version, CancellationToken cancellationToken);
}

/// <summary>The snapshot being saved is not based on the latest stored revision: someone else changed it first.</summary>
public sealed class RunConcurrencyException(string entity, string id, int expectedRevision, int? actualRevision)
    : InvalidOperationException(
        $"{entity} '{id}' was changed concurrently (expected revision {expectedRevision}, stored {(actualRevision?.ToString() ?? "none")}).")
{
    public string Entity { get; } = entity;
    public string Id { get; } = id;
    public int ExpectedRevision { get; } = expectedRevision;
    public int? ActualRevision { get; } = actualRevision;
}

/// <summary>A run with the same idempotency key already exists for the workflow.</summary>
public sealed class DuplicateIdempotencyKeyException(WorkflowId workflow, string key, WorkflowRunId existing)
    : InvalidOperationException($"Workflow '{workflow}' already has run '{existing}' for idempotency key '{key}'.")
{
    public WorkflowId Workflow { get; } = workflow;
    public string Key { get; } = key;
    public WorkflowRunId ExistingRun { get; } = existing;
}

/// <summary>
/// Persistence port. Stores snapshots, not live objects, so any store (file, SQL, in-memory) can implement it.
/// <para>Durability: when a save returns, the change is durable; the engine acknowledges nothing before that.</para>
/// <para>Concurrency: saves are compare-and-set on <c>Revision</c>. A snapshot with revision 0 inserts a new record; any other
/// revision must equal the stored one, otherwise <see cref="RunConcurrencyException"/> is thrown and nothing is written.
/// A successful save returns the snapshot with the next revision, which the caller must continue from.</para>
/// <para>Idempotency: inserting a run whose (workflow id, idempotency key) is taken throws <see cref="DuplicateIdempotencyKeyException"/>.</para>
/// </summary>
public interface IRunStore
{
    ValueTask<WorkflowRunSnapshot> SaveAsync(WorkflowRunSnapshot run, CancellationToken cancellationToken);
    ValueTask<StepRunSnapshot> SaveAsync(StepRunSnapshot stepRun, CancellationToken cancellationToken);
    ValueTask<WorkflowRunSnapshot?> GetRunAsync(WorkflowRunId id, CancellationToken cancellationToken);
    /// <summary>Step runs of a run in order of first insertion.</summary>
    ValueTask<IReadOnlyList<StepRunSnapshot>> GetStepRunsAsync(WorkflowRunId id, CancellationToken cancellationToken);

    /// <summary>The run started with this key for this workflow, or null.</summary>
    ValueTask<WorkflowRunSnapshot?> FindRunByIdempotencyKeyAsync(WorkflowId workflow, string key, CancellationToken cancellationToken);

    /// <summary>Runs currently in any of the given statuses, oldest first. Used by recovery to find interrupted work.</summary>
    ValueTask<IReadOnlyList<WorkflowRunSnapshot>> ListRunsAsync(IReadOnlyCollection<WorkflowRunStatus> statuses, CancellationToken cancellationToken);

    /// <summary>Appends a checkpoint; the store assigns <see cref="RunCheckpoint.Sequence"/> (per run, from 1) and returns it.</summary>
    ValueTask<RunCheckpoint> AppendCheckpointAsync(RunCheckpoint checkpoint, CancellationToken cancellationToken);
    /// <summary>Checkpoints of a run in sequence order.</summary>
    ValueTask<IReadOnlyList<RunCheckpoint>> GetCheckpointsAsync(WorkflowRunId id, CancellationToken cancellationToken);
}

/// <summary>What recovery did with one run.</summary>
public enum RecoveryOutcome
{
    /// <summary>The run was interrupted and has been continued; see the snapshot for where it ended up.</summary>
    Recovered,
    /// <summary>Nothing to do: the run is terminal, or waiting for input/approval (resume it with the normal command).</summary>
    NotInterrupted,
    /// <summary>Another worker updated or claimed the run first; it was left alone.</summary>
    ClaimedElsewhere,
    /// <summary>Recovery could not proceed (for example the pinned workflow version is not in the catalog); see Detail.</summary>
    Failed,
}

public sealed record RecoveryResult(WorkflowRunId Run, RecoveryOutcome Outcome, WorkflowRunSnapshot? Snapshot = null, string? Detail = null);

/// <summary>
/// Continues runs that were interrupted by a process restart or crash. Safe to call from several workers at once:
/// the first to save wins and the rest report <see cref="RecoveryOutcome.ClaimedElsewhere"/>.
/// Finished steps are never executed again.
/// </summary>
public interface IRunRecovery
{
    ValueTask<RecoveryResult> RecoverAsync(WorkflowRunId run, CancellationToken cancellationToken);
    /// <summary>Recovers every Pending or Running run in the store. Paused runs are waiting, not interrupted, and are left alone.</summary>
    ValueTask<IReadOnlyList<RecoveryResult>> RecoverAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Receives execution events from the engine. The sink assigns <see cref="ExecutionEvent.Sequence"/> (per run, from 1) when it
/// appends. Implementations must be thread-safe and must not throw for ordinary conditions: the engine treats a failing
/// sink as best-effort and carries on, so durable audit sinks should buffer and retry internally.
/// </summary>
public interface IExecutionEventSink
{
    ValueTask AppendAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken);
}

/// <summary>Read side of execution history. Events come back in sequence order.</summary>
public interface IExecutionHistory
{
    /// <summary>Events of one run, optionally limited to a channel. Empty for an unknown run.</summary>
    ValueTask<IReadOnlyList<ExecutionEvent>> GetEventsAsync(WorkflowRunId run, EventChannel? channel, CancellationToken cancellationToken);

    /// <summary>Artifact references mentioned by the run's events, in first-seen order, de-duplicated by artifact id.</summary>
    ValueTask<IReadOnlyList<ArtifactRef>> GetArtifactsAsync(WorkflowRunId run, CancellationToken cancellationToken);
}

/// <summary>
/// Central authorization point (port). The engine calls it before every agent or tool invocation and every approval decision,
/// and acts on the answer; a provider cannot bypass it. Implementations must be deterministic, side-effect free and deny
/// when information is missing. See docs/design/governance.md.
/// </summary>
public interface IPolicyEvaluator
{
    PolicyDecision Evaluate(PolicyRequest request);
}
