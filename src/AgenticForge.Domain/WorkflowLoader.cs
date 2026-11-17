using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticForge.Domain;

/// <summary>Outcome of loading a workflow definition: either a valid definition or a non-empty error list.</summary>
public sealed record WorkflowLoadResult(WorkflowDefinition? Definition, IReadOnlyList<string> Errors)
{
    public bool IsValid => Definition is not null && Errors.Count == 0;
}

/// <summary>
/// Loads a workflow definition from JSON and validates it completely before it can be executed:
/// well-formed JSON, supported <c>schemaVersion</c>, required properties, typed deserialization, then
/// <see cref="WorkflowDefinition.Validate"/> semantic rules. Never throws for bad input and needs no external services.
/// </summary>
public static class WorkflowLoader
{
    private static readonly string[] Required = ["id", "version", "name", "start", "steps", "transitions", "schemaVersion"];

    public static WorkflowLoadResult Load(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException e) { return Fail($"Workflow is not valid JSON: {e.Message}"); }

        if (node is not JsonObject root) return Fail("Workflow must be a JSON object.");

        var errors = new List<string>();
        foreach (var name in Required)
            if (root[name] is null) errors.Add($"Missing required property '{name}'.");

        if (root["schemaVersion"] is { } sv)
        {
            if (sv is not JsonValue v || !v.TryGetValue<int>(out var version))
                errors.Add("'schemaVersion' must be an integer.");
            else if (version != ContractSchema.Current)
                errors.Add($"Unsupported schemaVersion {version}; this reader supports {ContractSchema.Current}.");
        }
        if (errors.Count > 0) return new(null, errors);

        WorkflowDefinition? def;
        try { def = root.Deserialize<WorkflowDefinition>(ContractJson.Options); }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            return Fail($"Workflow does not match the definition shape: {e.Message}");
        }
        if (def is null) return Fail("Workflow is empty.");

        var semantic = def.Validate();
        return semantic.Count == 0 ? new(def, []) : new(null, semantic);
    }

    public static WorkflowLoadResult LoadFile(string path) => Load(File.ReadAllText(path));

    private static WorkflowLoadResult Fail(string error) => new(null, [error]);
}
