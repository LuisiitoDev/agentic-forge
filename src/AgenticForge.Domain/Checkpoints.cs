using System.Text.Json.Serialization;

namespace AgenticForge.Domain;

/// <summary>Why a checkpoint was written.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CheckpointKind
{
    /// <summary>A step run finished successfully and the run was advanced past it (or completed).</summary>
    StepCompleted,
    /// <summary>The run paused to wait for input or an approval.</summary>
    Waiting,
    /// <summary>Recovery took over an interrupted run.</summary>
    Recovered,
}

/// <summary>
/// Append-only, durable marker of a point from which a run can safely continue. Checkpoints name what is already
/// done (<see cref="CompletedStepRun"/>), so recovery and operators can see that finished work must not be repeated.
/// They never carry step input or output; those live in the step run snapshots.
/// </summary>
/// <param name="Sequence">Position within the run, from 1. Assigned by the store on append (0 before append).</param>
/// <param name="CompletedStepRun">The step run that completed, or the one now waiting, where applicable.</param>
/// <param name="NextStep">Where execution continues; null when the run completed.</param>
/// <param name="RunRevision">Revision of the run snapshot that was current when the checkpoint was taken.</param>
public sealed record RunCheckpoint(
    WorkflowRunId RunId,
    long Sequence,
    CheckpointKind Kind,
    DateTimeOffset CreatedAt,
    StepId? Step = null,
    StepRunId? CompletedStepRun = null,
    string? Outcome = null,
    StepId? NextStep = null,
    int RunRevision = 0,
    IReadOnlyList<ArtifactRef>? Artifacts = null,
    int SchemaVersion = ContractSchema.Current);
