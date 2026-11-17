using System.Text.Json.Serialization;

namespace AgenticForge.Domain;

/// <summary>What a step does when executed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepKind
{
    /// <summary>Invoke an agent (see Abstractions.IAgent).</summary>
    Agent,
    /// <summary>Invoke a tool directly (see Abstractions.IToolProvider).</summary>
    Tool,
    /// <summary>Pause until a human supplies an <see cref="ApprovalDecision"/>.</summary>
    Approval,
}

/// <summary>Bounded retry policy. Unbounded retries are not representable.</summary>
public sealed record RetryPolicy(int MaxAttempts = 1, TimeSpan? Delay = null);

/// <summary>One step. <see cref="Target"/> is an agent id or tool name; it is null for approval steps.</summary>
/// <param name="Capabilities">Capability names the step is allowed to use; anything else is denied.</param>
/// <param name="AllowedOutcomes">Outcomes the step may report; others are rejected (agents cannot invent transitions).</param>
public sealed record StepDefinition(
    StepId Id,
    StepKind Kind,
    string? Target = null,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyList<string>? AllowedOutcomes = null,
    RetryPolicy? Retry = null,
    TimeSpan? Timeout = null);

/// <summary>
/// Routes a step's reported outcome to the next step. A null <see cref="To"/> completes the run
/// successfully. A step failure that exhausts retries fails the run (no failure transitions in v1).
/// </summary>
public sealed record Transition(StepId From, string Outcome, StepId? To);

/// <summary>A versioned, declarative workflow. Contains no execution logic and no vendor types.</summary>
public sealed record WorkflowDefinition(
    WorkflowId Id,
    WorkflowVersion Version,
    string Name,
    StepId Start,
    IReadOnlyList<StepDefinition> Steps,
    IReadOnlyList<Transition> Transitions,
    int SchemaVersion = ContractSchema.Current)
{
    /// <summary>Returns structural problems; an empty list means the definition is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var ids = new HashSet<StepId>();
        foreach (var s in Steps)
        {
            if (!ids.Add(s.Id)) errors.Add($"Duplicate step id '{s.Id}'.");
            if (s.Kind == StepKind.Approval && s.Target is not null)
                errors.Add($"Approval step '{s.Id}' must not have a target.");
            if (s.Kind != StepKind.Approval && string.IsNullOrWhiteSpace(s.Target))
                errors.Add($"Step '{s.Id}' of kind {s.Kind} requires a target.");
            if (s.Retry is { MaxAttempts: < 1 })
                errors.Add($"Step '{s.Id}' retry MaxAttempts must be at least 1.");
        }

        if (!ids.Contains(Start)) errors.Add($"Start step '{Start}' is not defined.");

        var routes = new HashSet<(StepId, string)>();
        foreach (var t in Transitions)
        {
            if (!ids.Contains(t.From)) errors.Add($"Transition from unknown step '{t.From}'.");
            if (t.To is { } to && !ids.Contains(to)) errors.Add($"Transition to unknown step '{to}'.");
            if (string.IsNullOrWhiteSpace(t.Outcome)) errors.Add($"Transition from '{t.From}' has an empty outcome.");
            if (!routes.Add((t.From, t.Outcome))) errors.Add($"Ambiguous transition: '{t.From}' on '{t.Outcome}' is defined twice.");
            var from = Steps.FirstOrDefault(s => s.Id == t.From);
            if (from?.AllowedOutcomes is { } allowed && !allowed.Contains(t.Outcome))
                errors.Add($"Transition outcome '{t.Outcome}' is not an allowed outcome of step '{t.From}'.");
        }

        foreach (var s in Steps)
            foreach (var outcome in s.AllowedOutcomes ?? [])
                if (!routes.Contains((s.Id, outcome)))
                    errors.Add($"Allowed outcome '{outcome}' of step '{s.Id}' has no transition.");

        if (ids.Contains(Start))
        {
            var seen = new HashSet<StepId> { Start };
            var queue = new Queue<StepId>([Start]);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var t in Transitions.Where(t => t.From == cur && t.To is not null))
                    if (seen.Add(t.To!.Value)) queue.Enqueue(t.To.Value);
            }
            foreach (var id in ids.Where(i => !seen.Contains(i)))
                errors.Add($"Step '{id}' is unreachable from the start step.");
        }

        return errors;
    }
}
