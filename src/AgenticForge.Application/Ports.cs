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
