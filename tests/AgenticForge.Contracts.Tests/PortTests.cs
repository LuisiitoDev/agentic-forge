using AgenticForge.Abstractions;
using AgenticForge.Domain;
using Xunit;

namespace AgenticForge.Contracts.Tests;

public class PortTests
{
    private static StepExecutionContext Ctx() => new(new Correlation(new WorkflowRunId("r1"), "corr", new StepRunId("s1")));

    [Fact]
    public async Task Agent_CanBeInvokedWithoutAnyProvider()
    {
        var agent = new ScriptedAgent(_ => AgentInvocationResult.Success("ready", Samples.Json("""{"ok":true}""")));
        var result = await agent.InvokeAsync(new AgentInvocationRequest(agent.Descriptor.Id, Ctx(), Samples.Json("{}")), CancellationToken.None);
        Assert.Equal(InvocationStatus.Succeeded, result.Status);
        Assert.Equal("ready", result.Outcome);
        Assert.Single(agent.Calls);
    }

    [Fact]
    public async Task Agent_HonorsCancellation_AsCancelledNotFailed()
    {
        var agent = new ScriptedAgent(_ => throw new InvalidOperationException("should not run"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await agent.InvokeAsync(new AgentInvocationRequest(agent.Descriptor.Id, Ctx(), Samples.Json("{}")), cts.Token);
        Assert.Equal(InvocationStatus.Cancelled, result.Status);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void Failure_IsExplicitResult()
    {
        var r = AgentInvocationResult.Fault(new Failure(FailureKind.Validation, "bad-input", "missing field"));
        Assert.Equal(InvocationStatus.Failed, r.Status);
        Assert.Null(r.Outcome);
        Assert.Equal("bad-input", r.Failure!.Code);
    }

    [Fact]
    public async Task Tool_RoundTripsThroughPort()
    {
        IToolProvider tools = new EchoToolProvider();
        var args = Samples.Json("""{"x":1}""");
        var r = await tools.InvokeAsync(new ToolInvocationRequest(tools.Tools[0].Name, Ctx(), args), CancellationToken.None);
        Assert.Equal(InvocationStatus.Succeeded, r.Status);
        Assert.Equal(1, r.Output!.Value.GetProperty("x").GetInt32());
    }
}
