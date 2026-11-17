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

/// <summary>Supplies the data a paused run is waiting for. Exactly one of the two members is set.</summary>
public sealed record ResumeRunCommand(
    WorkflowRunId Run,
    JsonElement? Input = null,
    ApprovalDecision? Decision = null);

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

/// <summary>
/// Persistence port. Stores snapshots, not live objects, so any store (file, SQL, in-memory) can implement it.
/// Saves are upserts by id; concurrency control (revisions/locks) is deliberately left to the persistence issue.
/// </summary>
public interface IRunStore
{
    ValueTask SaveAsync(WorkflowRunSnapshot run, CancellationToken cancellationToken);
    ValueTask SaveAsync(StepRunSnapshot stepRun, CancellationToken cancellationToken);
    ValueTask<WorkflowRunSnapshot?> GetRunAsync(WorkflowRunId id, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<StepRunSnapshot>> GetStepRunsAsync(WorkflowRunId id, CancellationToken cancellationToken);
}
