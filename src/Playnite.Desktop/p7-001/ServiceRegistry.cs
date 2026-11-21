namespace Playnite.Desktop.Bootstrap;

/// <summary>
/// Minimal singleton service registry for the desktop composition root. It is an instance,
/// never a global: callers receive it explicitly and register factory delegates, so the root
/// keeps control of construction order and dependencies stay constructor-injected.
/// </summary>
public sealed class ServiceRegistry
{
    private readonly Dictionary<Type, Func<ServiceRegistry, object>> factories = [];
    private readonly Dictionary<Type, object> instances = [];

    public void AddSingleton<TService>(Func<ServiceRegistry, TService> factory)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        var type = typeof(TService);
        if (!factories.TryAdd(type, registry => factory(registry)))
        {
            throw new InvalidOperationException($"Service {type.FullName} is already registered.");
        }
    }

    public TService GetRequiredService<TService>()
        where TService : class
    {
        var type = typeof(TService);
        if (instances.TryGetValue(type, out var existing))
        {
            return (TService)existing;
        }

        if (!factories.TryGetValue(type, out var factory))
        {
            throw new InvalidOperationException($"Service {type.FullName} is not registered.");
        }

        var instance = factory(this)
            ?? throw new InvalidOperationException($"The factory for {type.FullName} returned null.");
        instances.Add(type, instance);
        return (TService)instance;
    }
}
