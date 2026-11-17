using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticForge.Abstractions;
using AgenticForge.Domain;
using Xunit;

namespace AgenticForge.Contracts.Tests;

/// <summary>Every published example must deserialize and re-serialize to the same JSON. Guards compatibility.</summary>
public class JsonExampleTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", name));

    [Theory]
    [InlineData("workflow-definition.json", typeof(WorkflowDefinition))]
    [InlineData("agent-invocation-request.json", typeof(AgentInvocationRequest))]
    [InlineData("agent-invocation-result.json", typeof(AgentInvocationResult))]
    [InlineData("agent-invocation-failure.json", typeof(AgentInvocationResult))]
    [InlineData("tool-invocation-request.json", typeof(ToolInvocationRequest))]
    [InlineData("tool-invocation-result.json", typeof(ToolInvocationResult))]
    [InlineData("workflow-run.json", typeof(WorkflowRunSnapshot))]
    [InlineData("step-run.json", typeof(StepRunSnapshot))]
    public void Example_RoundTrips(string file, Type type)
    {
        var original = Read(file);
        var obj = JsonSerializer.Deserialize(original, type, ContractJson.Options)!;
        var again = JsonSerializer.Serialize(obj, type, ContractJson.Options);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(again)),
            $"{file} did not round-trip.\n{again}");
    }

    [Fact]
    public void WorkflowExample_IsValid()
    {
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(Read("workflow-definition.json"), ContractJson.Options)!;
        Assert.Empty(def.Validate());
    }

    [Fact]
    public void UnknownProperties_AreIgnored_ForForwardCompatibility()
    {
        var json = """{"kind":"Provider","code":"c","message":"m","futureField":123}""";
        var f = JsonSerializer.Deserialize<Failure>(json, ContractJson.Options)!;
        Assert.Equal("c", f.Code);
    }
}
