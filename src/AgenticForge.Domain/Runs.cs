using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticForge.Domain;

/// <summary>Thrown when a lifecycle operation is not legal from the current status.</summary>
public sealed class InvalidLifecycleTransitionException(string entity, string from, string operation)
    : InvalidOperationException($"{entity}: cannot {operation} from status '{from}'.");

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunStatus { Pending, Running, Paused, Succeeded, Failed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepRunStatus { Pending, Running, AwaitingInput, AwaitingApproval, Succeeded, Failed, Cancelled }

/// <summary>Serializable state of a <see cref="WorkflowRun"/>. This is what a persistence adapter stores.</summary>
public sealed record WorkflowRunSnapshot(
    WorkflowRunId Id,
    WorkflowId WorkflowId,
    WorkflowVersion WorkflowVersion,
    string CorrelationId,
    WorkflowRunStatus Status,
    DateTimeOffset CreatedAt,
    JsonElement? Input = null,
    JsonElement? Output = null,
    StepId? CurrentStep = null,
    Failure? Failure = null,
    string? CancellationReason = null,
    IReadOnlyList<ArtifactRef>? Artifacts = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? EndedAt = null,
    int SchemaVersion = ContractSchema.Current);

/// <summary>
/// One execution of one workflow version. Tracks run-level lifecycle only; per-step results live in
/// <see cref="StepRun"/> records that reference this run by id. Terminal states are final.
/// </summary>
public sealed class WorkflowRun
{
    public WorkflowRunSnapshot State { get; private set; }

    private WorkflowRun(WorkflowRunSnapshot state) => State = state;

    public WorkflowRunId Id => State.Id;
    public WorkflowRunStatus Status => State.Status;
    public bool IsTerminal => Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled;

    public static WorkflowRun Create(WorkflowRunId id, WorkflowDefinition workflow, string correlationId,
        DateTimeOffset now, JsonElement? input = null)
        => new(new WorkflowRunSnapshot(id, workflow.Id, workflow.Version, correlationId, WorkflowRunStatus.Pending, now, input));

    public static WorkflowRun Rehydrate(WorkflowRunSnapshot snapshot) => new(snapshot);

    public Correlation Correlation(StepRunId? stepRunId = null)
        => new(State.Id, State.CorrelationId, stepRunId);

    public void Start(DateTimeOffset now, StepId firstStep)
    {
        Require(WorkflowRunStatus.Pending, "start");
        State = State with { Status = WorkflowRunStatus.Running, StartedAt = now, CurrentStep = firstStep };
    }

    public void MoveTo(StepId step)
    {
        Require(WorkflowRunStatus.Running, "move to next step");
        State = State with { CurrentStep = step };
    }

    public void Pause()
    {
        Require(WorkflowRunStatus.Running, "pause");
        State = State with { Status = WorkflowRunStatus.Paused };
    }

    public void Resume()
    {
        Require(WorkflowRunStatus.Paused, "resume");
        State = State with { Status = WorkflowRunStatus.Running };
    }

    public void Succeed(DateTimeOffset now, JsonElement? output = null, IReadOnlyList<ArtifactRef>? artifacts = null)
    {
        Require(WorkflowRunStatus.Running, "succeed");
        State = State with { Status = WorkflowRunStatus.Succeeded, EndedAt = now, Output = output, Artifacts = artifacts, CurrentStep = null };
    }

    /// <summary>Failing is legal from Running or Paused (e.g. an interrupted run explicitly failed).</summary>
    public void Fail(DateTimeOffset now, Failure failure)
    {
        RequireAny("fail", WorkflowRunStatus.Running, WorkflowRunStatus.Paused);
        State = State with { Status = WorkflowRunStatus.Failed, EndedAt = now, Failure = failure };
    }

    /// <summary>Cancellation is legal from any non-terminal status and is distinct from failure.</summary>
    public void Cancel(DateTimeOffset now, string? reason = null)
    {
        if (IsTerminal) throw new InvalidLifecycleTransitionException(nameof(WorkflowRun), Status.ToString(), "cancel");
        State = State with { Status = WorkflowRunStatus.Cancelled, EndedAt = now, CancellationReason = reason };
    }

    private void Require(WorkflowRunStatus expected, string op) => RequireAny(op, expected);

    private void RequireAny(string op, params WorkflowRunStatus[] allowed)
    {
        if (!allowed.Contains(Status))
            throw new InvalidLifecycleTransitionException(nameof(WorkflowRun), Status.ToString(), op);
    }
}

/// <summary>Serializable state of a <see cref="StepRun"/>.</summary>
/// <param name="Attempt">1-based attempt number; each retry is a new StepRun with the next attempt.</param>
/// <param name="Outcome">Routing outcome reported on success; matched against workflow transitions.</param>
public sealed record StepRunSnapshot(
    StepRunId Id,
    WorkflowRunId RunId,
    StepId StepId,
    int Attempt,
    StepRunStatus Status,
    JsonElement? Input = null,
    string? Outcome = null,
    JsonElement? Output = null,
    Failure? Failure = null,
    string? CancellationReason = null,
    IReadOnlyList<ArtifactRef>? Artifacts = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? EndedAt = null,
    int SchemaVersion = ContractSchema.Current,
    ApprovalDecision? Approval = null);

/// <summary>
/// One attempt at one step of one run. It references its <see cref="WorkflowRun"/> by id only, so the
/// two lifecycles can evolve, be stored, and be tested independently.
/// </summary>
public sealed class StepRun
{
    public StepRunSnapshot State { get; private set; }

    private StepRun(StepRunSnapshot state) => State = state;

    public StepRunId Id => State.Id;
    public StepRunStatus Status => State.Status;
    public bool IsTerminal => Status is StepRunStatus.Succeeded or StepRunStatus.Failed or StepRunStatus.Cancelled;

    public static StepRun Create(StepRunId id, WorkflowRunId runId, StepId stepId, int attempt = 1, JsonElement? input = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return new(new StepRunSnapshot(id, runId, stepId, attempt, StepRunStatus.Pending, input));
    }

    public static StepRun Rehydrate(StepRunSnapshot snapshot) => new(snapshot);

    public void Start(DateTimeOffset now)
    {
        RequireAny("start", StepRunStatus.Pending);
        State = State with { Status = StepRunStatus.Running, StartedAt = now };
    }

    public void AwaitInput()
    {
        RequireAny("await input", StepRunStatus.Running);
        State = State with { Status = StepRunStatus.AwaitingInput };
    }

    public void AwaitApproval()
    {
        RequireAny("await approval", StepRunStatus.Running);
        State = State with { Status = StepRunStatus.AwaitingApproval };
    }

    /// <summary>Resumes the step. An approval step must be resumed with the human's decision, which is kept for audit.</summary>
    public void Resume(ApprovalDecision? decision = null)
    {
        RequireAny("resume", StepRunStatus.AwaitingInput, StepRunStatus.AwaitingApproval);
        if (Status == StepRunStatus.AwaitingApproval && decision is null)
            throw new ArgumentException("An approval decision is required to resume an approval step.", nameof(decision));
        State = State with { Status = StepRunStatus.Running, Approval = decision ?? State.Approval };
    }

    public void Succeed(DateTimeOffset now, string outcome, JsonElement? output = null, IReadOnlyList<ArtifactRef>? artifacts = null)
    {
        RequireAny("succeed", StepRunStatus.Running);
        if (string.IsNullOrWhiteSpace(outcome)) throw new ArgumentException("Outcome is required.", nameof(outcome));
        State = State with { Status = StepRunStatus.Succeeded, EndedAt = now, Outcome = outcome, Output = output, Artifacts = artifacts };
    }

    public void Fail(DateTimeOffset now, Failure failure)
    {
        RequireAny("fail", StepRunStatus.Running, StepRunStatus.AwaitingInput, StepRunStatus.AwaitingApproval);
        State = State with { Status = StepRunStatus.Failed, EndedAt = now, Failure = failure };
    }

    public void Cancel(DateTimeOffset now, string? reason = null)
    {
        if (IsTerminal) throw new InvalidLifecycleTransitionException(nameof(StepRun), Status.ToString(), "cancel");
        State = State with { Status = StepRunStatus.Cancelled, EndedAt = now, CancellationReason = reason };
    }

    private void RequireAny(string op, params StepRunStatus[] allowed)
    {
        if (!allowed.Contains(Status))
            throw new InvalidLifecycleTransitionException(nameof(StepRun), Status.ToString(), op);
    }
}
