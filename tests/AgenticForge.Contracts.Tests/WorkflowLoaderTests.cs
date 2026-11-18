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
    public void NullInput_ReturnsErrorWithoutThrowing()
    {
        var result = WorkflowLoader.Load(null);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void DuplicateJsonKeys_ReturnErrorWithoutThrowing()
        => Assert.False(WorkflowLoader.Load("{\"id\":\"a\",\"id\":\"b\"}").IsValid);

    private static string Minimal() => Text(Path.Combine(Dir(""), "valid-minimal.json"));

    private static string Mutate(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(Minimal())!.AsObject();
        change(root);
        return root.ToJsonString();
    }

    private static JsonObject FirstStep(JsonObject root) => root["steps"]!.AsArray()[0]!.AsObject();

    public static IEnumerable<object[]> StructuralMutations()
    {
        yield return M("remove id", r => r.Remove("id"));
        yield return M("id padded", r => r["id"] = " x");
        yield return M("id trailing newline", r => r["id"] = "x\n");
        yield return M("id number", r => r["id"] = 5);
        yield return M("name empty", r => r["name"] = "");
        yield return M("name null", r => r["name"] = null);
        yield return M("start blank", r => r["start"] = "");
        yield return M("steps empty", r => r["steps"] = new JsonArray());
        yield return M("steps object", r => r["steps"] = new JsonObject());
        yield return M("transitions string", r => r["transitions"] = "x");
        yield return M("schemaVersion 2", r => r["schemaVersion"] = 2);
        yield return M("schemaVersion string", r => r["schemaVersion"] = "1");
        yield return M("schemaVersion 1.5", r => r["schemaVersion"] = 1.5);
        yield return M("step kind unknown", r => FirstStep(r)["kind"] = "Robot");
        yield return M("step kind missing", r => FirstStep(r).Remove("kind"));
        yield return M("step timeout bad", r => FirstStep(r)["timeout"] = "ten minutes");
        yield return M("step timeout ok", r => FirstStep(r)["timeout"] = "00:10:00");
        yield return M("step timeout newline", r => FirstStep(r)["timeout"] = "00:10:00\n");
        yield return M("retry zero", r => FirstStep(r)["retry"] = new JsonObject { ["maxAttempts"] = 0 });
        yield return M("retry ok", r => FirstStep(r)["retry"] = new JsonObject { ["maxAttempts"] = 3, ["delay"] = "00:00:05" });
        yield return M("retry delay bad", r => FirstStep(r)["retry"] = new JsonObject { ["delay"] = "soon" });
        yield return M("retry not object", r => FirstStep(r)["retry"] = 3);
        yield return M("capabilities duplicate", r => FirstStep(r)["capabilities"] = new JsonArray("a", "a"));
        yield return M("capabilities padded", r => FirstStep(r)["capabilities"] = new JsonArray(" a"));
        yield return M("allowedOutcomes duplicate", r => FirstStep(r)["allowedOutcomes"] = new JsonArray("ok", "ok"));
        yield return M("transition missing outcome", r => r["transitions"] = new JsonArray(new JsonObject { ["from"] = "greet" }));
        yield return M("transition to padded", r => r["transitions"] = new JsonArray(new JsonObject { ["from"] = "greet", ["outcome"] = "ok", ["to"] = " greet" }));
    }

    private static object[] M(string name, Action<JsonObject> change) => [name, change];

    [Theory, MemberData(nameof(StructuralMutations))]
    public void LoaderStructuralValidation_AgreesWithJsonSchema(string name, Action<JsonObject> change)
    {
        var json = Mutate(change);
        var schemaValid = SchemaValid(json);
        var errors = WorkflowLoader.ValidateStructure(JsonNode.Parse(json)!.AsObject());
        Assert.True(schemaValid == (errors.Count == 0),
            $"{name}: schema valid={schemaValid}, loader structural errors=[{string.Join("; ", errors)}]");
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("0.0.0", true)]
    [InlineData("10.20.30", true)]
    [InlineData("01.2.3", false)]
    [InlineData("1.02.3", false)]
    [InlineData("1.2.03", false)]
    [InlineData("+1.2.3", false)]
    [InlineData("1.-2.3", false)]
    [InlineData("-1.2.3", false)]
    [InlineData(" 1.2.3", false)]
    [InlineData("1.2.3 ", false)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3.4", false)]
    [InlineData("1..3", false)]
    [InlineData("a.b.c", false)]
    [InlineData("1.2.3-beta", false)]
    [InlineData("١.٢.٣", false)] // Arabic-Indic digits
    public void Version_StrictParsing_AgreesAcrossParserLoaderAndSchema(string version, bool expected)
    {
        Assert.Equal(expected, WorkflowVersion.TryParse(version, out _));
        if (expected) Assert.Equal(version, WorkflowVersion.Parse(version).Value);
        else Assert.Throws<FormatException>(() => WorkflowVersion.Parse(version));

        var json = Mutate(r => r["version"] = version);
        Assert.Equal(expected, SchemaValid(json));
        Assert.Equal(expected, WorkflowLoader.Load(json).IsValid);
    }

    [Fact]
    public void Version_Overflow_IsRejectedByParserAndLoader()
    {
        // The schema has no upper bound on components; the runtime type (int) does.
        Assert.False(WorkflowVersion.TryParse("99999999999.0.0", out _));
        Assert.False(WorkflowLoader.Load(Mutate(r => r["version"] = "99999999999.0.0")).IsValid);
    }

    [Fact]
    public void Version_Null_IsRejected()
    {
        Assert.False(WorkflowVersion.TryParse(null, out _));
        Assert.Throws<FormatException>(() => WorkflowVersion.Parse(null!));
    }

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
