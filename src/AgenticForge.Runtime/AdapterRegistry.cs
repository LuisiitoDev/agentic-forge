using AgenticForge.Abstractions;
using AgenticForge.Domain;

namespace AgenticForge.Runtime;

public sealed class AdapterRegistrationException(string message) : InvalidOperationException(message);

/// <summary>An adapter failed to initialize; <see cref="Adapter"/> names it. Adapters initialized before it have been disposed.</summary>
public sealed class AdapterInitializationException(string adapter, Exception inner)
    : InvalidOperationException($"Adapter '{adapter}' failed to initialize: {inner.Message}", inner)
{
    public string Adapter { get; } = adapter;
}

/// <summary>
/// Composition-root helper: the one place that knows which concrete adapters exist. Adapters are plain objects built with
/// constructor injection and registered here; the core only sees the ports. Implements the engine's resolvers, so swapping an
/// implementation never touches workflow definitions or orchestration. Register everything, then <see cref="InitializeAsync"/>.
/// </summary>
public sealed class AdapterRegistry : IAgentResolver, IToolResolver, IAsyncDisposable
{
    private readonly List<object> _ordered = [];
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IToolProvider> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IModelInference> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IExecutionEnvironment> _environments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPlatformIntegration> _platforms = new(StringComparer.Ordinal);
    private readonly List<object> _initialized = [];
    private bool _started;
    private bool _disposed;

    public AdapterRegistry AddAgent(IAgent agent)
    {
        Guard();
        var id = agent.Descriptor.Id.Value;
        if (_agents.ContainsKey(id)) throw new AdapterRegistrationException($"Agent '{id}' is already registered.");
        _agents[id] = agent;
        _ordered.Add(agent);
        return this;
    }

    public AdapterRegistry AddToolProvider(IToolProvider provider)
    {
        Guard();
        foreach (var tool in provider.Tools)
            if (_tools.ContainsKey(tool.Name.Value) || provider.Tools.Count(t => t.Name == tool.Name) > 1)
                throw new AdapterRegistrationException($"Tool '{tool.Name}' is offered by more than one provider.");
        foreach (var tool in provider.Tools) _tools[tool.Name.Value] = provider;
        _ordered.Add(provider);
        return this;
    }

    public AdapterRegistry AddModel(IModelInference model) => Add(_models, model, model.Descriptor, AdapterKind.ModelInference);
    public AdapterRegistry AddExecutionEnvironment(IExecutionEnvironment env) => Add(_environments, env, env.Descriptor, AdapterKind.ExecutionEnvironment);
    public AdapterRegistry AddPlatform(IPlatformIntegration platform) => Add(_platforms, platform, platform.Descriptor, AdapterKind.Platform);

    public IAgent? Resolve(AgentId id) => _agents.GetValueOrDefault(id.Value);
    public IToolProvider? Resolve(ToolName name) => _tools.GetValueOrDefault(name.Value);
    public IModelInference? GetModel(string id) => _models.GetValueOrDefault(id);
    public IExecutionEnvironment? GetExecutionEnvironment(string id) => _environments.GetValueOrDefault(id);
    public IPlatformIntegration? GetPlatform(string id) => _platforms.GetValueOrDefault(id);

    /// <summary>Capability report of every registered adapter that describes itself with an <see cref="AdapterDescriptor"/>.</summary>
    public IReadOnlyList<AdapterDescriptor> Describe()
        => [.. _models.Values.Select(m => m.Descriptor), .. _environments.Values.Select(e => e.Descriptor), .. _platforms.Values.Select(p => p.Descriptor),
            .. _agents.Values.Select(a => new AdapterDescriptor(a.Descriptor.Id.Value, AdapterKind.Agent, "n/a", a.Descriptor.Capabilities)),
            .. _tools.Values.Distinct().SelectMany(p => p.Tools).Select(t => new AdapterDescriptor(t.Name.Value, AdapterKind.Tool, "n/a", []))];

    /// <summary>Initializes adapters that implement <see cref="IAdapterLifecycle"/>, in registration order. All-or-nothing.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_started) throw new AdapterRegistrationException("The registry was already initialized.");
        _started = true;
        foreach (var adapter in _ordered.Distinct())
        {
            if (adapter is not IAdapterLifecycle lifecycle) continue;
            try
            {
                await lifecycle.InitializeAsync(cancellationToken);
                _initialized.Add(lifecycle);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await DisposeInitializedAsync();
                throw new AdapterInitializationException(adapter.GetType().Name, ex);
            }
            catch
            {
                await DisposeInitializedAsync();
                throw;
            }
        }
    }

    /// <summary>Disposes initialized adapters in reverse order. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisposeInitializedAsync();
    }

    private async ValueTask DisposeInitializedAsync()
    {
        for (var i = _initialized.Count - 1; i >= 0; i--)
        {
            try { await ((IAdapterLifecycle)_initialized[i]).DisposeAsync(); }
            catch { /* disposal is best effort so one adapter cannot leak the others */ }
        }
        _initialized.Clear();
    }

    private AdapterRegistry Add<T>(Dictionary<string, T> map, T adapter, AdapterDescriptor descriptor, AdapterKind expected) where T : notnull
    {
        Guard();
        var errors = descriptor.Validate().ToList();
        if (descriptor.Kind != expected) errors.Add($"Descriptor kind must be {expected} but was {descriptor.Kind}.");
        if (errors.Count > 0) throw new AdapterRegistrationException($"Invalid adapter descriptor: {string.Join(" ", errors)}");
        if (!map.TryAdd(descriptor.Id, adapter)) throw new AdapterRegistrationException($"{expected} adapter '{descriptor.Id}' is already registered.");
        _ordered.Add(adapter);
        return this;
    }

    private void Guard()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdapterRegistry));
        if (_started) throw new AdapterRegistrationException("Adapters must be registered before initialization.");
    }
}
