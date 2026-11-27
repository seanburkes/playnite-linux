using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Playnite.Core.Addons;

namespace Playnite.Core.Plugins;

/// <summary>
/// In-process plugin host supporting built-in zero-reflection registration, manifest-driven
/// lazy activation for external assemblies, surface filtering, and O(1) indexed capability lookup.
/// </summary>
public sealed class PluginHost : IDisposable
{
    private const int MaxDiscoveryDepth = AddonInstallLayout.MaxDiscoveryDepth;

    private readonly Lock gate = new();
    private readonly PluginSurface activeSurface;
    private readonly TimeProvider timeProvider;
    private readonly IPluginSettingsStore settingsStore;
    private readonly Action<string, string>? log;

    private readonly Dictionary<string, PluginEntry> entriesById = new(StringComparer.Ordinal);
    private readonly List<string> registrationOrder = [];
    private readonly List<PluginDiagnostic> diagnostics = [];

    private readonly Dictionary<Guid, (string PluginId, LibrarySourceInfo Source)> sourceIndex = [];
    private readonly Dictionary<Guid, string> lazySourceOwners = [];
    private readonly Dictionary<Guid, (string PluginId, ILaunchCapability Capability)> launchIndex = [];
    private readonly Dictionary<Guid, string> lazyLaunchOwners = [];
    private readonly Dictionary<string, (string OwnerPluginId, ILibrarySyncCapability Capability)> syncIndex =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PluginId, PluginCommandDescriptor Descriptor, ICommandCapability Capability)> commandIndex =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string PluginId, IEventHookCapability Hook)>> eventIndex =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPluginSettingsCapability> settingsIndex =
        new(StringComparer.Ordinal);

    private bool disposed;

    /// <summary>Creates a plugin host scoped to <paramref name="activeSurface"/>.</summary>
    public PluginHost(
        PluginSurface activeSurface = PluginSurface.Core,
        TimeProvider? timeProvider = null,
        IPluginSettingsStore? settingsStore = null,
        Action<string, string>? log = null)
    {
        if (activeSurface == PluginSurface.None)
        {
            throw new ArgumentException("At least one active surface must be specified.", nameof(activeSurface));
        }

        this.activeSurface = activeSurface;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.settingsStore = settingsStore ?? new InMemoryPluginSettingsStore();
        this.log = log;
    }

    /// <summary>Gets the active host surface mask.</summary>
    public PluginSurface ActiveSurface => activeSurface;

    /// <summary>Gets whether this host has been disposed.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (gate)
            {
                return disposed;
            }
        }
    }

    /// <summary>Gets all diagnostics recorded by this host.</summary>
    public IReadOnlyList<PluginDiagnostic> Diagnostics
    {
        get
        {
            lock (gate)
            {
                return diagnostics.ToArray();
            }
        }
    }

    /// <summary>Gets snapshots of all registered plugins in deterministic registration order.</summary>
    public IReadOnlyList<PluginRecord> GetRecords()
    {
        lock (gate)
        {
            var records = new List<PluginRecord>(registrationOrder.Count);
            foreach (var id in registrationOrder)
            {
                var entry = entriesById[id];
                records.Add(new PluginRecord(
                    entry.Descriptor,
                    entry.State,
                    entry.Instance is not null,
                    entry.LastError));
            }

            return records;
        }
    }

    /// <summary>Registers a built-in plugin instance directly in-process.</summary>
    public bool RegisterBuiltIn(IPlaynitePlugin plugin, bool activateImmediately = true)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var descriptor = plugin.Descriptor;
        ValidateSurfaceAndId(descriptor);

        PluginEntry entry;
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureUniqueIdAndSourceLocked(descriptor);

            entry = new PluginEntry(descriptor, () => plugin)
            {
                Instance = plugin,
                State = descriptor.EnabledByDefault ? PluginLifecycleState.Registered : PluginLifecycleState.Disabled,
            };
            entriesById.Add(descriptor.Id, entry);
            registrationOrder.Add(descriptor.Id);

            if (!descriptor.EnabledByDefault || !activateImmediately)
            {
                RegisterLazyOwnersLocked(entry);
                return true;
            }
        }

        return ActivateEntry(entry);
    }

    /// <summary>Registers a lazy plugin factory without materializing the plugin until needed.</summary>
    public void RegisterLazy(PluginDescriptor descriptor, Func<IPlaynitePlugin> factory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(factory);
        ValidateSurfaceAndId(descriptor);

        lock (gate)
        {
            ThrowIfDisposed();
            EnsureUniqueIdAndSourceLocked(descriptor);

            var entry = new PluginEntry(descriptor, factory)
            {
                State = descriptor.EnabledByDefault ? PluginLifecycleState.Registered : PluginLifecycleState.Disabled,
            };
            entriesById.Add(descriptor.Id, entry);
            registrationOrder.Add(descriptor.Id);
            RegisterLazyOwnersLocked(entry);
        }
    }

    /// <summary>
    /// Scans <paramref name="pluginsRoot"/> for <c>plugin.json</c> (preferred) or <c>extension.yaml</c>
    /// manifests and registers matching-surface plugins lazily without loading their assemblies.
    /// </summary>
    public PluginDiscoveryResult DiscoverExternal(string pluginsRoot, IRegularFileProbe regularFileProbe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        ArgumentNullException.ThrowIfNull(regularFileProbe);

        lock (gate)
        {
            ThrowIfDisposed();
        }

        var discovered = new List<PluginDescriptor>();
        var discoveryDiagnostics = new List<PluginDiagnostic>();
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRoot));
        if (!Directory.Exists(fullRoot))
        {
            return new PluginDiscoveryResult(discovered, discoveryDiagnostics);
        }

        var rootInfo = new DirectoryInfo(fullRoot);
        if (IsSymlinkOrReparsePoint(rootInfo))
        {
            var rootDiagnostic = new PluginDiagnostic(
                string.Empty,
                "unsafe-plugins-root",
                $"Refusing symlinked plugins root '{fullRoot}'.");
            discoveryDiagnostics.Add(rootDiagnostic);
            lock (gate)
            {
                if (!disposed)
                {
                    diagnostics.Add(rootDiagnostic);
                }
            }
            return new PluginDiscoveryResult(discovered, discoveryDiagnostics);
        }

        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((rootInfo, 0));

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            if (depth > 0 && IsSymlinkOrReparsePoint(directory))
            {
                continue;
            }

            FileInfo[] files;
            DirectoryInfo[] children;
            try
            {
                files = directory.GetFiles();
                children = depth < MaxDiscoveryDepth ? directory.GetDirectories() : [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                discoveryDiagnostics.Add(new PluginDiagnostic(
                    string.Empty,
                    "discovery-read-failed",
                    $"Could not enumerate plugin directory '{directory.FullName}': {exception.Message}"));
                continue;
            }

            if (depth > 0)
            {
                var v2Manifest = files.FirstOrDefault(file =>
                    string.Equals(file.Name, PluginManifestParser.PluginManifestFileName, StringComparison.Ordinal));
                var legacyManifest = files.FirstOrDefault(file =>
                    string.Equals(file.Name, PluginManifestParser.LegacyExtensionManifestFileName, StringComparison.Ordinal));

                if (v2Manifest is not null || legacyManifest is not null)
                {
                    var manifestFile = v2Manifest ?? legacyManifest!;
                    var isV2 = v2Manifest is not null;
                    DiscoverManifestDirectory(directory.FullName, manifestFile, isV2, regularFileProbe, discovered, discoveryDiagnostics);
                    continue;
                }
            }

            foreach (var child in children.OrderBy(item => item.Name, StringComparer.Ordinal).Reverse())
            {
                if (!IsSymlinkOrReparsePoint(child))
                {
                    pending.Push((child, depth + 1));
                }
            }
        }

        if (discoveryDiagnostics.Count > 0)
        {
            lock (gate)
            {
                if (!disposed)
                {
                    diagnostics.AddRange(discoveryDiagnostics);
                }
            }
        }

        return new PluginDiscoveryResult(discovered, discoveryDiagnostics);
    }

    /// <summary>Materializes and activates a registered or faulted plugin by identifier.</summary>
    public bool Activate(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        PluginEntry entry;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!entriesById.TryGetValue(pluginId, out var found))
            {
                return false;
            }

            if (found.State == PluginLifecycleState.Active)
            {
                return true;
            }

            if (found.State == PluginLifecycleState.Disabled)
            {
                return false;
            }

            entry = found;
        }

        return ActivateEntry(entry);
    }

    /// <summary>Enables and activates a disabled, registered, or faulted plugin.</summary>
    public bool Enable(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        PluginEntry entry;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!entriesById.TryGetValue(pluginId, out var found))
            {
                return false;
            }

            if (found.State == PluginLifecycleState.Active)
            {
                return true;
            }

            if (found.State == PluginLifecycleState.Disabled)
            {
                found.State = PluginLifecycleState.Registered;
            }

            entry = found;
        }

        return ActivateEntry(entry);
    }

    /// <summary>Disables a plugin and removes its capabilities from all host lookup indexes.</summary>
    public bool Disable(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (gate)
        {
            ThrowIfDisposed();
            if (!entriesById.TryGetValue(pluginId, out var entry))
            {
                return false;
            }

            RemoveLazyOwnersLocked(entry);
            RemoveIndexedCapabilitiesLocked(entry);
            entry.State = PluginLifecycleState.Disabled;
            return true;
        }
    }

    /// <summary>Looks up a library source by <paramref name="sourceId"/>, activating lazy owners on first access.</summary>
    public bool TryGetSource(Guid sourceId, out LibrarySourceInfo source)
    {
        if (sourceId == Guid.Empty)
        {
            source = null!;
            return false;
        }

        PluginEntry? lazyOwner = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (sourceIndex.TryGetValue(sourceId, out var found))
            {
                source = found.Source;
                return true;
            }

            if (lazySourceOwners.TryGetValue(sourceId, out var ownerId)
                && entriesById.TryGetValue(ownerId, out var entry)
                && entry.State == PluginLifecycleState.Registered)
            {
                lazyOwner = entry;
            }
        }

        if (lazyOwner is not null)
        {
            ActivateEntry(lazyOwner);
            lock (gate)
            {
                ThrowIfDisposed();
                if (sourceIndex.TryGetValue(sourceId, out var found))
                {
                    source = found.Source;
                    return true;
                }
            }
        }

        source = null!;
        return false;
    }

    /// <summary>
    /// Returns library source descriptors from active plugins plus unmaterialized lazy library
    /// manifests without loading any external assemblies.
    /// </summary>
    public IReadOnlyList<LibrarySourceInfo> GetDeclaredSources()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var sources = new List<LibrarySourceInfo>();
            foreach (var id in registrationOrder)
            {
                var entry = entriesById[id];
                if (entry.State == PluginLifecycleState.Active && entry.Registration is not null)
                {
                    foreach (var cap in entry.Registration.LibrarySources)
                    {
                        if (cap.Source.SourceId != Guid.Empty)
                        {
                            sources.Add(cap.Source);
                        }
                    }
                }
                else if (entry.State == PluginLifecycleState.Registered
                    && entry.Descriptor.SourceId != Guid.Empty
                    && entry.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.LibrarySource))
                {
                    sources.Add(new LibrarySourceInfo(
                        entry.Descriptor.SourceId,
                        entry.Descriptor.Name,
                        entry.Descriptor.Name,
                        SupportsSync: entry.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.LibrarySync)));
                }
            }

            return sources;
        }
    }

    /// <summary>Returns all active library sources, materializing enabled lazy library plugins.</summary>
    public IReadOnlyList<LibrarySourceInfo> GetSources()
    {
        var pending = SnapshotLazyEntries(PluginCapabilityKind.LibrarySource);
        foreach (var entry in pending)
        {
            ActivateEntry(entry);
        }

        lock (gate)
        {
            ThrowIfDisposed();
            return registrationOrder
                .Select(id => entriesById[id])
                .Where(entry => entry.State == PluginLifecycleState.Active && entry.Registration is not null)
                .SelectMany(entry => entry.Registration!.LibrarySources.Select(cap => cap.Source))
                .Where(source => source.SourceId != Guid.Empty)
                .ToArray();
        }
    }

    /// <summary>Looks up a launch capability by <paramref name="sourceId"/>, activating lazy owners on first access.</summary>
    public bool TryGetLaunchProvider(Guid sourceId, out ILaunchCapability capability)
    {
        if (sourceId == Guid.Empty)
        {
            capability = null!;
            return false;
        }

        PluginEntry? lazyOwner = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (launchIndex.TryGetValue(sourceId, out var found))
            {
                capability = found.Capability;
                return true;
            }

            if (lazyLaunchOwners.TryGetValue(sourceId, out var ownerId)
                && entriesById.TryGetValue(ownerId, out var entry)
                && entry.State == PluginLifecycleState.Registered)
            {
                lazyOwner = entry;
            }
        }

        if (lazyOwner is not null)
        {
            ActivateEntry(lazyOwner);
            lock (gate)
            {
                ThrowIfDisposed();
                if (launchIndex.TryGetValue(sourceId, out var found))
                {
                    capability = found.Capability;
                    return true;
                }
            }
        }

        capability = null!;
        return false;
    }

    /// <summary>Looks up a library sync provider by identifier, activating a lazy owner if needed.</summary>
    public bool TryGetSyncProvider(string pluginId, out ILibrarySyncCapability capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        PluginEntry? lazyTarget = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (syncIndex.TryGetValue(pluginId, out var found))
            {
                capability = found.Capability;
                return true;
            }

            if (entriesById.TryGetValue(pluginId, out var entry)
                && entry.State == PluginLifecycleState.Registered
                && entry.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.LibrarySync))
            {
                lazyTarget = entry;
            }
        }

        if (lazyTarget is not null)
        {
            ActivateEntry(lazyTarget);
            lock (gate)
            {
                ThrowIfDisposed();
                if (syncIndex.TryGetValue(pluginId, out var found))
                {
                    capability = found.Capability;
                    return true;
                }
            }
        }

        // Fallback if a lazy LibrarySync plugin uses a custom SyncProvider.PluginId != Descriptor.Id.
        var pendingSyncs = SnapshotLazyEntries(PluginCapabilityKind.LibrarySync);
        foreach (var pending in pendingSyncs)
        {
            ActivateEntry(pending);
            lock (gate)
            {
                ThrowIfDisposed();
                if (syncIndex.TryGetValue(pluginId, out var found))
                {
                    capability = found.Capability;
                    return true;
                }
            }
        }

        capability = null!;
        return false;
    }

    /// <summary>Returns all active library sync providers, activating pending lazy sync plugins.</summary>
    public IReadOnlyList<ILibrarySyncCapability> GetSyncProviders()
    {
        var pending = SnapshotLazyEntries(PluginCapabilityKind.LibrarySync);
        foreach (var entry in pending)
        {
            ActivateEntry(entry);
        }

        lock (gate)
        {
            ThrowIfDisposed();
            return registrationOrder
                .Select(id => entriesById[id])
                .Where(entry => entry.State == PluginLifecycleState.Active && entry.Registration is not null)
                .SelectMany(entry => entry.Registration!.SyncProviders)
                .ToArray();
        }
    }

    /// <summary>Executes a registered library sync provider and publishes <see cref="PluginEvents.LibrarySynced"/>.</summary>
    public async Task<PluginSyncResult> RunSyncAsync(
        string pluginId,
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetSyncProvider(pluginId, out var capability))
        {
            return new PluginSyncResult(
                pluginId,
                Succeeded: false,
                Reconciled: false,
                Added: 0,
                Updated: 0,
                Removed: 0,
                Unchanged: 0,
                Skipped: 0,
                Summary: $"Sync provider '{pluginId}' is not available.",
                Issues: ["sync-provider-unavailable"]);
        }

        string ownerPluginId;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!syncIndex.TryGetValue(pluginId, out var entry)
                || !entriesById.TryGetValue(entry.OwnerPluginId, out var pluginEntry)
                || pluginEntry.State != PluginLifecycleState.Active)
            {
                return new PluginSyncResult(
                    pluginId,
                    Succeeded: false,
                    Reconciled: false,
                    Added: 0,
                    Updated: 0,
                    Removed: 0,
                    Unchanged: 0,
                    Skipped: 0,
                    Summary: $"Sync provider '{pluginId}' is not available.",
                    Issues: ["sync-provider-unavailable"]);
            }

            ownerPluginId = entry.OwnerPluginId;
            capability = entry.Capability;
        }

        PluginSyncResult result;
        try
        {
            result = await capability.SyncAsync(databasePath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Sync provider '{pluginId}' returned null.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordRuntimeFault(ownerPluginId, "sync-failed", $"Sync '{pluginId}' failed: {exception.Message}");
            return new PluginSyncResult(
                pluginId,
                Succeeded: false,
                Reconciled: false,
                Added: 0,
                Updated: 0,
                Removed: 0,
                Unchanged: 0,
                Skipped: 0,
                Summary: $"{capability.DisplayName} sync failed: {exception.Message}",
                Issues: [exception.Message]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (result.Succeeded)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pluginId"] = string.IsNullOrWhiteSpace(result.PluginId) ? pluginId : result.PluginId,
                ["succeeded"] = "true",
                ["added"] = result.Added.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["updated"] = result.Updated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["removed"] = result.Removed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reconciled"] = result.Reconciled ? "true" : "false",
            };
            try
            {
                await PublishEventAsync(
                    new PluginEvent(PluginEvents.LibrarySynced, timeProvider.GetUtcNow(), Payload: payload),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException exception)
            {
                log?.Invoke(ownerPluginId, $"Skipping library.synced event after host disposal ({exception.GetType().Name}).");
            }
        }

        return result;
    }

    /// <summary>Returns active plugin commands, activating any pending lazy command plugins.</summary>
    public IReadOnlyList<PluginCommandDescriptor> GetCommands(PluginCommandPlacement? placement = null)
    {
        var pending = SnapshotLazyEntries(PluginCapabilityKind.Commands);
        foreach (var entry in pending)
        {
            ActivateEntry(entry);
        }

        lock (gate)
        {
            ThrowIfDisposed();
            var items = commandIndex.Values.Select(item => item.Descriptor);
            if (placement is { } filter)
            {
                items = items.Where(descriptor => descriptor.Placement == filter);
            }

            return items.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Executes an indexed plugin command by identifier.</summary>
    public async Task<PluginCommandResult> ExecuteCommandAsync(
        string commandId,
        PluginCommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var pending = SnapshotLazyEntries(PluginCapabilityKind.Commands);
        foreach (var lazyEntry in pending)
        {
            ActivateEntry(lazyEntry);
        }

        string pluginId;
        ICommandCapability capability;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!commandIndex.TryGetValue(commandId, out var registered))
            {
                return new PluginCommandResult(false, $"Plugin command '{commandId}' is not available.");
            }

            pluginId = registered.PluginId;
            capability = registered.Capability;
        }

        try
        {
            return await capability.ExecuteAsync(commandId, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordRuntimeFault(pluginId, "command-failed", $"Command '{commandId}' failed: {exception.Message}");
            return new PluginCommandResult(false, exception.Message);
        }
    }

    /// <summary>Returns the declarative settings schema declared by one active plugin.</summary>
    public IReadOnlyList<PluginSettingField> GetSettingsSchema(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        PluginEntry? lazyTarget = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (settingsIndex.TryGetValue(pluginId, out var settings))
            {
                return settings.Fields;
            }

            if (entriesById.TryGetValue(pluginId, out var entry)
                && entry.State == PluginLifecycleState.Registered
                && entry.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.Settings))
            {
                lazyTarget = entry;
            }
        }

        if (lazyTarget is not null)
        {
            ActivateEntry(lazyTarget);
            lock (gate)
            {
                ThrowIfDisposed();
                return settingsIndex.TryGetValue(pluginId, out var settings)
                    ? settings.Fields
                    : [];
            }
        }

        return [];
    }

    /// <summary>
    /// Publishes an event to indexed subscribers. Events with zero subscribers and no pending lazy
    /// hook plugins return <see cref="Task.CompletedTask"/> synchronously without allocating.
    /// </summary>
    public Task PublishEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pluginEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginEvent.Name);
        cancellationToken.ThrowIfCancellationRequested();

        var pending = SnapshotLazyEntries(PluginCapabilityKind.EventHooks);
        foreach (var lazyEntry in pending)
        {
            ActivateEntry(lazyEntry);
        }

        (string PluginId, IEventHookCapability Hook)[] subscribers;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!eventIndex.TryGetValue(pluginEvent.Name, out var list) || list.Count == 0)
            {
                return Task.CompletedTask;
            }

            subscribers = list.ToArray();
        }

        return DispatchEventAsync(subscribers, pluginEvent, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        List<PluginEntry> toDispose;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            toDispose = registrationOrder.Select(id => entriesById[id]).ToList();
            entriesById.Clear();
            registrationOrder.Clear();
            sourceIndex.Clear();
            lazySourceOwners.Clear();
            launchIndex.Clear();
            lazyLaunchOwners.Clear();
            syncIndex.Clear();
            commandIndex.Clear();
            eventIndex.Clear();
            settingsIndex.Clear();
        }

        foreach (var entry in toDispose)
        {
            lock (entry.ActivationGate)
            {
                DisposeEntryArtifacts(entry, markDisposed: true);
            }
        }
    }

    private IReadOnlyList<PluginEntry> SnapshotLazyEntries(PluginCapabilityKind capability)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            List<PluginEntry>? pending = null;
            foreach (var id in registrationOrder)
            {
                var entry = entriesById[id];
                if (entry.State == PluginLifecycleState.Registered
                    && entry.Descriptor.Capabilities.HasFlag(capability))
                {
                    pending ??= [];
                    pending.Add(entry);
                }
            }

            return pending ?? (IReadOnlyList<PluginEntry>)[];
        }
    }

    private void DiscoverManifestDirectory(
        string directoryPath,
        FileInfo manifestFile,
        bool isV2,
        IRegularFileProbe regularFileProbe,
        List<PluginDescriptor> discovered,
        List<PluginDiagnostic> discoveryDiagnostics)
    {
        if (HasSymlinkSegment(directoryPath, manifestFile.FullName)
            || !regularFileProbe.IsRegularFile(manifestFile.FullName))
        {
            discoveryDiagnostics.Add(new PluginDiagnostic(
                string.Empty,
                "unsafe-manifest-file",
                $"Refusing non-regular or symlinked manifest '{manifestFile.FullName}'."));
            return;
        }

        string content;
        try
        {
            if (!TryReadBoundedText(manifestFile.FullName, PluginManifestParser.MaxManifestBytes, out content, out var readError))
            {
                discoveryDiagnostics.Add(new PluginDiagnostic(
                    string.Empty,
                    readError == "size-limit" ? PluginManifestParser.InvalidManifestCode : "manifest-read-failed",
                    readError == "size-limit"
                        ? $"{manifestFile.Name}: manifest exceeds the {PluginManifestParser.MaxManifestBytes}-byte limit."
                        : $"Could not read '{manifestFile.FullName}': {readError}"));
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            discoveryDiagnostics.Add(new PluginDiagnostic(
                string.Empty,
                "manifest-read-failed",
                $"Could not read '{manifestFile.FullName}': {exception.Message}"));
            return;
        }

        var (descriptor, parseDiagnostics) = isV2
            ? PluginManifestParser.ParseV2Manifest(content, directoryPath, manifestFile.Name)
            : PluginManifestParser.ParseLegacyExtensionManifest(content, directoryPath, manifestFile.Name);

        if (descriptor is null || parseDiagnostics.Count > 0)
        {
            discoveryDiagnostics.AddRange(parseDiagnostics);
            return;
        }

        var matchingSurfaces = descriptor.Surfaces & activeSurface;
        if (matchingSurfaces == 0)
        {
            return;
        }

        var hasCodeSurface = (matchingSurfaces & (PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen)) != 0;
        if (!hasCodeSurface)
        {
            // Theme-only packages declare a .axaml resource dictionary rather than a .NET assembly;
            // register their descriptor without invoking AssemblyLoadContext.
            var themePath = Path.GetFullPath(Path.Combine(directoryPath, descriptor.EntryPoints.Theme!.Replace('/', Path.DirectorySeparatorChar)));
            if (!AddonInstallLayout.IsWithin(directoryPath, themePath)
                || HasSymlinkSegment(directoryPath, themePath)
                || !File.Exists(themePath)
                || !regularFileProbe.IsRegularFile(themePath))
            {
                discoveryDiagnostics.Add(new PluginDiagnostic(
                    descriptor.Id,
                    "missing-entry-point",
                    $"Theme '{descriptor.Id}' entry point '{themePath}' is missing or not a regular file."));
                return;
            }

            RegisterDiscoveredEntry(
                descriptor,
                () => new ThemeDescriptorPlugin(descriptor),
                discovered,
                discoveryDiagnostics);
            return;
        }

        var relativeEntry = SelectCodeEntryPointForSurface(descriptor, matchingSurfaces);
        if (relativeEntry is null)
        {
            return;
        }

        var entryFullPath = Path.GetFullPath(Path.Combine(directoryPath, relativeEntry.Replace('/', Path.DirectorySeparatorChar)));
        if (!AddonInstallLayout.IsWithin(directoryPath, entryFullPath))
        {
            discoveryDiagnostics.Add(new PluginDiagnostic(
                descriptor.Id,
                AddonManifestValidator.UnsafeModuleCode,
                $"Entry point '{relativeEntry}' escapes plugin directory '{directoryPath}'."));
            return;
        }

        if (HasSymlinkSegment(directoryPath, entryFullPath)
            || !File.Exists(entryFullPath)
            || !regularFileProbe.IsRegularFile(entryFullPath))
        {
            discoveryDiagnostics.Add(new PluginDiagnostic(
                descriptor.Id,
                "missing-entry-point",
                $"Plugin '{descriptor.Id}' entry point '{entryFullPath}' is missing or not a regular file."));
            return;
        }

        RegisterDiscoveredEntry(
            descriptor,
            () => LoadExternalPlugin(descriptor, directoryPath, entryFullPath, regularFileProbe),
            discovered,
            discoveryDiagnostics);
    }

    private void RegisterDiscoveredEntry(
        PluginDescriptor descriptor,
        Func<IPlaynitePlugin> factory,
        List<PluginDescriptor> discovered,
        List<PluginDiagnostic> discoveryDiagnostics)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (entriesById.ContainsKey(descriptor.Id))
            {
                discoveryDiagnostics.Add(new PluginDiagnostic(
                    descriptor.Id,
                    "duplicate-plugin-id",
                    $"Plugin '{descriptor.Id}' is already registered."));
                return;
            }

            if (FindSourceConflictLocked(descriptor) is { } conflictOwner)
            {
                discoveryDiagnostics.Add(new PluginDiagnostic(
                    descriptor.Id,
                    "duplicate-source-id",
                    $"Plugin '{descriptor.Id}' conflicts with source '{descriptor.SourceId}' already owned by '{conflictOwner}'."));
                return;
            }

            var entry = new PluginEntry(descriptor, factory)
            {
                State = descriptor.EnabledByDefault ? PluginLifecycleState.Registered : PluginLifecycleState.Disabled,
            };
            entriesById.Add(descriptor.Id, entry);
            registrationOrder.Add(descriptor.Id);
            RegisterLazyOwnersLocked(entry);
            discovered.Add(descriptor);
        }
    }

    private static bool TryReadBoundedText(
        string path,
        int maxBytes,
        out string content,
        out string? error)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[maxBytes + 1];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead > maxBytes)
        {
            content = string.Empty;
            error = "size-limit";
            return false;
        }

        content = Encoding.UTF8.GetString(buffer, 0, totalRead);
        error = null;
        return true;
    }

    private static bool HasSymlinkSegment(string baseDirectory, string fullTargetPath)
    {
        var normalizedBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var normalizedTarget = Path.GetFullPath(fullTargetPath);
        if (!AddonInstallLayout.IsWithin(normalizedBase, normalizedTarget))
        {
            return true;
        }

        var relative = Path.GetRelativePath(normalizedBase, normalizedTarget);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = normalizedBase;

        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            var isLast = i == segments.Length - 1;
            FileSystemInfo info = isLast && File.Exists(current)
                ? new FileInfo(current)
                : new DirectoryInfo(current);
            if (IsSymlinkOrReparsePoint(info))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSymlinkOrReparsePoint(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0);

    private static string? SelectCodeEntryPointForSurface(PluginDescriptor descriptor, PluginSurface matchingSurfaces)
    {
        if (matchingSurfaces.HasFlag(PluginSurface.Desktop) && descriptor.EntryPoints.Desktop is { } desktop)
        {
            return desktop;
        }

        if (matchingSurfaces.HasFlag(PluginSurface.Fullscreen) && descriptor.EntryPoints.Fullscreen is { } fullscreen)
        {
            return fullscreen;
        }

        if (matchingSurfaces.HasFlag(PluginSurface.Core) && descriptor.EntryPoints.Core is { } core)
        {
            return core;
        }

        if ((matchingSurfaces & (PluginSurface.Desktop | PluginSurface.Fullscreen)) != 0
            && descriptor.EntryPoints.Core is { } fallbackCore)
        {
            return fallbackCore;
        }

        return null;
    }

    private static IPlaynitePlugin LoadExternalPlugin(
        PluginDescriptor descriptor,
        string pluginDirectory,
        string assemblyPath,
        IRegularFileProbe regularFileProbe)
    {
        if (!AddonInstallLayout.IsWithin(pluginDirectory, assemblyPath)
            || HasSymlinkSegment(pluginDirectory, assemblyPath)
            || !File.Exists(assemblyPath)
            || !regularFileProbe.IsRegularFile(assemblyPath))
        {
            throw new InvalidOperationException(
                $"Plugin '{descriptor.Id}' entry point '{assemblyPath}' failed load-time security verification.");
        }

        var loadContext = new PluginAssemblyLoadContext(pluginDirectory, assemblyPath, regularFileProbe);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var pluginType = ResolvePluginType(assembly, descriptor);
            if (Activator.CreateInstance(pluginType) is not IPlaynitePlugin plugin)
            {
                throw new InvalidOperationException($"Type '{pluginType.FullName}' did not instantiate an IPlaynitePlugin.");
            }

            return new ExternalPluginHandle(plugin, loadContext);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private static Type ResolvePluginType(Assembly assembly, PluginDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.EntryPoints.EntryType))
        {
            var explicitType = assembly.GetType(descriptor.EntryPoints.EntryType, throwOnError: false);
            if (explicitType is null || explicitType.IsAbstract || !typeof(IPlaynitePlugin).IsAssignableFrom(explicitType))
            {
                throw new InvalidOperationException(
                    $"Declared entryType '{descriptor.EntryPoints.EntryType}' was not found or does not implement IPlaynitePlugin.");
            }

            return explicitType;
        }

        var candidates = assembly.GetExportedTypes()
            .Where(type => !type.IsAbstract && typeof(IPlaynitePlugin).IsAssignableFrom(type))
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidOperationException(
                $"Plugin assembly '{assembly.GetName().Name}' must export exactly one concrete IPlaynitePlugin when entryType is omitted (found {candidates.Length}).");
        }

        return candidates[0];
    }

    private bool ActivateEntry(PluginEntry entry)
    {
        IPlaynitePlugin? candidateInstance = null;
        PluginAssemblyLoadContext? candidateLoadContext = null;
        PluginRegistration? candidateRegistration = null;
        PluginEntry? staleArtifactsToDispose = null;

        lock (entry.ActivationGate)
        {
            PluginContext context;
            lock (gate)
            {
                if (disposed)
                {
                    return false;
                }

                if (entry.State == PluginLifecycleState.Active)
                {
                    return true;
                }

                if (entry.State == PluginLifecycleState.Disabled)
                {
                    return false;
                }

                if (entry.State == PluginLifecycleState.Faulted)
                {
                    staleArtifactsToDispose = new PluginEntry(entry.Descriptor, entry.Factory)
                    {
                        Instance = entry.Descriptor.LoadKind == PluginLoadKind.ExternalAssembly ? entry.Instance : null,
                        Registration = entry.Registration,
                        LoadContext = entry.LoadContext,
                    };
                    if (entry.Descriptor.LoadKind == PluginLoadKind.ExternalAssembly)
                    {
                        entry.Instance = null;
                    }
                    entry.Registration = null;
                    entry.LoadContext = null;
                    entry.LastError = null;
                    entry.State = PluginLifecycleState.Registered;
                }

                candidateInstance = entry.Instance;
                candidateRegistration = entry.Registration;
                context = new PluginContext(entry.Descriptor.Id, activeSurface, timeProvider, settingsStore, log);
            }

            if (staleArtifactsToDispose is not null)
            {
                DisposeEntryArtifacts(staleArtifactsToDispose, markDisposed: false);
            }

            try
            {
                if (candidateInstance is null)
                {
                    var created = entry.Factory();
                    if (created is ExternalPluginHandle handle)
                    {
                        candidateInstance = handle.Inner;
                        candidateLoadContext = handle.LoadContext;
                    }
                    else
                    {
                        candidateInstance = created;
                    }
                }

                candidateRegistration ??= candidateInstance.Setup(context);
            }
            catch (Exception exception)
            {
                var failedEntry = new PluginEntry(entry.Descriptor, entry.Factory)
                {
                    Instance = entry.Descriptor.LoadKind == PluginLoadKind.ExternalAssembly ? candidateInstance : null,
                    Registration = candidateRegistration,
                    LoadContext = candidateLoadContext,
                };
                DisposeEntryArtifacts(failedEntry, markDisposed: false);

                lock (gate)
                {
                    if (!disposed)
                    {
                        RemoveLazyOwnersLocked(entry);
                        RemoveIndexedCapabilitiesLocked(entry);
                        entry.State = PluginLifecycleState.Faulted;
                        entry.LastError = exception.Message;
                        if (entry.Descriptor.LoadKind == PluginLoadKind.ExternalAssembly)
                        {
                            entry.Instance = null;
                        }
                        entry.Registration = null;
                        entry.LoadContext = null;
                        diagnostics.Add(new PluginDiagnostic(entry.Descriptor.Id, "activation-failed", exception.Message));
                    }
                }

                return false;
            }

            bool committed;
            string? conflictError = null;
            lock (gate)
            {
                if (disposed || entry.State == PluginLifecycleState.Disabled)
                {
                    committed = false;
                }
                else
                {
                    conflictError = ValidateRegistrationConflictsLocked(entry.Descriptor.Id, candidateRegistration);
                    if (conflictError is not null)
                    {
                        committed = false;
                        RemoveLazyOwnersLocked(entry);
                        RemoveIndexedCapabilitiesLocked(entry);
                        entry.State = PluginLifecycleState.Faulted;
                        entry.LastError = conflictError;
                        diagnostics.Add(new PluginDiagnostic(entry.Descriptor.Id, "capability-conflict", conflictError));
                    }
                    else
                    {
                        entry.Instance = candidateInstance;
                        if (candidateLoadContext is not null)
                        {
                            entry.LoadContext = candidateLoadContext;
                        }
                        entry.Registration = candidateRegistration;
                        IndexCapabilitiesLocked(entry);
                        entry.State = PluginLifecycleState.Active;
                        entry.LastError = null;
                        committed = true;
                    }
                }
            }

            if (!committed)
            {
                var rollback = new PluginEntry(entry.Descriptor, entry.Factory)
                {
                    Instance = entry.Descriptor.LoadKind == PluginLoadKind.ExternalAssembly ? candidateInstance : null,
                    Registration = candidateRegistration,
                    LoadContext = candidateLoadContext,
                };
                DisposeEntryArtifacts(rollback, markDisposed: false);
                return false;
            }

            return true;
        }
    }

    private void RegisterLazyOwnersLocked(PluginEntry entry)
    {
        if (!entry.Descriptor.EnabledByDefault || entry.Descriptor.SourceId == Guid.Empty)
        {
            return;
        }

        if (entry.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.LibrarySource))
        {
            lazySourceOwners.TryAdd(entry.Descriptor.SourceId, entry.Descriptor.Id);
        }

        if (entry.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.Launch))
        {
            lazyLaunchOwners.TryAdd(entry.Descriptor.SourceId, entry.Descriptor.Id);
        }
    }

    private void RemoveLazyOwnersLocked(PluginEntry entry)
    {
        if (entry.Descriptor.SourceId == Guid.Empty)
        {
            return;
        }

        if (lazySourceOwners.TryGetValue(entry.Descriptor.SourceId, out var sourceOwner)
            && string.Equals(sourceOwner, entry.Descriptor.Id, StringComparison.Ordinal))
        {
            lazySourceOwners.Remove(entry.Descriptor.SourceId);
        }

        if (lazyLaunchOwners.TryGetValue(entry.Descriptor.SourceId, out var launchOwner)
            && string.Equals(launchOwner, entry.Descriptor.Id, StringComparison.Ordinal))
        {
            lazyLaunchOwners.Remove(entry.Descriptor.SourceId);
        }
    }

    private string? ValidateRegistrationConflictsLocked(string pluginId, PluginRegistration registration)
    {
        var seenSources = new HashSet<Guid>();
        foreach (var library in registration.LibrarySources)
        {
            var id = library.Source.SourceId;
            if (id != Guid.Empty && !seenSources.Add(id))
            {
                return $"Plugin '{pluginId}' declared duplicate SourceId '{id}'.";
            }

            if (id != Guid.Empty && sourceIndex.TryGetValue(id, out var existing)
                && !string.Equals(existing.PluginId, pluginId, StringComparison.Ordinal))
            {
                return $"SourceId '{id}' is already registered by plugin '{existing.PluginId}'.";
            }

            if (id != Guid.Empty && lazySourceOwners.TryGetValue(id, out var lazyOwner)
                && !string.Equals(lazyOwner, pluginId, StringComparison.Ordinal))
            {
                return $"SourceId '{id}' is reserved by lazy plugin '{lazyOwner}'.";
            }
        }

        var seenLaunchers = new HashSet<Guid>();
        foreach (var launch in registration.LaunchProviders)
        {
            var id = launch.SourceId;
            if (id != Guid.Empty && !seenLaunchers.Add(id))
            {
                return $"Plugin '{pluginId}' declared duplicate launch SourceId '{id}'.";
            }

            if (id != Guid.Empty && launchIndex.TryGetValue(id, out var existing)
                && !string.Equals(existing.PluginId, pluginId, StringComparison.Ordinal))
            {
                return $"Launch SourceId '{id}' is already registered by plugin '{existing.PluginId}'.";
            }

            if (id != Guid.Empty && lazyLaunchOwners.TryGetValue(id, out var lazyOwner)
                && !string.Equals(lazyOwner, pluginId, StringComparison.Ordinal))
            {
                return $"Launch SourceId '{id}' is reserved by lazy plugin '{lazyOwner}'.";
            }
        }

        var seenSyncIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in registration.SyncProviders)
        {
            if (string.IsNullOrWhiteSpace(provider.PluginId))
            {
                return $"Plugin '{pluginId}' declared a sync provider with a blank PluginId.";
            }

            if (!seenSyncIds.Add(provider.PluginId))
            {
                return $"Plugin '{pluginId}' declared duplicate sync provider '{provider.PluginId}'.";
            }

            if (syncIndex.TryGetValue(provider.PluginId, out var existing)
                && !string.Equals(existing.OwnerPluginId, pluginId, StringComparison.Ordinal))
            {
                return $"Sync provider '{provider.PluginId}' is already registered by plugin '{existing.OwnerPluginId}'.";
            }

            if (!string.Equals(provider.PluginId, pluginId, StringComparison.Ordinal)
                && entriesById.ContainsKey(provider.PluginId))
            {
                return $"Sync provider '{provider.PluginId}' collides with plugin ID '{provider.PluginId}'.";
            }
        }

        foreach (var provider in registration.CommandProviders)
        {
            foreach (var command in provider.Commands)
            {
                if (commandIndex.TryGetValue(command.Id, out var existing)
                    && !string.Equals(existing.PluginId, pluginId, StringComparison.Ordinal))
                {
                    return $"Command '{command.Id}' is already registered by plugin '{existing.PluginId}'.";
                }
            }
        }

        return null;
    }

    private void IndexCapabilitiesLocked(PluginEntry entry)
    {
        RemoveIndexedCapabilitiesLocked(entry);
        var registration = entry.Registration;
        if (registration is null)
        {
            return;
        }

        foreach (var library in registration.LibrarySources)
        {
            if (library.Source.SourceId != Guid.Empty)
            {
                sourceIndex[library.Source.SourceId] = (entry.Descriptor.Id, library.Source);
                if (lazySourceOwners.TryGetValue(library.Source.SourceId, out var owner)
                    && string.Equals(owner, entry.Descriptor.Id, StringComparison.Ordinal))
                {
                    lazySourceOwners.Remove(library.Source.SourceId);
                }
            }
        }

        foreach (var launch in registration.LaunchProviders)
        {
            if (launch.SourceId != Guid.Empty)
            {
                launchIndex[launch.SourceId] = (entry.Descriptor.Id, launch);
                if (lazyLaunchOwners.TryGetValue(launch.SourceId, out var owner)
                    && string.Equals(owner, entry.Descriptor.Id, StringComparison.Ordinal))
                {
                    lazyLaunchOwners.Remove(launch.SourceId);
                }
            }
        }

        foreach (var provider in registration.SyncProviders)
        {
            if (!string.IsNullOrWhiteSpace(provider.PluginId))
            {
                syncIndex[provider.PluginId] = (entry.Descriptor.Id, provider);
            }
        }

        foreach (var provider in registration.CommandProviders)
        {
            foreach (var command in provider.Commands)
            {
                commandIndex[command.Id] = (entry.Descriptor.Id, command, provider);
            }
        }

        foreach (var hook in registration.EventHooks)
        {
            foreach (var eventName in hook.SubscribedEvents.Distinct(StringComparer.Ordinal))
            {
                if (!eventIndex.TryGetValue(eventName, out var list))
                {
                    list = [];
                    eventIndex[eventName] = list;
                }

                if (!list.Any(item => string.Equals(item.PluginId, entry.Descriptor.Id, StringComparison.Ordinal)
                        && ReferenceEquals(item.Hook, hook)))
                {
                    list.Add((entry.Descriptor.Id, hook));
                }
            }
        }

        if (registration.Settings is not null)
        {
            settingsIndex[entry.Descriptor.Id] = registration.Settings;
        }
    }

    private void RemoveIndexedCapabilitiesLocked(PluginEntry entry)
    {
        if (entry.Registration is not { } registration)
        {
            return;
        }

        foreach (var library in registration.LibrarySources)
        {
            if (sourceIndex.TryGetValue(library.Source.SourceId, out var existing)
                && string.Equals(existing.PluginId, entry.Descriptor.Id, StringComparison.Ordinal))
            {
                sourceIndex.Remove(library.Source.SourceId);
            }
        }

        foreach (var launch in registration.LaunchProviders)
        {
            if (launchIndex.TryGetValue(launch.SourceId, out var existing)
                && string.Equals(existing.PluginId, entry.Descriptor.Id, StringComparison.Ordinal))
            {
                launchIndex.Remove(launch.SourceId);
            }
        }

        foreach (var provider in registration.SyncProviders)
        {
            if (syncIndex.TryGetValue(provider.PluginId, out var existing)
                && string.Equals(existing.OwnerPluginId, entry.Descriptor.Id, StringComparison.Ordinal))
            {
                syncIndex.Remove(provider.PluginId);
            }
        }

        foreach (var provider in registration.CommandProviders)
        {
            foreach (var command in provider.Commands)
            {
                if (commandIndex.TryGetValue(command.Id, out var existing)
                    && string.Equals(existing.PluginId, entry.Descriptor.Id, StringComparison.Ordinal))
                {
                    commandIndex.Remove(command.Id);
                }
            }
        }

        foreach (var hook in registration.EventHooks)
        {
            foreach (var eventName in hook.SubscribedEvents)
            {
                if (eventIndex.TryGetValue(eventName, out var list))
                {
                    list.RemoveAll(item => string.Equals(item.PluginId, entry.Descriptor.Id, StringComparison.Ordinal));
                    if (list.Count == 0)
                    {
                        eventIndex.Remove(eventName);
                    }
                }
            }
        }

        settingsIndex.Remove(entry.Descriptor.Id);
    }

    private async Task DispatchEventAsync(
        (string PluginId, IEventHookCapability Hook)[] subscribers,
        PluginEvent pluginEvent,
        CancellationToken cancellationToken)
    {
        foreach (var (pluginId, hook) in subscribers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (disposed || !entriesById.TryGetValue(pluginId, out var entry) || entry.State != PluginLifecycleState.Active)
                {
                    continue;
                }
            }

            try
            {
                await hook.OnEventAsync(pluginEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordRuntimeFault(pluginId, "event-hook-failed",
                    $"Event '{pluginEvent.Name}' failed: {exception.Message}");
            }
        }
    }

    internal void ReportCapabilityFault(string pluginId, string code, string message) =>
        RecordRuntimeFault(pluginId, code, message);

    private void RecordRuntimeFault(string pluginId, string code, string message)
    {
        PluginEntry? toUnload = null;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            diagnostics.Add(new PluginDiagnostic(pluginId, code, message));
            if (entriesById.TryGetValue(pluginId, out var entry) && entry.State == PluginLifecycleState.Active)
            {
                RemoveLazyOwnersLocked(entry);
                RemoveIndexedCapabilitiesLocked(entry);
                entry.State = PluginLifecycleState.Faulted;
                entry.LastError = message;

                toUnload = new PluginEntry(entry.Descriptor, entry.Factory)
                {
                    Instance = entry.Descriptor.LoadKind == PluginLoadKind.ExternalAssembly ? entry.Instance : null,
                    Registration = entry.Registration,
                    LoadContext = entry.LoadContext,
                };
                if (entry.Descriptor.LoadKind == PluginLoadKind.ExternalAssembly)
                {
                    entry.Instance = null;
                }
                entry.Registration = null;
                entry.LoadContext = null;
            }
        }

        if (toUnload is not null)
        {
            DisposeEntryArtifacts(toUnload, markDisposed: false);
        }
    }

    private void DisposeEntryArtifacts(PluginEntry entry, bool markDisposed)
    {
        if (markDisposed)
        {
            entry.State = PluginLifecycleState.Disposed;
        }

        if (entry.Registration?.OnDispose is { } onDispose)
        {
            try
            {
                onDispose();
            }
            catch (Exception exception)
            {
                RecordDiagnosticSafe(entry.Descriptor.Id, "dispose-failed", exception.Message);
            }
        }

        if (entry.Instance is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception exception)
            {
                RecordDiagnosticSafe(entry.Descriptor.Id, "dispose-failed", exception.Message);
            }
        }

        entry.Registration = null;
        entry.Instance = null;
        entry.LoadContext?.Unload();
        entry.LoadContext = null;
    }

    private void RecordDiagnosticSafe(string pluginId, string code, string message)
    {
        lock (gate)
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, code, message));
        }
    }

    private void ValidateSurfaceAndId(PluginDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Id);
        if ((descriptor.Surfaces & activeSurface) == 0)
        {
            throw new InvalidOperationException(
                $"Plugin '{descriptor.Id}' targets surfaces '{descriptor.Surfaces}', which do not match active surface '{activeSurface}'.");
        }
    }

    private void EnsureUniqueIdAndSourceLocked(PluginDescriptor descriptor)
    {
        if (entriesById.ContainsKey(descriptor.Id))
        {
            throw new InvalidOperationException($"Plugin '{descriptor.Id}' is already registered.");
        }

        if (FindSourceConflictLocked(descriptor) is { } conflictOwner)
        {
            throw new InvalidOperationException(
                $"Plugin '{descriptor.Id}' conflicts with SourceId '{descriptor.SourceId}' already owned by '{conflictOwner}'.");
        }
    }

    private string? FindSourceConflictLocked(PluginDescriptor descriptor)
    {
        if (descriptor.SourceId == Guid.Empty)
        {
            return null;
        }

        if (descriptor.Capabilities.HasFlag(PluginCapabilityKind.LibrarySource))
        {
            if (sourceIndex.TryGetValue(descriptor.SourceId, out var existing))
            {
                return existing.PluginId;
            }

            if (lazySourceOwners.TryGetValue(descriptor.SourceId, out var lazyOwner))
            {
                return lazyOwner;
            }
        }

        if (descriptor.Capabilities.HasFlag(PluginCapabilityKind.Launch))
        {
            if (launchIndex.TryGetValue(descriptor.SourceId, out var existing))
            {
                return existing.PluginId;
            }

            if (lazyLaunchOwners.TryGetValue(descriptor.SourceId, out var lazyOwner))
            {
                return lazyOwner;
            }
        }

        return null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class PluginEntry(PluginDescriptor descriptor, Func<IPlaynitePlugin> factory)
    {
        public Lock ActivationGate { get; } = new();
        public PluginDescriptor Descriptor { get; } = descriptor;
        public Func<IPlaynitePlugin> Factory { get; } = factory;
        public IPlaynitePlugin? Instance { get; set; }
        public PluginRegistration? Registration { get; set; }
        public PluginAssemblyLoadContext? LoadContext { get; set; }
        public PluginLifecycleState State { get; set; }
        public string? LastError { get; set; }
    }

    private sealed class ThemeDescriptorPlugin(PluginDescriptor descriptor) : IPlaynitePlugin
    {
        public PluginDescriptor Descriptor { get; } = descriptor;
        public PluginRegistration Setup(PluginContext context) => new();
    }

    private sealed class ExternalPluginHandle(IPlaynitePlugin inner, PluginAssemblyLoadContext loadContext) : IPlaynitePlugin
    {
        public IPlaynitePlugin Inner { get; } = inner;
        public PluginAssemblyLoadContext LoadContext { get; } = loadContext;
        public PluginDescriptor Descriptor => Inner.Descriptor;
        public PluginRegistration Setup(PluginContext context) => Inner.Setup(context);
    }

    private sealed class PluginAssemblyLoadContext(
        string pluginDirectory,
        string pluginAssemblyPath,
        IRegularFileProbe regularFileProbe)
        : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: true)
    {
        private static readonly HashSet<string> HostSharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            "Playnite.Core",
            "Playnite.SDK.Abstractions",
            "Playnite.SDK",
        };

        private readonly AssemblyDependencyResolver resolver = new(pluginAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not null && HostSharedAssemblies.Contains(assemblyName.Name))
            {
                return null;
            }

            var resolved = resolver.ResolveAssemblyToPath(assemblyName);
            if (resolved is null)
            {
                return null;
            }

            var fullResolved = Path.GetFullPath(resolved);
            if (!AddonInstallLayout.IsWithin(pluginDirectory, fullResolved)
                || HasSymlinkSegment(pluginDirectory, fullResolved)
                || !regularFileProbe.IsRegularFile(fullResolved))
            {
                throw new InvalidOperationException(
                    $"Plugin dependency '{fullResolved}' escapes plugin directory '{pluginDirectory}' or is not a regular file.");
            }

            return LoadFromAssemblyPath(fullResolved);
        }
    }
}
