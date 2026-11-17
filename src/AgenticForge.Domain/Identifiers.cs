using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticForge.Domain;

/// <summary>A non-empty string identifier serialized as a plain JSON string.</summary>
public interface IStringId<TSelf> where TSelf : struct, IStringId<TSelf>
{
    string Value { get; }
    static abstract TSelf Create(string value);
}

/// <summary>Serializes any <see cref="IStringId{TSelf}"/> as a bare JSON string.</summary>
public sealed class StringIdConverter<T> : JsonConverter<T> where T : struct, IStringId<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => T.Create(reader.GetString() ?? throw new JsonException("Identifier must be a string."));

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value);
}

internal static class IdGuard
{
    public static string NotBlank(string? value, string type)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{type} must not be empty.", nameof(value))
            : value;
}

/// <summary>Stable identifier of a workflow definition (shared by all its versions).</summary>
[JsonConverter(typeof(StringIdConverter<WorkflowId>))]
public readonly record struct WorkflowId : IStringId<WorkflowId>
{
    public string Value { get; }
    public WorkflowId(string value) => Value = IdGuard.NotBlank(value, nameof(WorkflowId));
    public static WorkflowId Create(string value) => new(value);
    public override string ToString() => Value;
}

/// <summary>Identifier of a step, unique within one workflow definition.</summary>
[JsonConverter(typeof(StringIdConverter<StepId>))]
public readonly record struct StepId : IStringId<StepId>
{
    public string Value { get; }
    public StepId(string value) => Value = IdGuard.NotBlank(value, nameof(StepId));
    public static StepId Create(string value) => new(value);
    public override string ToString() => Value;
}

/// <summary>Identifier of one execution of a workflow.</summary>
[JsonConverter(typeof(StringIdConverter<WorkflowRunId>))]
public readonly record struct WorkflowRunId : IStringId<WorkflowRunId>
{
    public string Value { get; }
    public WorkflowRunId(string value) => Value = IdGuard.NotBlank(value, nameof(WorkflowRunId));
    public static WorkflowRunId Create(string value) => new(value);
    public override string ToString() => Value;
}

/// <summary>Identifier of one execution record (one attempt) of a step within a run.</summary>
[JsonConverter(typeof(StringIdConverter<StepRunId>))]
public readonly record struct StepRunId : IStringId<StepRunId>
{
    public string Value { get; }
    public StepRunId(string value) => Value = IdGuard.NotBlank(value, nameof(StepRunId));
    public static StepRunId Create(string value) => new(value);
    public override string ToString() => Value;
}

/// <summary>Identifier of an artifact.</summary>
[JsonConverter(typeof(StringIdConverter<ArtifactId>))]
public readonly record struct ArtifactId : IStringId<ArtifactId>
{
    public string Value { get; }
    public ArtifactId(string value) => Value = IdGuard.NotBlank(value, nameof(ArtifactId));
    public static ArtifactId Create(string value) => new(value);
    public override string ToString() => Value;
}

/// <summary>Logical name of an agent as referenced by workflow definitions (not a vendor model name).</summary>
[JsonConverter(typeof(StringIdConverter<AgentId>))]
public readonly record struct AgentId : IStringId<AgentId>
{
    public string Value { get; }
    public AgentId(string value) => Value = IdGuard.NotBlank(value, nameof(AgentId));
    public static AgentId Create(string value) => new(value);
    public override string ToString() => Value;
}

/// <summary>Logical name of a tool as referenced by workflow definitions.</summary>
[JsonConverter(typeof(StringIdConverter<ToolName>))]
public readonly record struct ToolName : IStringId<ToolName>
{
    public string Value { get; }
    public ToolName(string value) => Value = IdGuard.NotBlank(value, nameof(ToolName));
    public static ToolName Create(string value) => new(value);
    public override string ToString() => Value;
}

/// <summary>
/// Semantic version of a workflow definition, serialized as "major.minor.patch".
/// A run is always pinned to exactly one version.
/// </summary>
[JsonConverter(typeof(StringIdConverter<WorkflowVersion>))]
public readonly record struct WorkflowVersion : IStringId<WorkflowVersion>, IComparable<WorkflowVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    public WorkflowVersion(int major, int minor, int patch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        (Major, Minor, Patch) = (major, minor, patch);
    }

    public string Value => $"{Major}.{Minor}.{Patch}";

    public static WorkflowVersion Create(string value) => Parse(value);

    public static WorkflowVersion Parse(string value)
    {
        var parts = (value ?? string.Empty).Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch))
        {
            throw new FormatException($"'{value}' is not a valid workflow version (expected major.minor.patch).");
        }
        return new WorkflowVersion(major, minor, patch);
    }

    public int CompareTo(WorkflowVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => Value;
}
