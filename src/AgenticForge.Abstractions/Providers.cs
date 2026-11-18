using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticForge.Domain;

namespace AgenticForge.Abstractions;

/// <summary>The separate concerns a provider can implement. Each has its own port; none leaks into another.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdapterKind
{
    ModelInference,
    Agent,
    Tool,
    ExecutionEnvironment,
    /// <summary>Implements <c>IRunStore</c> (AgenticForge.Application).</summary>
    Persistence,
    Platform,
}

/// <summary>Self-description and capability report of an adapter. Capability names are opaque to the core and matched against step policy.</summary>
public sealed record AdapterDescriptor(string Id, AdapterKind Kind, string Version, IReadOnlyList<string> Capabilities)
{
    /// <summary>Returns the problems that make this descriptor unusable; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("Adapter id must not be blank.");
        if (string.IsNullOrWhiteSpace(Version)) errors.Add("Adapter version must not be blank.");
        if (Capabilities is null) errors.Add("Capabilities must not be null.");
        else if (Capabilities.Any(string.IsNullOrWhiteSpace)) errors.Add("Capability names must not be blank.");
        else if (Capabilities.Distinct(StringComparer.Ordinal).Count() != Capabilities.Count) errors.Add("Capability names must be unique.");
        return errors;
    }
}

public sealed record AdapterHealth(bool Healthy, string? Detail = null);

/// <summary>
/// Optional lifecycle of any adapter. The composition root (see <c>AdapterRegistry</c>) creates adapters with constructor injection,
/// calls <see cref="InitializeAsync"/> once before first use, and disposes them in reverse order. Adapters must not start work or
/// open connections in their constructor, and must tolerate <c>DisposeAsync</c> without a prior initialize.
/// </summary>
public interface IAdapterLifecycle : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);
    ValueTask<AdapterHealth> CheckHealthAsync(CancellationToken cancellationToken);
}

/// <summary>Provider-neutral error classes. Adapters map their SDK/HTTP/process errors to one of these.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderErrorCategory
{
    /// <summary>The provider rejected the request as malformed or unsupported. Not retryable.</summary>
    InvalidRequest,
    Unauthorized,
    NotFound,
    /// <summary>Retryable after a delay.</summary>
    RateLimited,
    /// <summary>Transient outage or network error. Retryable.</summary>
    Unavailable,
    /// <summary>The provider (not the engine) timed out. Retryable.</summary>
    Timeout,
    /// <summary>The provider refused the content (safety, policy). Not retryable.</summary>
    ContentRejected,
    /// <summary>Anything unclassified. Not retryable, so unknown errors never loop.</summary>
    Unknown,
}

/// <summary>An error as the adapter understood it, before normalization. Diagnostics are kept; secret-looking keys are redacted.</summary>
/// <param name="ProviderCode">The provider's own error code or HTTP status, kept verbatim for diagnosis.</param>
public sealed record ProviderError(
    string Adapter,
    ProviderErrorCategory Category,
    string ProviderCode,
    string Message,
    TimeSpan? RetryAfter = null,
    IReadOnlyDictionary<string, string>? Diagnostics = null);

/// <summary>Maps provider errors to the normalized <see cref="Failure"/> the runtime understands, keeping diagnostic details.</summary>
public static class ProviderFailures
{
    public const string AdapterKey = "adapter";
    public const string CategoryKey = "category";
    public const string ProviderCodeKey = "providerCode";
    public const string RetryAfterKey = "retryAfterSeconds";
    public const string ExceptionTypeKey = "exceptionType";
    public const string Redacted = "[redacted]";

    private static readonly string[] SecretMarkers = ["authorization", "token", "secret", "password", "apikey", "api-key", "api_key", "credential", "cookie"];

    public static Failure ToFailure(ProviderError error)
    {
        var (kind, retryable) = error.Category switch
        {
            ProviderErrorCategory.InvalidRequest => (FailureKind.Validation, false),
            ProviderErrorCategory.Timeout => (FailureKind.Timeout, true),
            ProviderErrorCategory.RateLimited or ProviderErrorCategory.Unavailable => (FailureKind.Provider, true),
            _ => (FailureKind.Provider, false),
        };

        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        if (error.Diagnostics is not null)
            foreach (var (key, value) in error.Diagnostics)
                details[key] = SecretMarkers.Any(m => key.Contains(m, StringComparison.OrdinalIgnoreCase)) ? Redacted : value;
        // Reserved keys are written last so diagnostics cannot spoof them.
        details[AdapterKey] = error.Adapter;
        details[CategoryKey] = error.Category.ToString();
        details[ProviderCodeKey] = error.ProviderCode;
        if (error.RetryAfter is { } after) details[RetryAfterKey] = after.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new Failure(kind, "provider_" + Snake(error.Category.ToString()), error.Message, retryable, details);
    }

