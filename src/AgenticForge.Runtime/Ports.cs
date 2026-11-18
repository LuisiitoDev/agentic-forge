using AgenticForge.Abstractions;
using AgenticForge.Domain;

namespace AgenticForge.Runtime;

/// <summary>Resolves the logical agent id named by a workflow to an implementation. Returns null if unknown.</summary>
public interface IAgentResolver
{
    IAgent? Resolve(AgentId id);
}

/// <summary>Resolves the provider that offers a tool. Returns null if no provider offers it.</summary>
public interface IToolResolver
{
    IToolProvider? Resolve(ToolName name);
}

/// <summary>Source of run and step-run ids, so traces are reproducible in tests.</summary>
public interface IIdGenerator
{
    string NewId(string prefix);
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}

/// <summary>Raised when the requested workflow (or pinned version) is not in the catalog.</summary>
public sealed class WorkflowNotFoundException(WorkflowId id, WorkflowVersion? version)
    : InvalidOperationException($"Workflow '{id}' version '{version?.ToString() ?? "latest"}' was not found.");

/// <summary>Raised when a definition fails <see cref="WorkflowDefinition.Validate"/>; the engine never runs invalid workflows.</summary>
public sealed class InvalidWorkflowException(IReadOnlyList<string> errors)
    : InvalidOperationException("Workflow definition is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Engine knobs. Outcome names are fixed by the engine for steps that have no agent to choose them.</summary>
/// <param name="MaxStepRuns">Run-level budget of StepRun records (including retries). Bounds review/rework cycles.</param>
public sealed record RuntimeOptions(int MaxStepRuns = 100)
{
    public const string ToolCompletedOutcome = "completed";
    public const string ApprovedOutcome = "approved";
    public const string RejectedOutcome = "rejected";
    public const string ChangesRequestedOutcome = "changes_requested";

    public static string OutcomeFor(ApprovalDecisionKind kind) => kind switch
    {
        ApprovalDecisionKind.Approved => ApprovedOutcome,
        ApprovalDecisionKind.Rejected => RejectedOutcome,
        ApprovalDecisionKind.ChangesRequested => ChangesRequestedOutcome,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
