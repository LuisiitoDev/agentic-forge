using AgenticForge.Abstractions;
using AgenticForge.Domain;

namespace AgenticForge.Adapters.Conformance;

/// <summary>
/// Behavior every adapter port must have, whatever the provider. Subclass one of the port suites (not this class), supply a
/// factory for your adapter and a valid request, and the inherited tests run against it. See docs/design/provider-adapters.md.
/// </summary>
public abstract class PortConformance<TAdapter, TRequest, TResult> where TAdapter : class
{
    /// <summary>A healthy adapter that succeeds for <see cref="CreateRequest"/>.</summary>
    protected abstract TAdapter CreateAdapter();
    /// <summary>A request the adapter accepts and answers successfully.</summary>
    protected abstract TRequest CreateRequest(TAdapter adapter);
    protected abstract ValueTask<TResult> InvokeAsync(TAdapter adapter, TRequest request, CancellationToken cancellationToken);
    protected abstract InvocationStatus StatusOf(TResult result);
    protected abstract Failure? FailureOf(TResult result);

    /// <summary>An adapter whose backing provider fails with a classified error for <see cref="CreateRequest"/>; null skips the failure tests.</summary>
    protected virtual TAdapter? CreateFailingAdapter() => null;
    /// <summary>An adapter whose call never completes on its own (to test cancellation); null skips that test.</summary>
    protected virtual TAdapter? CreateBlockingAdapter() => null;

    protected static StepExecutionContext Context(DateTimeOffset? deadline = null)
        => new(new Correlation(new WorkflowRunId("run-conformance"), "corr-conformance", new StepRunId("step-conformance")), 1, deadline);

    [Fact]
    public async Task Success_has_succeeded_status_and_no_failure()
    {
        await using var scope = new AdapterScope(CreateAdapter());
        var result = await InvokeAsync(scope.Adapter, CreateRequest(scope.Adapter), CancellationToken.None);
        Assert.Equal(InvocationStatus.Succeeded, StatusOf(result));
        Assert.Null(FailureOf(result));
    }

    [Fact]
    public async Task Already_cancelled_token_is_reported_as_cancelled_never_as_success_or_failure()
    {
        await using var scope = new AdapterScope(CreateAdapter());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        try
        {
            var result = await InvokeAsync(scope.Adapter, CreateRequest(scope.Adapter), cts.Token);
            Assert.Equal(InvocationStatus.Cancelled, StatusOf(result));
            Assert.Null(FailureOf(result));
        }
        catch (OperationCanceledException)
        {
            // Throwing is an accepted way to honor cancellation.
        }
    }

    [Fact]
    public async Task Cancelling_a_running_call_stops_it_promptly()
    {
        var blocking = CreateBlockingAdapter();
        if (blocking is null) return;
        await using var scope = new AdapterScope(blocking);
        using var cts = new CancellationTokenSource();
        var call = InvokeAsync(scope.Adapter, CreateRequest(scope.Adapter), cts.Token).AsTask();
        await Task.Delay(50);
        await cts.CancelAsync();

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(call, finished); // honored cancellation instead of hanging
        if (call.IsCanceled) return;
        var result = await call;
        Assert.Equal(InvocationStatus.Cancelled, StatusOf(result));
        Assert.Null(FailureOf(result));
    }

    [Fact]
    public async Task Provider_errors_are_returned_as_normalized_failures_not_thrown()
    {
        var failing = CreateFailingAdapter();
        if (failing is null) return;
        await using var scope = new AdapterScope(failing);
        var result = await InvokeAsync(scope.Adapter, CreateRequest(scope.Adapter), CancellationToken.None);

        Assert.Equal(InvocationStatus.Failed, StatusOf(result));
        var failure = FailureOf(result);
        Assert.NotNull(failure);
        FailureRules.AssertNormalized(failure);
    }

    [Fact]
    public async Task Lifecycle_is_ordered_idempotent_to_dispose_and_reports_health()
    {
        var adapter = CreateAdapter();
        if (adapter is not IAdapterLifecycle lifecycle)
        {
            await DisposeIfNeededAsync(adapter);
            return;
        }
        await lifecycle.InitializeAsync(CancellationToken.None);
        Assert.True((await lifecycle.CheckHealthAsync(CancellationToken.None)).Healthy);
        await lifecycle.DisposeAsync();
        await lifecycle.DisposeAsync(); // second dispose must not throw

        var neverInitialized = CreateAdapter();
        await DisposeIfNeededAsync(neverInitialized); // dispose without initialize must not throw
    }

    private static async ValueTask DisposeIfNeededAsync(object adapter)
    {
        if (adapter is IAsyncDisposable d) await d.DisposeAsync();
        else if (adapter is IDisposable s) s.Dispose();
    }

    /// <summary>Initializes lifecycle adapters and disposes them, so every test starts and ends clean.</summary>
    protected sealed class AdapterScope : IAsyncDisposable
    {
        public AdapterScope(TAdapter adapter)
        {
            Adapter = adapter;
            if (adapter is IAdapterLifecycle lifecycle) lifecycle.InitializeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        public TAdapter Adapter { get; }
        public async ValueTask DisposeAsync() => await DisposeIfNeededAsync(Adapter);
    }
}

/// <summary>What a normalized <see cref="Failure"/> must look like. Used by all suites.</summary>
public static class FailureRules
{
    public static void AssertNormalized(Failure failure)
    {
        Assert.False(string.IsNullOrWhiteSpace(failure.Code), "Failure.Code must be a stable, non-blank code.");
        Assert.False(string.IsNullOrWhiteSpace(failure.Message), "Failure.Message must be non-blank.");
        // Internal means a defect in Agentic Forge; an adapter reports provider-side problems.
        Assert.NotEqual(FailureKind.Internal, failure.Kind);
        if (failure.Kind == FailureKind.Timeout) Assert.True(failure.Retryable, "Provider timeouts are retryable.");
        if (failure.Kind is FailureKind.Provider or FailureKind.Timeout)
        {
            Assert.NotNull(failure.Details);
            Assert.True(failure.Details!.ContainsKey(ProviderFailures.AdapterKey), "Details must name the adapter.");
            Assert.True(failure.Details.ContainsKey(ProviderFailures.ProviderCodeKey), "Details must keep the provider's own error code.");
        }
        foreach (var (key, value) in failure.Details ?? new Dictionary<string, string>())
            if (new[] { "authorization", "token", "secret", "password", "apikey" }.Any(m => key.Contains(m, StringComparison.OrdinalIgnoreCase)))
                Assert.Equal(ProviderFailures.Redacted, value);
    }
}
