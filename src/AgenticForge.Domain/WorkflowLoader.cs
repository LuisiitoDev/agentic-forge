using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgenticForge.Domain;

/// <summary>Outcome of loading a workflow definition: either a valid definition or a non-empty error list.</summary>
public sealed record WorkflowLoadResult(WorkflowDefinition? Definition, IReadOnlyList<string> Errors)
{
    public bool IsValid => Definition is not null && Errors.Count == 0;
}

/// <summary>
/// Loads a workflow definition from JSON and validates it completely before it can be executed:
/// well-formed JSON, structural rules (<see cref="ValidateStructure"/>, mirroring the committed JSON Schema),
/// typed deserialization, then <see cref="WorkflowDefinition.Validate"/> semantic rules.
/// Never throws for bad input (including null) and needs no external services.
/// </summary>
public static class WorkflowLoader
{
    private static readonly string[] Required = ["id", "version", "name", "start", "steps", "transitions", "schemaVersion"];
    private static readonly string[] StepKinds = ["Agent", "Tool", "Approval"];

    // Mirrors docs/design/schema/workflow-definition.schema.json. \z (not $) so a trailing newline is rejected, as in ECMA-262.
    private static readonly Regex IdentifierPattern = new(@"^\S(.*\S)?\z", RegexOptions.CultureInvariant);
    private static readonly Regex DurationPattern = new(@"^([0-9]+\.)?[0-9]{1,2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?\z", RegexOptions.CultureInvariant);

    /// <summary>Loads a definition. Null or malformed input yields an error result, never an exception.</summary>
    public static WorkflowLoadResult Load(string? json)
    {
        if (json is null) return Fail("Workflow JSON must not be null.");

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException e) { return Fail($"Workflow is not valid JSON: {e.Message}"); }
        catch (ArgumentException e) { return Fail($"Workflow is not valid JSON: {e.Message}"); } // e.g. duplicate keys

        if (node is not JsonObject root) return Fail("Workflow must be a JSON object.");

        var errors = ValidateStructure(root);
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

    /// <summary>
    /// The single authoritative structural validator. It enforces the same contract as the committed JSON Schema
    /// (parity is covered by tests); cross-reference rules stay in <see cref="WorkflowDefinition.Validate"/>.
    /// </summary>
    public static List<string> ValidateStructure(JsonObject root)
    {
        var errors = new List<string>();
        foreach (var name in Required)
            if (!root.ContainsKey(name)) errors.Add($"Missing required property '{name}'.");

        if (root.ContainsKey("schemaVersion"))
        {
            if (!TryInteger(root["schemaVersion"], out var version))
                errors.Add("'schemaVersion' must be an integer.");
            else if (version != ContractSchema.Current)
                errors.Add($"Unsupported schemaVersion {version}; this reader supports {ContractSchema.Current}.");
        }

        if (root.ContainsKey("id")) CheckPattern(root["id"], IdentifierPattern, "'id'", errors);
        if (root.ContainsKey("start")) CheckPattern(root["start"], IdentifierPattern, "'start'", errors);
        if (root.ContainsKey("name") && !(IsString(root["name"], out var n) && n.Length > 0))
            errors.Add("'name' must be a non-empty string.");
        if (root.ContainsKey("version")
            && !(IsString(root["version"], out var v) && WorkflowVersion.TryParse(v, out _)))
            errors.Add("'version' must be a major.minor.patch version without signs or leading zeros.");

        if (root.ContainsKey("steps"))
        {
            if (root["steps"] is not JsonArray steps) errors.Add("'steps' must be an array.");
            else
            {
                if (steps.Count == 0) errors.Add("'steps' must contain at least one step.");
                for (var i = 0; i < steps.Count; i++) CheckStep(steps[i], i, errors);
            }
        }

        if (root.ContainsKey("transitions"))
        {
            if (root["transitions"] is not JsonArray transitions) errors.Add("'transitions' must be an array.");
            else for (var i = 0; i < transitions.Count; i++) CheckTransition(transitions[i], i, errors);
        }
        return errors;
    }

