using System.Text.Json;
using AgenticForge.Abstractions;
using AgenticForge.Domain;

namespace AgenticForge.Contracts.Tests;

/// <summary>Scripted agent: proves the port is testable with no LLM or platform account.</summary>
internal sealed class ScriptedAgent(Func<AgentInvocationRequest, AgentInvocationResult> script) : IAgent
{
    public AgentDescriptor Descriptor { get; } = new(new AgentId("scripted"), "Scripted", ["read-requirements"]);
    public List<AgentInvocationRequest> Calls { get; } = [];

    public ValueTask<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        return cancellationToken.IsCancellationRequested
            ? ValueTask.FromResult(AgentInvocationResult.CancelledResult())
            : ValueTask.FromResult(script(request));
    }
}

internal sealed class EchoToolProvider : IToolProvider
{
    public IReadOnlyList<ToolDescriptor> Tools { get; } = [new(new ToolName("echo"), "Echoes arguments", ToolSideEffects.None)];

    public ValueTask<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult(ToolInvocationResult.Success(request.Arguments));
}

internal static class Samples
{
    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    public static readonly DateTimeOffset T0 = new(2026, 11, 17, 17, 0, 0, TimeSpan.Zero);

    public static WorkflowDefinition Definition() => new(
        new WorkflowId("wf"), new WorkflowVersion(1, 0, 0), "Sample", new StepId("a"),
        [new StepDefinition(new StepId("a"), StepKind.Agent, "scripted"),
         new StepDefinition(new StepId("b"), StepKind.Approval)],
        [new Transition(new StepId("a"), "ready", new StepId("b")),
         new Transition(new StepId("b"), "approved", null)]);
}
