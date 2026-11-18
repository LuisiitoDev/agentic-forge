using AgenticForge.Abstractions;
using AgenticForge.Domain;

namespace AgenticForge.Adapters.Tests;

public sealed class ProviderErrorTests
{
    [Theory]
    [InlineData(ProviderErrorCategory.InvalidRequest, FailureKind.Validation, false, "provider_invalid_request")]
    [InlineData(ProviderErrorCategory.Unauthorized, FailureKind.Provider, false, "provider_unauthorized")]
    [InlineData(ProviderErrorCategory.NotFound, FailureKind.Provider, false, "provider_not_found")]
    [InlineData(ProviderErrorCategory.RateLimited, FailureKind.Provider, true, "provider_rate_limited")]
    [InlineData(ProviderErrorCategory.Unavailable, FailureKind.Provider, true, "provider_unavailable")]
    [InlineData(ProviderErrorCategory.Timeout, FailureKind.Timeout, true, "provider_timeout")]
    [InlineData(ProviderErrorCategory.ContentRejected, FailureKind.Provider, false, "provider_content_rejected")]
    [InlineData(ProviderErrorCategory.Unknown, FailureKind.Provider, false, "provider_unknown")]
    public void Categories_map_to_kind_retryability_and_code(ProviderErrorCategory category, FailureKind kind, bool retryable, string code)
    {
        var f = ProviderFailures.ToFailure(new ProviderError("acme", category, "E1", "msg"));
        Assert.Equal((kind, retryable, code), (f.Kind, f.Retryable, f.Code));
    }

    [Fact]
    public void Diagnostics_are_kept_secrets_are_redacted_and_reserved_keys_cannot_be_spoofed()
    {
        var f = ProviderFailures.ToFailure(new ProviderError("acme", ProviderErrorCategory.RateLimited, "429", "slow down", TimeSpan.FromSeconds(2.5),
            new Dictionary<string, string> { ["requestId"] = "r-9", ["Authorization"] = "Bearer abc", ["adapter"] = "spoofed" }));
        Assert.Equal("slow down", f.Message);
        Assert.Equal("r-9", f.Details!["requestId"]);
        Assert.Equal(ProviderFailures.Redacted, f.Details["Authorization"]);
        Assert.Equal("acme", f.Details[ProviderFailures.AdapterKey]);
        Assert.Equal("429", f.Details[ProviderFailures.ProviderCodeKey]);
        Assert.Equal("2.5", f.Details[ProviderFailures.RetryAfterKey]);
        Assert.DoesNotContain("abc", string.Join(' ', f.Details.Values));
    }

    [Fact]
    public void Exceptions_map_to_unknown_non_retryable_failures_with_the_type_recorded()
    {
        var f = ProviderFailures.FromException("acme", new InvalidOperationException("kaput"));
        Assert.Equal("provider_unknown", f.Code);
        Assert.False(f.Retryable);
        Assert.Equal("kaput", f.Message);
        Assert.Equal("System.InvalidOperationException", f.Details![ProviderFailures.ExceptionTypeKey]);
    }

    [Fact]
    public void Cancellation_is_not_mapped_to_a_failure()
        => Assert.Throws<ArgumentException>(() => ProviderFailures.FromException("acme", new OperationCanceledException()));

    [Fact]
    public void Descriptors_are_validated()
    {
        Assert.Empty(new AdapterDescriptor("a", AdapterKind.Platform, "1.0.0", ["x"]).Validate());
        Assert.NotEmpty(new AdapterDescriptor(" ", AdapterKind.Platform, "1.0.0", []).Validate());
        Assert.NotEmpty(new AdapterDescriptor("a", AdapterKind.Platform, "", []).Validate());
        Assert.NotEmpty(new AdapterDescriptor("a", AdapterKind.Platform, "1", ["x", "x"]).Validate());
    }
}