    /// <summary>Maps an unexpected exception from an adapter to an Unknown provider failure; the exception type is recorded, never rethrown.</summary>
    public static Failure FromException(string adapter, Exception exception)
    {
        if (exception is OperationCanceledException)
            throw new ArgumentException("Cancellation is not a failure; return the Cancelled result instead.", nameof(exception));
        return ToFailure(new ProviderError(adapter, ProviderErrorCategory.Unknown, exception.GetType().Name, exception.Message,
            Diagnostics: new Dictionary<string, string> { [ExceptionTypeKey] = exception.GetType().FullName ?? exception.GetType().Name }));
    }

    private static string Snake(string pascal)
        => string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

// ---------------------------------------------------------------- model inference

public sealed record ModelMessage(string Role, string Content);

public sealed record ModelRequest(StepExecutionContext Context, string Model, IReadOnlyList<ModelMessage> Messages, int? MaxOutputTokens = null);

public sealed record ModelUsage(int InputTokens, int OutputTokens);

public sealed record ModelResult
{
    public InvocationStatus Status { get; init; }
    public string? Text { get; init; }
    public ModelUsage? Usage { get; init; }
    public Failure? Failure { get; init; }

    public static ModelResult Success(string text, ModelUsage? usage = null) => new() { Status = InvocationStatus.Succeeded, Text = text, Usage = usage };
    public static ModelResult Fault(Failure failure) => new() { Status = InvocationStatus.Failed, Failure = failure };
    public static ModelResult CancelledResult() => new() { Status = InvocationStatus.Cancelled };
}

/// <summary>
/// Model inference (port): turns messages into text. Knows nothing about workflows, tools or runs. Obeys the shared rules in
/// docs/design/provider-adapters.md: cancellation returns Cancelled, errors are returned as normalized failures, never thrown.
/// </summary>
public interface IModelInference
{
    AdapterDescriptor Descriptor { get; }
    ValueTask<ModelResult> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------- execution environment

public sealed record ExecutionRequest(
    StepExecutionContext Context,
    string Command,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record ExecutionResult
{
    public InvocationStatus Status { get; init; }
    /// <summary>Set when the command ran to completion. A non-zero exit code is a result, not a failure; the workflow decides what it means.</summary>
    public int? ExitCode { get; init; }
    public string? StandardOutput { get; init; }
    public string? StandardError { get; init; }
    public Failure? Failure { get; init; }

    public static ExecutionResult Completed(int exitCode, string? stdout = null, string? stderr = null)
        => new() { Status = InvocationStatus.Succeeded, ExitCode = exitCode, StandardOutput = stdout, StandardError = stderr };
    public static ExecutionResult Fault(Failure failure) => new() { Status = InvocationStatus.Failed, Failure = failure };
    public static ExecutionResult CancelledResult() => new() { Status = InvocationStatus.Cancelled };
}

/// <summary>Where commands run (local process, container, remote runner). Must stop the work when cancelled.</summary>
public interface IExecutionEnvironment
{
    AdapterDescriptor Descriptor { get; }
    ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------- platform integration

public sealed record PlatformRequest(
    StepExecutionContext Context,
    string Operation,
    JsonElement Payload,
    string? IdempotencyKey = null);

public sealed record PlatformResult
{
    public InvocationStatus Status { get; init; }
    public JsonElement? Output { get; init; }
    public IReadOnlyList<ArtifactRef>? Artifacts { get; init; }
    public Failure? Failure { get; init; }

    public static PlatformResult Success(JsonElement? output = null, IReadOnlyList<ArtifactRef>? artifacts = null)
        => new() { Status = InvocationStatus.Succeeded, Output = output, Artifacts = artifacts };
    public static PlatformResult Fault(Failure failure) => new() { Status = InvocationStatus.Failed, Failure = failure };
    public static PlatformResult CancelledResult() => new() { Status = InvocationStatus.Cancelled };
}

/// <summary>
/// Integration with a collaboration/source platform (issues, pull requests, CI). Operations are named by the adapter's
/// capabilities. With an idempotency key, repeating a call must not repeat the external effect.
/// </summary>
public interface IPlatformIntegration
{
    AdapterDescriptor Descriptor { get; }
    ValueTask<PlatformResult> ExecuteAsync(PlatformRequest request, CancellationToken cancellationToken);
}
