# Provider adapters: contracts, registration and conformance

## ADR: one port per concern, normalized outcomes, composition-root registration

**Status:** accepted. **Context:** Agentic Forge must not leak provider models into the core. Inference, agent execution, tools, execution
environments, persistence and platform integrations vary independently, so a provider that does one must not be forced to model the others.

**Decision**
1. One small port per concern. Ports and their DTOs use only core types (`Failure`, `ArtifactRef`, `Correlation`) and never provider SDK types.
2. Every call returns a result with the shared `InvocationStatus` (`Succeeded`, `Failed`, `Cancelled`, `NeedsInput`); errors are values, not exceptions.
3. Provider errors are normalized by `ProviderFailures` into `Failure` with the provider's own details preserved.
4. Adapters are plain objects built by constructor injection and registered in an `AdapterRegistry` at the composition root. The core never resolves services itself and has no static/ambient lookup.
5. A reusable, abstract conformance suite defines the behavior; fakes pass it and real adapters subclass it.

**Consequences:** swapping a provider is a change to the composition root only. Core projects (`Domain`, `Abstractions`, `Application`, `Runtime`) reference no provider SDK; a test asserts their assembly references.

## Ports

| Concern | Port (assembly) | Notes |
|---|---|---|
| Model inference | `IModelInference` (Abstractions) | messages in, text and usage out |
| Agent | `IAgent` (Abstractions) | one unit of work for one step; may be built on `IModelInference` (`ModelBackedAgent`) |
| Tools | `IToolProvider` (Abstractions) | descriptors with side-effect class |
| Execution environment | `IExecutionEnvironment` (Abstractions) | a non-zero exit code is a result, not a failure |
| Persistence | `IRunStore` (Application) | conformance: `RunStoreContractTests` in the SQLite tests |
| Platform integration | `IPlatformIntegration` (Abstractions) | named operations; idempotency key prevents repeated effects |

Each non-agent/tool port exposes an `AdapterDescriptor` (id, `AdapterKind`, version, capabilities). That is the **capability report**:
`AdapterRegistry.Describe()` lists it, and capability names are matched against step policy by the governance layer (see governance.md).

## Cancellation, timeout, errors and retryability

- **Cancellation** is not a failure. An adapter observes the token and returns the `Cancelled` result (throwing `OperationCanceledException` is also accepted). It must stop its work, not just stop waiting, and must do so promptly. An already-cancelled token yields `Cancelled` without side effects.
- **Timeout** has two sources. The *step* timeout is enforced by the engine through the token (a linked source); the adapter just honors cancellation and the engine records a `Timeout` failure. A *provider's own* timeout is reported by the adapter as `ProviderErrorCategory.Timeout` (`FailureKind.Timeout`, retryable). `StepExecutionContext.Deadline` is advisory.
- **Errors** are returned, never thrown. Unexpected exceptions map through `ProviderFailures.FromException` (category Unknown, not retryable, so unclassified errors never loop). `FailureKind.Internal` is reserved for defects inside Agentic Forge, so adapters never use it.
- **Normalization** (`ProviderFailures.ToFailure`):