    private static void CheckStep(JsonNode? node, int index, List<string> errors)
    {
        var at = $"steps[{index}]";
        if (node is not JsonObject step) { errors.Add($"{at} must be an object."); return; }

        if (!step.ContainsKey("id")) errors.Add($"{at}: missing required property 'id'.");
        else CheckPattern(step["id"], IdentifierPattern, $"{at}.id", errors);

        var hasKind = false;
        var isApproval = false;
        if (!step.ContainsKey("kind")) errors.Add($"{at}: missing required property 'kind'.");
        else if (IsString(step["kind"], out var kind) && StepKinds.Contains(kind))
        {
            hasKind = true;
            isApproval = kind == "Approval";
        }
        else errors.Add($"{at}: unknown step kind; expected one of {string.Join(", ", StepKinds)}.");

        if (step.ContainsKey("target")) CheckPattern(step["target"], IdentifierPattern, $"{at}.target", errors);
        if (hasKind)
        {
            if (isApproval && step.ContainsKey("target")) errors.Add($"{at}: an Approval step must not have a target.");
            if (isApproval && step.ContainsKey("capabilities")) errors.Add($"{at}: an Approval step must not declare capabilities.");
            if (!isApproval && !step.ContainsKey("target")) errors.Add($"{at}: a non-Approval step requires a target.");
        }

        if (step.ContainsKey("capabilities")) CheckUniqueNames(step["capabilities"], $"{at}.capabilities", errors);
        if (step.ContainsKey("allowedOutcomes")) CheckUniqueNames(step["allowedOutcomes"], $"{at}.allowedOutcomes", errors);
        if (step.ContainsKey("timeout")) CheckPattern(step["timeout"], DurationPattern, $"{at}.timeout", errors, "a duration (hh:mm:ss)");

        if (step.ContainsKey("retry"))
        {
            if (step["retry"] is not JsonObject retry) errors.Add($"{at}.retry must be an object.");
            else
            {
                if (retry.ContainsKey("maxAttempts") && !(TryInteger(retry["maxAttempts"], out var max) && max >= 1))
                    errors.Add($"{at}.retry: MaxAttempts must be an integer >= 1.");
                if (retry.ContainsKey("delay")) CheckPattern(retry["delay"], DurationPattern, $"{at}.retry.delay", errors, "a duration (hh:mm:ss)");
            }
        }
    }

    private static void CheckTransition(JsonNode? node, int index, List<string> errors)
    {
        var at = $"transitions[{index}]";
        if (node is not JsonObject t) { errors.Add($"{at} must be an object."); return; }
        foreach (var req in new[] { "from", "outcome" })
            if (!t.ContainsKey(req)) errors.Add($"{at}: missing required property '{req}'.");
        foreach (var key in new[] { "from", "outcome", "to" })
            if (t.ContainsKey(key)) CheckPattern(t[key], IdentifierPattern, $"{at}.{key}", errors);
    }

    private static void CheckUniqueNames(JsonNode? node, string at, List<string> errors)
    {
        if (node is not JsonArray arr) { errors.Add($"{at} must be an array."); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in arr)
        {
            if (!(IsString(item, out var s) && IdentifierPattern.IsMatch(s)))
                errors.Add($"{at} must contain only non-blank strings without surrounding whitespace.");
            else if (!seen.Add(s)) errors.Add($"{at} contains duplicate value '{s}'.");
        }
    }

    private static void CheckPattern(JsonNode? node, Regex pattern, string at, List<string> errors,
        string expected = "a non-blank string without surrounding whitespace")
    {
        if (!(IsString(node, out var s) && pattern.IsMatch(s))) errors.Add($"{at} must be {expected}.");
    }

    private static bool IsString(JsonNode? node, out string value)
    {
        value = "";
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s) && s is not null) { value = s; return true; }
        return false;
    }

    // JSON Schema "integer": any number with no fractional part (1 and 1.0 both qualify).
    private static bool TryInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue jv) return false;
        if (jv.TryGetValue<long>(out value)) return true;
        if (jv.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < long.MaxValue) { value = (long)d; return true; }
        return false;
    }

    private static WorkflowLoadResult Fail(string error) => new(null, [error]);
}
