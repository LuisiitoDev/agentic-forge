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

    [Fact]
    public void Validate_RequiresTransitionForEveryAllowedOutcome()
    {
        var def = Samples.Definition() with
        {
            Steps = [new StepDefinition(new StepId("a"), StepKind.Agent, "scripted", AllowedOutcomes: ["ok", "unrouted"])],
            Start = new StepId("a"),
            Transitions = [new Transition(new StepId("a"), "ok", null)],
        };
        Assert.Contains(def.Validate(), e => e.Contains("'unrouted'") && e.Contains("has no transition"));
    }

    private static WorkflowDefinition With(StepDefinition step, params Transition[] transitions) =>
        Samples.Definition() with { Start = step.Id, Steps = [step], Transitions = transitions };

    private static readonly StepId A = new("a");

    [Fact]
    public void Validate_RejectsMalformedAllowedOutcomes()
    {
        var errors = With(new StepDefinition(A, StepKind.Agent, "t", AllowedOutcomes: ["ok", "ok", " ", " pad"]),
            new Transition(A, "ok", null)).Validate();
        Assert.Contains(errors, e => e.Contains("'ok' more than once"));
        Assert.Contains(errors, e => e.Contains("empty allowed outcome"));
        Assert.Contains(errors, e => e.Contains("' pad'") && e.Contains("whitespace"));
    }

    [Fact]
    public void Validate_RejectsInvalidAndDuplicateTransitionOutcomes()
    {
        var errors = With(new StepDefinition(A, StepKind.Agent, "t"),
            new Transition(A, "", null), new Transition(A, " x", null),
            new Transition(A, "dup", null), new Transition(A, "dup", A)).Validate();
        Assert.Contains(errors, e => e.Contains("empty outcome"));
        Assert.Contains(errors, e => e.Contains("' x'") && e.Contains("whitespace"));
        Assert.Single(errors, e => e.Contains("Ambiguous"));
    }

    [Fact]
    public void Validate_OutcomesAreCaseSensitive()
    {
        var errors = With(new StepDefinition(A, StepKind.Agent, "t", AllowedOutcomes: ["ok"]), new Transition(A, "OK", null)).Validate();
        Assert.Contains(errors, e => e.Contains("not an allowed outcome"));
        Assert.Contains(errors, e => e.Contains("has no transition"));
    }

    [Fact]
    public void Validate_StepKindFieldRules()
    {
        Assert.Contains(With(new StepDefinition(A, StepKind.Tool), new Transition(A, "ok", null)).Validate(), e => e.Contains("requires a target"));
        Assert.Contains(With(new StepDefinition(A, StepKind.Agent, " "), new Transition(A, "ok", null)).Validate(), e => e.Contains("requires a target"));
        Assert.Contains(With(new StepDefinition(A, StepKind.Approval, "x"), new Transition(A, "ok", null)).Validate(), e => e.Contains("must not have a target"));
        Assert.Contains(With(new StepDefinition(A, StepKind.Approval, Capabilities: ["write"]), new Transition(A, "ok", null)).Validate(), e => e.Contains("must not declare capabilities"));
        Assert.Contains(With(new StepDefinition(A, (StepKind)99, "t"), new Transition(A, "ok", null)).Validate(), e => e.Contains("unknown kind"));
    }

    [Fact]
    public void Validate_RejectsBadCapabilitiesRetryAndTimeout()
    {
        var errors = With(new StepDefinition(A, StepKind.Agent, "t", Capabilities: ["r", "r", ""],
            Retry: new RetryPolicy(1, TimeSpan.FromSeconds(-1)), Timeout: TimeSpan.Zero), new Transition(A, "ok", null)).Validate();
        Assert.Contains(errors, e => e.Contains("capability 'r' more than once"));
        Assert.Contains(errors, e => e.Contains("empty capability"));
        Assert.Contains(errors, e => e.Contains("Delay must not be negative"));
        Assert.Contains(errors, e => e.Contains("timeout must be positive"));
    }

    [Fact]
    public void Validate_DeliberatelyAllows_ApprovalOutcomesToolWithoutAllowedOutcomesAndCycles()
    {
        var def = Samples.Definition() with
        {
            Steps = [new StepDefinition(A, StepKind.Tool, "echo"), new StepDefinition(new StepId("b"), StepKind.Approval, AllowedOutcomes: ["approved"])],
            Start = A,
            Transitions = [new Transition(A, "ok", new StepId("b")), new Transition(new StepId("b"), "approved", A)],
        };
        Assert.Empty(def.Validate());
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