| Category | `FailureKind` | Retryable | Code |
|---|---|---|---|
| InvalidRequest | Validation | no | `provider_invalid_request` |
| Unauthorized, NotFound, ContentRejected, Unknown | Provider | no | `provider_unauthorized`, ... |
| RateLimited, Unavailable | Provider | yes | `provider_rate_limited`, `provider_unavailable` |
| Timeout | Timeout | yes | `provider_timeout` |

  `Failure.Details` always carries `adapter`, `category`, `providerCode` (the provider's own code, verbatim) and `retryAfterSeconds` when known, plus the adapter's diagnostics (request ids and so on). Diagnostic keys that look like secrets (`authorization`, `token`, `secret`, `password`, `apikey`, `credential`, `cookie`) are replaced with `[redacted]`; reserved keys cannot be overwritten by diagnostics. The message is the provider's message: adapters must not put secrets in it.

## Lifecycle and dependency injection

- Adapters take their dependencies (HTTP client, options, credentials provider) in the constructor. Constructors do no I/O.
- Adapters that need set-up implement `IAdapterLifecycle`: `InitializeAsync` once before first use, `CheckHealthAsync`, `DisposeAsync` (safe when called twice or without initialize).
- `AdapterRegistry`: `Add*` registers (duplicate ids, invalid descriptors and wrong kinds are rejected; registration closes at initialization), `InitializeAsync` starts lifecycle adapters in registration order and is all-or-nothing (a failure disposes the ones already started and throws `AdapterInitializationException` naming the adapter), `DisposeAsync` disposes in reverse order. The registry is an `IAgentResolver` and `IToolResolver`, so it plugs straight into `WorkflowOrchestrator`. With `Microsoft.Extensions.DependencyInjection` in a host, register the same objects there and feed them to the registry; the core takes no DI container dependency.

## Conformance suite

Project `tests/AgenticForge.Adapters.Conformance` holds one abstract suite per port (`AgentConformance`, `ModelInferenceConformance`, `ToolProviderConformance`, `ExecutionEnvironmentConformance`, `PlatformIntegrationConformance`) on top of `PortConformance`. Inherited tests check: success shape, cancelled-token behavior, prompt stop of a blocked call, failures returned as normalized `Failure` with details (and no leaked secrets), lifecycle, descriptor validity, and per-port rules (agent outcome, unknown tool/operation is a failure, platform idempotency, exit code).

## Fakes

`FakeAgent`, `ModelBackedAgent`, `FakeModelInference`, `FakeToolProvider`, `FakeExecutionEnvironment`, `FakePlatformIntegration` (modes: `Normal`, `RateLimited`, `Block`) allow end-to-end runs with no accounts. `tests/AgenticForge.Adapters.Tests` runs one workflow definition on two different agent implementations and on swapped models, and checks failure, step-timeout and host-cancellation outcomes.

## Example: adding a provider

Say `Acme` offers model inference.

1. Create `AgenticForge.Adapters.Acme` referencing `AgenticForge.Abstractions` and the Acme SDK (never from a core project).
2. Implement the port; map every SDK error to a `ProviderError` and return `ProviderFailures.ToFailure(...)`; observe the token.

```csharp
public sealed class AcmeModel(AcmeClient client) : IModelInference
{
    public AdapterDescriptor Descriptor { get; } = new("acme", AdapterKind.ModelInference, "1.0.0", ["chat"]);

    public async ValueTask<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        try
        {
            var reply = await client.ChatAsync(request.Model, request.Messages.Select(m => (m.Role, m.Content)), ct);
            return ModelResult.Success(reply.Text, new ModelUsage(reply.InputTokens, reply.OutputTokens));
        }
        catch (OperationCanceledException) { return ModelResult.CancelledResult(); }
        catch (AcmeException e) when (e.Status == 429)
        {
            return ModelResult.Fault(ProviderFailures.ToFailure(new ProviderError("acme", ProviderErrorCategory.RateLimited,
                e.Status.ToString(), e.Message, e.RetryAfter, new Dictionary<string, string> { ["requestId"] = e.RequestId })));
        }
    }
}
```

3. Prove it: in the adapter's test project reference the conformance project and subclass `ModelInferenceConformance` (supply `CreateAdapter`, and optionally `CreateFailingAdapter` / `CreateBlockingAdapter` backed by a stubbed HTTP handler).
4. Register it at the composition root: `registry.AddModel(new AcmeModel(client))`, optionally `AddAgent(new ModelBackedAgent("writer", acme))`. Workflow definitions refer to the logical agent id only and do not change.

## Out of scope

Real provider adapters beyond the reference fakes (none is needed for the reference workflow yet), a DI-container integration package, streaming inference and tool protocols such as MCP.
