using System.Text.Json.Nodes;
using AgenticForge.Domain;
using Json.Schema;
using Xunit;

namespace AgenticForge.Contracts.Tests;

public class WorkflowLoaderTests
{
    private static string Dir(string sub) => Path.Combine(AppContext.BaseDirectory, "examples", "workflows", sub);
    private static string Text(string path) => File.ReadAllText(path);

    private static readonly JsonSchema Schema =
        JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", "workflow-definition.schema.json")));

    private static bool SchemaValid(string json) =>
        Schema.Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    public static IEnumerable<object[]> ValidFiles() =>
        Directory.GetFiles(Dir(""), "*.json").Select(f => new object[] { Path.GetFileName(f) });

    public static IEnumerable<object[]> InvalidFiles() =>
        Directory.GetFiles(Dir("invalid"), "*.json").Select(f => new object[] { Path.GetFileName(f) });

    [Theory, MemberData(nameof(ValidFiles))]
    public void ValidExample_PassesSchemaAndLoader(string file)
    {
        var json = Text(Path.Combine(Dir(""), file));
        Assert.True(SchemaValid(json));
        var result = WorkflowLoader.Load(json);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory, MemberData(nameof(InvalidFiles))]
    public void InvalidExample_IsRejectedBeforeExecution(string file)
    {
        var result = WorkflowLoader.Load(Text(Path.Combine(Dir("invalid"), file)));
        Assert.False(result.IsValid);
        Assert.Null(result.Definition);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("agent-without-target.json")]
    [InlineData("approval-with-target.json")]
    [InlineData("bad-retry.json")]
    [InlineData("unknown-step-kind.json")]
    [InlineData("unsupported-schema-version.json")]
    [InlineData("missing-required.json")]
    [InlineData("bad-version.json")]
    public void StructuralInvalidExample_FailsJsonSchema(string file)
        => Assert.False(SchemaValid(Text(Path.Combine(Dir("invalid"), file))));

    [Theory]
    [InlineData("missing-start.json", "Start step 'nope'")]
    [InlineData("unknown-transition-target.json", "unknown step 'ghost'")]
    [InlineData("unknown-transition-source.json", "unknown step 'ghost'")]
    [InlineData("disallowed-outcome.json", "not an allowed outcome")]
    [InlineData("unrouted-outcome.json", "has no transition")]
    [InlineData("unreachable-step.json", "'orphan' is unreachable")]
    [InlineData("duplicate-step-id.json", "Duplicate step id")]
    [InlineData("ambiguous-transition.json", "Ambiguous")]
    [InlineData("agent-without-target.json", "requires a target")]
    [InlineData("approval-with-target.json", "must not have a target")]
    [InlineData("bad-retry.json", "MaxAttempts")]
    [InlineData("unsupported-schema-version.json", "Unsupported schemaVersion 2")]
    [InlineData("missing-required.json", "Missing required property 'steps'")]
    public void InvalidExample_ReportsExpectedError(string file, string fragment)
    {
        var result = WorkflowLoader.Load(Text(Path.Combine(Dir("invalid"), file)));
        Assert.Contains(result.Errors, e => e.Contains(fragment));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("")]
    public void MalformedInput_ReturnsErrorsWithoutThrowing(string json)
        => Assert.False(WorkflowLoader.Load(json).IsValid);

    [Fact]
    public void Cycles_AreSupported_ForReviewLoops()
    {
        var def = WorkflowLoader.Load(Text(Path.Combine(Dir(""), "valid-spec-driven-delivery.json")));
        Assert.True(def.IsValid);
        Assert.Contains(def.Definition!.Transitions, t => t.From.Value == "review" && t.To?.Value == "implement");
    }

    [Fact]
    public void DefinitionContainsNoVendorOrCiSyntax()
    {
        foreach (var f in Directory.GetFiles(Dir(""), "*.json"))
        {
            var t = Text(f).ToLowerInvariant();
            Assert.DoesNotContain("github", t);
            Assert.DoesNotContain("runs-on", t);
        }
    }
}
