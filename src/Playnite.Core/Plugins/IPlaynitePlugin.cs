namespace Playnite.Core.Plugins;

/// <summary>Isolated per-plugin key/value setting store provided through <see cref="PluginContext"/>.</summary>
public interface IPluginSettingsStore
{
    /// <summary>Reads a setting value for one plugin, or null when unset.</summary>
    string? Get(string pluginId, string key);

    /// <summary>Writes or clears a setting value for one plugin.</summary>
    void Set(string pluginId, string key, string? value);

    /// <summary>Returns a snapshot of all stored settings for one plugin.</summary>
    IReadOnlyDictionary<string, string> GetAll(string pluginId);
}

/// <summary>Thread-safe in-memory <see cref="IPluginSettingsStore"/> implementation.</summary>
public sealed class InMemoryPluginSettingsStore : IPluginSettingsStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> values = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string? Get(string pluginId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (gate)
        {
            return values.TryGetValue(pluginId, out var pluginMap) && pluginMap.TryGetValue(key, out var value)
                ? value
                : null;
        }
    }

    /// <inheritdoc/>
    public void Set(string pluginId, string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (gate)
        {
            if (value is null)
            {
                if (values.TryGetValue(pluginId, out var existing))
                {
                    existing.Remove(key);
                }

                return;
            }

            if (!values.TryGetValue(pluginId, out var pluginMap))
            {
                pluginMap = new Dictionary<string, string>(StringComparer.Ordinal);
                values[pluginId] = pluginMap;
            }

            pluginMap[key] = value;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> GetAll(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (gate)
        {
            return values.TryGetValue(pluginId, out var pluginMap)
                ? new Dictionary<string, string>(pluginMap, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}

/// <summary>Narrow host context handed to a plugin once during <see cref="IPlaynitePlugin.Setup"/>.</summary>
public sealed class PluginContext
{
    private readonly IPluginSettingsStore settingsStore;
    private readonly Action<string, string>? log;

    /// <summary>Creates a plugin setup context.</summary>
    public PluginContext(
        string pluginId,
        PluginSurface activeSurface,
        TimeProvider timeProvider,
        IPluginSettingsStore settingsStore,
        Action<string, string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(settingsStore);
        PluginId = pluginId;
        ActiveSurface = activeSurface;
        TimeProvider = timeProvider;
        this.settingsStore = settingsStore;
        this.log = log;
    }

    /// <summary>Gets the identifier of the plugin being initialized.</summary>
    public string PluginId { get; }

    /// <summary>Gets the active host surface mask.</summary>
    public PluginSurface ActiveSurface { get; }

    /// <summary>Gets the host time provider.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Reads a persisted setting value for this plugin.</summary>
    public string? GetSetting(string key) => settingsStore.Get(PluginId, key);

    /// <summary>Writes or clears a persisted setting value for this plugin.</summary>
    public void SetSetting(string key, string? value) => settingsStore.Set(PluginId, key, value);

    /// <summary>Emits a scoped diagnostic message to the host log sink.</summary>
    public void Log(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            log?.Invoke(PluginId, message);
        }
    }
}

/// <summary>Flat capability map returned once by <see cref="IPlaynitePlugin.Setup"/>.</summary>
public sealed class PluginRegistration
{
    /// <summary>Gets the library sources contributed by the plugin.</summary>
    public IReadOnlyList<ILibrarySourceCapability> LibrarySources { get; init; } = [];

    /// <summary>Gets the launch capabilities contributed by the plugin.</summary>
    public IReadOnlyList<ILaunchCapability> LaunchProviders { get; init; } = [];

    /// <summary>Gets the library sync providers contributed by the plugin.</summary>
    public IReadOnlyList<ILibrarySyncCapability> SyncProviders { get; init; } = [];

    /// <summary>Gets the command providers contributed by the plugin.</summary>
    public IReadOnlyList<ICommandCapability> CommandProviders { get; init; } = [];

    /// <summary>Gets the event hook handlers contributed by the plugin.</summary>
    public IReadOnlyList<IEventHookCapability> EventHooks { get; init; } = [];

    /// <summary>Gets the declarative settings schema for the plugin, if any.</summary>
    public IPluginSettingsCapability? Settings { get; init; }

    /// <summary>Gets an optional disposal callback invoked when the host shuts down.</summary>
    public Action? OnDispose { get; init; }
}

/// <summary>Unified contract for built-in and dynamically loaded in-process Playnite plugins.</summary>
public interface IPlaynitePlugin
{
    /// <summary>Gets the static descriptor identifying the plugin and its capabilities.</summary>
    PluginDescriptor Descriptor { get; }

    /// <summary>Initializes the plugin once and returns its capability map.</summary>
    PluginRegistration Setup(PluginContext context);
}
