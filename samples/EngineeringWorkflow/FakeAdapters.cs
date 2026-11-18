using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticForge.Abstractions;
using AgenticForge.Domain;

namespace AgenticForge.Samples.EngineeringWorkflow;

/// <summary>Builds artifact references whose digest really is the hash of the content, as the approval binding requires.</summary>
internal static class SampleArtifacts
{
    public static ArtifactRef Create(string id, string kind, string content, string mediaType = "text/markdown")
        => new(new ArtifactId(id), kind, $"sample://artifacts/{id}", mediaType, ArtifactDigest.Compute(Encoding.UTF8.GetBytes(content)));

    public static JsonElement Json(JsonNode node) => JsonSerializer.SerializeToElement(node);
}

/// <summary>
/// Deterministic stand-in for a planning agent. Reads the request from the run input and returns a plan artifact.
/// Replace it with any <see cref="IAgent"/> (LLM-backed, human-in-the-loop, remote) without touching the workflow.
/// </summary>
public sealed class FakePlanner : IAgent
{
    public AgentDescriptor Descriptor { get; } = new(new AgentId("planner"), "Fake planner", ["read-requirements"]);
    public int Calls { get; private set; }

    public ValueTask<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        var title = request.Input.GetProperty("input").TryGetProperty("title", out var t) ? t.GetString() : "untitled";
        var plan = $"# Plan: {title}\n1. Add the change\n2. Cover it with tests\n";
        var output = new JsonObject { ["summary"] = $"Plan for '{title}'", ["tasks"] = new JsonArray("Add the change", "Cover it with tests") };
        return ValueTask.FromResult(AgentInvocationResult.Success("planned", SampleArtifacts.Json(output),
            [SampleArtifacts.Create("plan", "plan", plan)]));
    }
}

/// <summary>
/// Deterministic implementer. Used by both the <c>implement</c> and <c>rework</c> steps: when a review exists in its input it
/// addresses the findings, otherwise it produces the first revision. Stateless, so it behaves the same after a restart.
/// </summary>
public sealed class FakeImplementer : IAgent
{
    public AgentDescriptor Descriptor { get; } = new(new AgentId("implementer"), "Fake implementer", ["read-repo", "write-repo"]);
    public int Calls { get; private set; }
    /// <summary>The findings each rework call was given (empty for the first revision).</summary>
    public List<string> AddressedFindings { get; } = [];

    public ValueTask<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        var steps = request.Input.GetProperty("steps");
        var findings = steps.TryGetProperty("review", out var review) && review.TryGetProperty("findings", out var f)
            ? f.EnumerateArray().Select(x => x.GetProperty("message").GetString()!).ToList()
            : [];
        AddressedFindings.AddRange(findings);
        var revision = findings.Count == 0 ? 1 : 2;
        var patch = $"patch revision {revision}\n" + string.Concat(findings.Select(x => $"fixes: {x}\n"));
        var output = new JsonObject { ["revision"] = revision, ["addressed"] = new JsonArray(findings.Select(x => (JsonNode)x).ToArray()) };
        return ValueTask.FromResult(AgentInvocationResult.Success("implemented", SampleArtifacts.Json(output),
            [SampleArtifacts.Create($"patch-r{revision}", "patch", patch, "text/x-diff")]));
    }
}

/// <summary>
/// Deterministic reviewer. Requests changes for the first <c>changeRounds</c> reviews, then passes. The round is derived from the
/// input (a <c>rework</c> output exists on the second review), not from instance state, so a restart cannot change the verdict.
/// </summary>
public sealed class FakeReviewer(int changeRounds = 1) : IAgent
{
    public AgentDescriptor Descriptor { get; } = new(new AgentId("reviewer"), "Fake reviewer", ["read-repo"]);
    public int Calls { get; private set; }

    public ValueTask<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        var round = request.Input.GetProperty("steps").TryGetProperty("rework", out _) ? 2 : 1;
        if (round > changeRounds)
        {
            var ok = new JsonObject { ["round"] = round, ["findings"] = new JsonArray() };
            return ValueTask.FromResult(AgentInvocationResult.Success("pass", SampleArtifacts.Json(ok),
                [SampleArtifacts.Create($"review-r{round}", "review", $"review round {round}: no findings\n")]));
        }
        var message = $"Missing test for edge case (round {round})";
        var output = new JsonObject
        {
            ["round"] = round,
            ["findings"] = new JsonArray(new JsonObject { ["id"] = $"R{round}-1", ["severity"] = "major", ["message"] = message }),
        };
        return ValueTask.FromResult(AgentInvocationResult.Success("changes-requested", SampleArtifacts.Json(output),
            [SampleArtifacts.Create($"review-r{round}", "review", $"review round {round}: {message}\n")]));
    }
}

/// <summary>
/// Fake external-effect tool that stands in for "open a pull request". A GitHub adapter would implement the same
/// <see cref="IToolProvider"/> contract under the same tool name. Idempotent per key, like a real publisher must be.
/// </summary>
public sealed class FakePublisher : IToolProvider
{
    private readonly Dictionary<string, ToolInvocationResult> _byKey = [];
    public const string PublishTool = "publish-change";
    public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [new(new ToolName(PublishTool), "Publishes the reviewed change for human consumption", ToolSideEffects.Idempotent)];
    /// <summary>How many external effects actually happened.</summary>
    public int Effects { get; private set; }

    public ValueTask<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken)
    {
        if (request.IdempotencyKey is { } key && _byKey.TryGetValue(key, out var previous)) return ValueTask.FromResult(previous);
        Effects++;
        var reference = $"sample://change-requests/{Effects}";
        var result = ToolInvocationResult.Success(SampleArtifacts.Json(new JsonObject { ["reference"] = reference }),
            [SampleArtifacts.Create($"change-request-{Effects}", "change-request", $"change request {reference}\n", "text/plain")]);
        if (request.IdempotencyKey is { } k) _byKey[k] = result;
        return ValueTask.FromResult(result);
    }
}
