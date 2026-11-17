using AgenticForge.Domain;
using Xunit;

namespace AgenticForge.Contracts.Tests;

public class DefinitionTests
{
    [Fact]
    public void ValidDefinition_HasNoErrors() => Assert.Empty(Samples.Definition().Validate());

    [Fact]
    public void Validate_ReportsStructuralProblems()
    {
        var def = Samples.Definition() with
        {
            Start = new StepId("missing"),
            Steps = [new StepDefinition(new StepId("a"), StepKind.Agent), new StepDefinition(new StepId("a"), StepKind.Approval, "x")],
            Transitions = [new Transition(new StepId("a"), "go", new StepId("zzz")), new Transition(new StepId("a"), "go", null)],
        };
        var errors = def.Validate();
        Assert.Contains(errors, e => e.Contains("Duplicate step id"));
        Assert.Contains(errors, e => e.Contains("requires a target"));
        Assert.Contains(errors, e => e.Contains("must not have a target"));
        Assert.Contains(errors, e => e.Contains("Start step"));
        Assert.Contains(errors, e => e.Contains("unknown step 'zzz'"));
        Assert.Contains(errors, e => e.Contains("Ambiguous"));
    }

    [Fact]
    public void Validate_FlagsUnreachableStepsAndDisallowedOutcomes()
    {
        var def = Samples.Definition() with
        {
            Steps = [.. Samples.Definition().Steps, new StepDefinition(new StepId("orphan"), StepKind.Tool, "t")],
        };
        Assert.Contains(def.Validate(), e => e.Contains("'orphan' is unreachable"));

        var strict = Samples.Definition() with
        {
            Steps = [new StepDefinition(new StepId("a"), StepKind.Agent, "scripted", AllowedOutcomes: ["other"]), new StepDefinition(new StepId("b"), StepKind.Approval)],
        };
        Assert.Contains(strict.Validate(), e => e.Contains("not an allowed outcome"));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("0.0.1")]
    public void Version_RoundTrips(string text) => Assert.Equal(text, WorkflowVersion.Parse(text).ToString());

    [Theory]
    [InlineData("1.2")]
    [InlineData("a.b.c")]
    public void Version_RejectsMalformed(string text) => Assert.Throws<FormatException>(() => WorkflowVersion.Parse(text));

    [Fact]
    public void Version_Orders() => Assert.True(new WorkflowVersion(1, 10, 0).CompareTo(new WorkflowVersion(1, 2, 9)) > 0);

    [Fact]
    public void Ids_RejectEmpty() => Assert.Throws<ArgumentException>(() => new StepId(" "));
}
