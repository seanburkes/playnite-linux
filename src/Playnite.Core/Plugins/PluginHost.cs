using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Playnite.Core.Addons;
using Playnite.Core.Themes;

namespace Playnite.Core.Plugins;

/// <summary>
/// In-process plugin host supporting built-in zero-reflection registration, manifest-driven
/// lazy activation for external assemblies, surface filtering, and O(1) indexed capability lookup.
/// </summary>
public sealed class PluginHost : IDisposable
{
    public const string HostStatePluginId = "playnite.host.state";

    private const string EnabledKeyPrefix = "enabled:";
    private const string ActiveThemeKey = "theme.active";
    private const int MaxDiscoveryDepth = AddonInstallLayout.MaxDiscoveryDepth;
    private const int MaxSettingsFieldsPerPlugin = 64;
    private const int MaxSettingKeyLength = 128;
    private const int MaxSettingLabelLength = 80;
    private const int MaxSettingDescriptionLength = 240;
    private const int MaxSettingChoices = 64;
    private const int MaxSettingPathLength = 4096;
    private const int MaxCommandsPerPlugin = 32;
    private const int MaxCommandIdLength = 128;
    private const int MaxCommandLabelLength = 80;
    private const int MaxCommandMessageLength = 240;

    private readonly Lock gate = new();
    private readonly Lock lifecycleGate = new();
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
    private readonly Dictionary<string, List<(string PluginId, IUiSlotCapability Capability)>> uiSlotIndex =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string OwnerPluginId, IMetadataCapability Capability)> metadataIndex =
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

    /// <summary>Gets the plugin settings store backing this host.</summary>
    public IPluginSettingsStore SettingsStore => settingsStore;

    /// <summary>Records a host diagnostic entry for <paramref name="pluginId"/>.</summary>
    public void RecordDiagnostic(string pluginId, string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (gate)
        {
            if (!disposed)
            {
                diagnostics.Add(new PluginDiagnostic(pluginId ?? string.Empty, code, message));
            }
        }

        log?.Invoke(pluginId ?? string.Empty, message);
    }

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

    /// <summary>Gets a snapshot of diagnostics recorded by this host, optionally filtered to one plugin.</summary>
    public IReadOnlyList<PluginDiagnostic> GetDiagnostics(string? pluginId = null)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                return diagnostics.ToArray();
            }

            return diagnostics
                .Where(d => string.Equals(d.PluginId, pluginId, StringComparison.Ordinal))
                .ToArray();
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

    /// <summary>Gets enabled <see cref="PluginSurface.Theme"/> descriptors without materializing plugins.</summary>
    public IReadOnlyList<PluginDescriptor> GetThemeDescriptors()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var list = new List<PluginDescriptor>();
            foreach (var id in registrationOrder)
            {
                var entry = entriesById[id];
                if (entry.State is PluginLifecycleState.Registered or PluginLifecycleState.Active
                    && entry.Descriptor.Surfaces.HasFlag(PluginSurface.Theme))
                {
                    list.Add(entry.Descriptor);
                }
            }

            return list;
        }
    }

    /// <summary>Reads the persisted active desktop theme identifier from host state, if valid.</summary>
    public string? GetActiveThemeId()
    {
        lock (gate)
        {
            ThrowIfDisposed();
        }

        try
        {
            var raw = settingsStore.Get(HostStatePluginId, ActiveThemeKey);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var trimmed = raw.Trim();
            if (!AddonPathSafety.IsSafeSegment(trimmed) || IsReservedThemeStateId(trimmed))
            {
                return null;
            }

            return trimmed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            RecordDiagnostic(HostStatePluginId, "theme-state-read-failed",
                $"Could not read persisted active theme: {exception.Message}");
            return null;
        }
    }

    /// <summary>Persists or clears the active desktop theme identifier in host state.</summary>
    public bool SetActiveThemeId(string? themeId)
    {
        lock (lifecycleGate)
        {
            lock (gate)
            {
                ThrowIfDisposed();
            }

            string? normalized = null;
            if (!string.IsNullOrWhiteSpace(themeId))
            {
                var candidate = themeId.Trim();
                if (!string.Equals(candidate, ThemePackDefaults.DefaultDesktopThemeId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!AddonPathSafety.IsSafeSegment(candidate)
                        || IsReservedHostId(candidate)
                        || string.Equals(candidate, ThemePackDefaults.DefaultFullscreenThemeId, StringComparison.OrdinalIgnoreCase))
                    {
                        RecordDiagnostic(HostStatePluginId, "theme-state-invalid",
                            $"Refusing to persist unsafe or reserved theme id '{themeId}'.");
                        return false;
                    }

                    normalized = candidate;
                }
            }

            try
            {
                settingsStore.Set(HostStatePluginId, ActiveThemeKey, normalized);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                RecordDiagnostic(HostStatePluginId, "theme-state-persist-failed",
                    $"Could not persist active theme '{normalized ?? ThemePackDefaults.DefaultDesktopThemeId}': {exception.Message}");
                return false;
            }
        }
    }

    private static bool IsReservedThemeStateId(string candidate) =>
        IsReservedHostId(candidate)
        || string.Equals(candidate, ThemePackDefaults.DefaultDesktopThemeId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(candidate, ThemePackDefaults.DefaultFullscreenThemeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Registers a built-in plugin instance directly in-process.</summary>
    public bool RegisterBuiltIn(IPlaynitePlugin plugin, bool activateImmediately = true)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var descriptor = plugin.Descriptor;
        ValidateSurfaceAndId(descriptor);
        var initialEnabled = TryReadPersistedEnabledOverride(descriptor.Id) ?? descriptor.EnabledByDefault;

        PluginEntry entry;
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureUniqueIdAndSourceLocked(descriptor);

            entry = new PluginEntry(descriptor, () => plugin)
            {
                Instance = plugin,
                State = initialEnabled ? PluginLifecycleState.Registered : PluginLifecycleState.Disabled,
            };
            entriesById.Add(descriptor.Id, entry);
            registrationOrder.Add(descriptor.Id);

            if (!initialEnabled || !activateImmediately)
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
        var initialEnabled = TryReadPersistedEnabledOverride(descriptor.Id) ?? descriptor.EnabledByDefault;

        lock (gate)
        {
            ThrowIfDisposed();
            EnsureUniqueIdAndSourceLocked(descriptor);

            var entry = new PluginEntry(descriptor, factory)
            {
                State = initialEnabled ? PluginLifecycleState.Registered : PluginLifecycleState.Disabled,
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
                if ((depth != 0 || child.Name != AddonInstaller.PackageStoreName) && !IsSymlinkOrReparsePoint(child))
                {
                    pending.Push((child, depth + 1));
                }
            }
        }

        var packages = new AddonInstaller(new AddonInstallLayout(fullRoot), regularFileProbe);
        try
        {
            foreach (var (package, directory) in packages.GetActivePackageDirectories(
                         (id, message) => discoveryDiagnostics.Add(new PluginDiagnostic(id, "invalid-package-state", message))))
            {
                try
                {
                    var descriptor = packages.ReadPackageDescriptor(directory);
                    if (descriptor.Id != package.Id || descriptor.Version != package.Version)
                    {
                        throw new InvalidDataException("The installed package manifest does not match its recorded id and version.");
                    }
                    RegisterExternalDescriptor(descriptor, directory, regularFileProbe, discovered, discoveryDiagnostics);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    discoveryDiagnostics.Add(new PluginDiagnostic(package.Id, "invalid-package", exception.Message));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            discoveryDiagnostics.Add(new PluginDiagnostic(string.Empty, "package-read-failed", exception.Message));
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
        if (IsReservedHostId(pluginId))
        {
            return false;
        }

        lock (lifecycleGate)
        {
            var persistedOverride = TryReadPersistedEnabledOverride(pluginId);
            PluginEntry entry;
            bool wasDisabled;
            bool wasFaulted;
            lock (gate)
            {
                ThrowIfDisposed();
                if (!entriesById.TryGetValue(pluginId, out var found) || found.State == PluginLifecycleState.Disposed)
                {
                    return false;
                }

                if (found.State == PluginLifecycleState.Active
                    && persistedOverride != false
                    && (found.Descriptor.EnabledByDefault || persistedOverride == true))
                {
                    return true;
                }

                wasDisabled = found.State == PluginLifecycleState.Disabled;
                wasFaulted = found.State == PluginLifecycleState.Faulted;
                if (wasDisabled)
                {
                    found.State = PluginLifecycleState.Registered;
                }

                entry = found;
            }

            if (!ActivateEntry(entry))
            {
                return false;
            }

            if (wasDisabled
                || wasFaulted
                || persistedOverride == false
                || (!entry.Descriptor.EnabledByDefault && persistedOverride != true))
            {
                TryPersistEnabledState(pluginId, enabled: true);
            }

            return true;
        }
    }

    /// <summary>Disables a plugin and removes its capabilities from all host lookup indexes.</summary>
    public bool Disable(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (IsReservedHostId(pluginId))
        {
            return false;
        }

        lock (lifecycleGate)
        {
            var persistedOverride = TryReadPersistedEnabledOverride(pluginId);
            bool changed;
            lock (gate)
            {
                ThrowIfDisposed();
                if (!entriesById.TryGetValue(pluginId, out var entry) || entry.State == PluginLifecycleState.Disposed)
                {
                    return false;
                }

                changed = entry.State != PluginLifecycleState.Disabled;
                RemoveLazyOwnersLocked(entry);
                RemoveIndexedCapabilitiesLocked(entry);
                entry.State = PluginLifecycleState.Disabled;
            }

            if (changed || persistedOverride == true)
            {
                TryPersistEnabledState(pluginId, enabled: false);
            }

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
            var effectiveResult = string.IsNullOrWhiteSpace(result.PluginId)
                ? result with { PluginId = pluginId }
                : result;
            await PublishSyncResultAsync(effectiveResult, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Publishes a <see cref="PluginEvents.LibrarySynced"/> event when <paramref name="result"/> succeeded.
    /// </summary>
    public async Task PublishSyncResultAsync(PluginSyncResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded)
        {
            return;
        }

        var normalizedPluginId = string.IsNullOrWhiteSpace(result.PluginId) ? "unknown" : result.PluginId.Trim();
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pluginId"] = normalizedPluginId,
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
                cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException exception)
        {
            log?.Invoke(normalizedPluginId, $"Skipping library.synced event after host disposal ({exception.GetType().Name}).");
        }
    }

    /// <summary>
    /// Returns whether any active or pending lazy plugin provides commands matching <paramref name="placement"/>
    /// without materializing external plugins.
    /// </summary>
    public bool HasCommandProviders(PluginCommandPlacement? placement = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (placement is { } filter)
            {
                if (commandIndex.Values.Any(item => item.Descriptor.Placement == filter))
                {
                    return true;
                }
            }
            else if (commandIndex.Count > 0)
            {
                return true;
            }

            foreach (var id in registrationOrder)
            {
                var entry = entriesById[id];
                if (entry.State == PluginLifecycleState.Registered
                    && entry.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.Commands))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Returns active plugin commands, activating any pending lazy command plugins.</summary>
    public IReadOnlyList<PluginCommandDescriptor> GetCommands(PluginCommandPlacement? placement = null) =>
        GetCommands(placement, activateLazy: true);

    /// <summary>
    /// Returns plugin commands, optionally activating any pending lazy command plugins when
    /// <paramref name="activateLazy"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<PluginCommandDescriptor> GetCommands(PluginCommandPlacement? placement, bool activateLazy)
    {
        if (activateLazy)
        {
            var pending = SnapshotLazyEntries(PluginCapabilityKind.Commands);
            foreach (var entry in pending)
            {
                ActivateEntry(entry);
            }
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
        var normalizedId = commandId.Trim();

        string pluginId;
        ICommandCapability capability;
        bool found;
        lock (gate)
        {
            ThrowIfDisposed();
            found = TryGetActiveCommandLocked(normalizedId, out pluginId, out capability);
        }

        if (!found)
        {
            var pending = SnapshotLazyEntries(PluginCapabilityKind.Commands);
            foreach (var lazyEntry in pending)
            {
                ActivateEntry(lazyEntry);
            }

            lock (gate)
            {
                ThrowIfDisposed();
                found = TryGetActiveCommandLocked(normalizedId, out pluginId, out capability);
            }
        }

        if (!found)
        {
            return new PluginCommandResult(false, $"Plugin command '{normalizedId}' is not available.");
        }

        try
        {
            var rawResult = await capability.ExecuteAsync(normalizedId, context, cancellationToken).ConfigureAwait(false);
            if (rawResult is null)
            {
                RecordRuntimeFault(pluginId, "command-failed", $"Command '{normalizedId}' returned null.");
                return new PluginCommandResult(false, $"Command '{normalizedId}' failed.");
            }

            var message = NormalizeDisplayText(rawResult.Message, MaxCommandMessageLength)
                ?? (rawResult.Succeeded ? "Command completed." : "Command failed.");
            return new PluginCommandResult(
                rawResult.Succeeded,
                message,
                rawResult.RefreshLibrary,
                rawResult.RefreshSlots);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordRuntimeFault(pluginId, "command-failed", $"Command '{normalizedId}' failed: {exception.Message}");
            var errorMessage = NormalizeDisplayText(exception.Message, MaxCommandMessageLength) ?? "Command failed.";
            return new PluginCommandResult(false, errorMessage);
        }
    }

    /// <summary>Returns the sanitized declarative settings schema declared by one plugin, activating lazily if requested.</summary>
    public IReadOnlyList<PluginSettingField> GetSettingsSchema(string pluginId) =>
        GetSettingsSchema(pluginId, activateIfLazy: true);

    /// <summary>
    /// Returns the sanitized declarative settings schema declared by one plugin, optionally activating
    /// a lazy plugin when <paramref name="activateIfLazy"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<PluginSettingField> GetSettingsSchema(string pluginId, bool activateIfLazy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (IsReservedHostId(pluginId))
        {
            return [];
        }

        IPluginSettingsCapability? settings = null;
        PluginEntry? lazyTarget = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (settingsIndex.TryGetValue(pluginId, out var found))
            {
                settings = found;
            }
            else if (activateIfLazy
                && entriesById.TryGetValue(pluginId, out var entry)
                && entry.State == PluginLifecycleState.Registered
                && entry.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.Settings))
            {
                lazyTarget = entry;
            }
        }

        if (lazyTarget is not null)
        {
            if (ActivateEntry(lazyTarget))
            {
                lock (gate)
                {
                    ThrowIfDisposed();
                    settingsIndex.TryGetValue(pluginId, out settings);
                }
            }
        }

        if (settings is null)
        {
            return [];
        }

        try
        {
            return SanitizeSettingsSchema(settings.Fields);
        }
        catch (Exception exception)
        {
            RecordRuntimeFault(pluginId, "settings-schema-failed", $"Settings schema failed: {exception.Message}");
            return [];
        }
    }

    /// <summary>
    /// Returns effective validated settings values for one plugin, combining persisted values
    /// with declared sanitized defaults without forcing lazy activation unless requested.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetSettingsValues(string pluginId, bool activateIfLazy = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var schema = GetSettingsSchema(pluginId, activateIfLazy);
        if (schema.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        IReadOnlyDictionary<string, string> stored;
        try
        {
            stored = settingsStore.GetAll(pluginId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            RecordDiagnostic(pluginId, "settings-read-failed",
                $"Could not read settings for '{pluginId}': {exception.Message}");
            stored = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var effective = new Dictionary<string, string>(schema.Count, StringComparer.Ordinal);
        foreach (var field in schema)
        {
            var fallback = field.DefaultValue ?? GetTypeFallbackValue(field);
            if (stored.TryGetValue(field.Key, out var rawStored)
                && TryValidateAndNormalizeSettingValue(field, rawStored, out var normalized, out _))
            {
                effective[field.Key] = normalized;
            }
            else
            {
                effective[field.Key] = fallback;
            }
        }

        return effective;
    }

    /// <summary>
    /// Validates every supplied setting against the plugin's declared schema before persisting any change.
    /// </summary>
    public PluginSettingsSaveResult SaveSettings(string pluginId, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(values);
        if (IsReservedHostId(pluginId))
        {
            return new PluginSettingsSaveResult(false, $"Plugin identifier '{pluginId}' is reserved by the plugin host.");
        }

        lock (lifecycleGate)
        {
            PluginLifecycleState initialState;
            lock (gate)
            {
                ThrowIfDisposed();
                if (!entriesById.TryGetValue(pluginId, out var entry))
                {
                    return new PluginSettingsSaveResult(false, $"Plugin '{pluginId}' is not registered.");
                }

                initialState = entry.State;
            }

            if (initialState == PluginLifecycleState.Disabled)
            {
                return new PluginSettingsSaveResult(false, $"Plugin '{pluginId}' is disabled. Enable it before saving settings.");
            }

            if (initialState == PluginLifecycleState.Disposed)
            {
                return new PluginSettingsSaveResult(false, $"Plugin '{pluginId}' has been disposed.");
            }

            var schema = GetSettingsSchema(pluginId, activateIfLazy: true);
            PluginLifecycleState currentState;
            string? faultError;
            lock (gate)
            {
                ThrowIfDisposed();
                currentState = entriesById.TryGetValue(pluginId, out var refreshed)
                    ? refreshed.State
                    : PluginLifecycleState.Disposed;
                faultError = refreshed?.LastError;
            }

            if (currentState == PluginLifecycleState.Disabled)
            {
                return new PluginSettingsSaveResult(false, $"Plugin '{pluginId}' is disabled. Enable it before saving settings.");
            }

            if (currentState == PluginLifecycleState.Disposed)
            {
                return new PluginSettingsSaveResult(false, $"Plugin '{pluginId}' has been disposed.");
            }

            if (currentState == PluginLifecycleState.Faulted)
            {
                return new PluginSettingsSaveResult(false, faultError ?? $"Plugin '{pluginId}' faulted while loading settings.");
            }

            if (schema.Count == 0)
            {
                return new PluginSettingsSaveResult(false, $"Plugin '{pluginId}' does not declare editable settings.");
            }

            var schemaByKey = new Dictionary<string, PluginSettingField>(schema.Count, StringComparer.Ordinal);
            foreach (var field in schema)
            {
                schemaByKey[field.Key] = field;
            }

            var normalizedWrites = new Dictionary<string, string?>(values.Count, StringComparer.Ordinal);
            var fieldErrors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (rawKey, rawValue) in values)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || !schemaByKey.TryGetValue(rawKey, out var field))
                {
                    var errorKey = string.IsNullOrWhiteSpace(rawKey) ? "<empty>" : rawKey;
                    fieldErrors[errorKey] = $"Unknown setting key '{errorKey}'.";
                    continue;
                }

                if (TryValidateAndNormalizeSettingValue(field, rawValue, out var normalized, out var error))
                {
                    normalizedWrites[field.Key] = normalized;
                }
                else
                {
                    fieldErrors[field.Key] = error!;
                }
            }

            if (fieldErrors.Count > 0)
            {
                return new PluginSettingsSaveResult(false, "One or more settings are invalid.", fieldErrors);
            }

            try
            {
                settingsStore.SetMany(pluginId, normalizedWrites);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                RecordDiagnostic(pluginId, "settings-save-failed",
                    $"Could not save settings for '{pluginId}': {exception.Message}");
                return new PluginSettingsSaveResult(false, exception.Message);
            }

            return new PluginSettingsSaveResult(true);
        }
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

    /// <summary>
    /// Returns true when any active subscriber or unmaterialized lazy UI-slot plugin can populate
    /// <paramref name="slotId"/>, without loading external assemblies.
    /// </summary>
    public bool HasUiSlotProviders(string slotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotId);
        var normalizedSlot = slotId.Trim();
        lock (gate)
        {
            ThrowIfDisposed();
            if (uiSlotIndex.TryGetValue(normalizedSlot, out var activeList)
                && activeList.Any(item => entriesById.TryGetValue(item.PluginId, out var entry)
                    && entry.State == PluginLifecycleState.Active))
            {
                return true;
            }

            foreach (var id in registrationOrder)
            {
                var entry = entriesById[id];
                if (entry.State == PluginLifecycleState.Registered
                    && entry.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.UiSlots))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Resolves sanitized declarative UI badges and sections from all active and lazy UI-slot plugins
    /// subscribed to <paramref name="slotId"/>.
    /// </summary>
    public async Task<IReadOnlyList<PluginUiSlotContribution>> ResolveUiSlotAsync(
        string slotId,
        PluginGameSnapshot game,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotId);
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSlot = slotId.Trim();

        var pending = SnapshotLazyEntries(PluginCapabilityKind.UiSlots);
        foreach (var lazyEntry in pending)
        {
            ActivateEntry(lazyEntry);
        }

        (string PluginId, IUiSlotCapability Capability)[] subscribers;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!uiSlotIndex.TryGetValue(normalizedSlot, out var list) || list.Count == 0)
            {
                return [];
            }

            subscribers = list.ToArray();
        }

        var results = new List<PluginUiSlotContribution>(subscribers.Length);
        foreach (var (pluginId, capability) in subscribers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (disposed || !entriesById.TryGetValue(pluginId, out var entry) || entry.State != PluginLifecycleState.Active)
                {
                    continue;
                }
            }

            PluginUiSlotContribution? sanitized;
            try
            {
                var raw = await capability.ResolveSlotAsync(normalizedSlot, game, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                sanitized = SanitizeUiSlotContribution(pluginId, normalizedSlot, raw);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordRuntimeFault(pluginId, "ui-slot-failed",
                    $"UI slot '{normalizedSlot}' failed: {exception.Message}");
                continue;
            }

            if (sanitized is not null)
            {
                results.Add(sanitized);
            }
        }

        return results;
    }

    /// <summary>Returns active metadata providers, activating any pending lazy metadata plugins.</summary>
    public IReadOnlyList<IMetadataCapability> GetMetadataProviders()
    {
        var pending = SnapshotLazyEntries(PluginCapabilityKind.Metadata);
        foreach (var lazyEntry in pending)
        {
            ActivateEntry(lazyEntry);
        }

        lock (gate)
        {
            ThrowIfDisposed();
            return metadataIndex.Values
                .Select(item => item.Capability)
                .OrderBy(item => item.ProviderId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>Looks up a metadata provider by identifier, activating pending lazy metadata plugins if needed.</summary>
    public bool TryGetMetadataProvider(string providerId, out IMetadataCapability capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        PluginEntry? lazyCandidate = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (metadataIndex.TryGetValue(providerId, out var found))
            {
                capability = found.Capability;
                return true;
            }

            if (entriesById.TryGetValue(providerId, out var exact)
                && exact.State == PluginLifecycleState.Registered
                && exact.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.Metadata))
            {
                lazyCandidate = exact;
            }
        }

        if (lazyCandidate is not null)
        {
            ActivateEntry(lazyCandidate);
            lock (gate)
            {
                ThrowIfDisposed();
                if (metadataIndex.TryGetValue(providerId, out var found))
                {
                    capability = found.Capability;
                    return true;
                }
            }
        }

        var pending = SnapshotLazyEntries(PluginCapabilityKind.Metadata);
        foreach (var lazyEntry in pending)
        {
            ActivateEntry(lazyEntry);
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (metadataIndex.TryGetValue(providerId, out var found))
            {
                capability = found.Capability;
                return true;
            }
        }

        capability = null!;
        return false;
    }

    /// <summary>Fetches fault-isolated metadata contributions across active and lazy metadata plugins.</summary>
    public async Task<IReadOnlyList<PluginGameMetadata>> FetchMetadataAsync(
        PluginGameSnapshot game,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();

        var pending = SnapshotLazyEntries(PluginCapabilityKind.Metadata);
        foreach (var lazyEntry in pending)
        {
            ActivateEntry(lazyEntry);
        }

        (string OwnerPluginId, IMetadataCapability Capability)[] providers;
        lock (gate)
        {
            ThrowIfDisposed();
            if (metadataIndex.Count == 0)
            {
                return [];
            }

            providers = metadataIndex.Values
                .OrderBy(item => item.Capability.ProviderId, StringComparer.Ordinal)
                .ToArray();
        }

        var results = new List<PluginGameMetadata>(providers.Length);
        foreach (var (ownerPluginId, capability) in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (disposed || !entriesById.TryGetValue(ownerPluginId, out var entry) || entry.State != PluginLifecycleState.Active)
                {
                    continue;
                }
            }

            PluginGameMetadata? sanitizedMetadata = null;
            try
            {
                var raw = await capability.FetchMetadataAsync(game, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (raw is not null)
                {
                    int? clampedScore = raw.CommunityScore is int score
                        ? Math.Clamp(score, 0, 100)
                        : null;
                    var sanitizedSummary = NormalizeDisplayText(raw.Summary, 512);
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (raw.Attributes is not null)
                    {
                        foreach (var pair in raw.Attributes)
                        {
                            var normalizedKey = NormalizeDisplayText(pair.Key, 64);
                            var normalizedValue = NormalizeDisplayText(pair.Value, 256);
                            if (normalizedKey is null || normalizedValue is null)
                            {
                                continue;
                            }

                            if (!attributes.ContainsKey(normalizedKey) && attributes.Count >= 32)
                            {
                                break;
                            }

                            attributes[normalizedKey] = normalizedValue;
                        }
                    }

                    sanitizedMetadata = new PluginGameMetadata(ownerPluginId, sanitizedSummary, clampedScore, attributes);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordRuntimeFault(ownerPluginId, "metadata-failed",
                    $"Metadata provider '{capability.ProviderId}' failed: {exception.Message}");
                continue;
            }

            if (sanitizedMetadata is not null)
            {
                results.Add(sanitizedMetadata);
            }
        }

        return results;
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
            uiSlotIndex.Clear();
            metadataIndex.Clear();
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

        RegisterExternalDescriptor(descriptor, directoryPath, regularFileProbe, discovered, discoveryDiagnostics);
    }

    private void RegisterExternalDescriptor(
        PluginDescriptor descriptor,
        string directoryPath,
        IRegularFileProbe regularFileProbe,
        List<PluginDescriptor> discovered,
        List<PluginDiagnostic> discoveryDiagnostics)
    {
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
        if (IsReservedHostId(descriptor.Id))
        {
            discoveryDiagnostics.Add(new PluginDiagnostic(
                descriptor.Id,
                "reserved-plugin-id",
                $"Plugin '{descriptor.Id}' uses a reserved host identifier."));
            return;
        }

        var initialEnabled = TryReadPersistedEnabledOverride(descriptor.Id) ?? descriptor.EnabledByDefault;

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
                State = initialEnabled ? PluginLifecycleState.Registered : PluginLifecycleState.Disabled,
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
        IReadOnlyList<(ICommandCapability? Provider, PluginCommandDescriptor[]? Commands)> commandSnapshots = [];
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
                commandSnapshots = SnapshotCommandProviders(candidateRegistration);
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
                    conflictError = ValidateRegistrationConflictsLocked(entry.Descriptor.Id, candidateRegistration, commandSnapshots);
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
                        IndexCapabilitiesLocked(entry, commandSnapshots);
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
        if (entry.State != PluginLifecycleState.Registered || entry.Descriptor.SourceId == Guid.Empty)
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

    private bool TryGetActiveCommandLocked(string commandId, out string pluginId, out ICommandCapability capability)
    {
        if (commandIndex.TryGetValue(commandId, out var registered)
            && entriesById.TryGetValue(registered.PluginId, out var ownerEntry)
            && ownerEntry.State == PluginLifecycleState.Active)
        {
            pluginId = registered.PluginId;
            capability = registered.Capability;
            return true;
        }

        pluginId = string.Empty;
        capability = null!;
        return false;
    }

    private static IReadOnlyList<(ICommandCapability? Provider, PluginCommandDescriptor[]? Commands)> SnapshotCommandProviders(
        PluginRegistration registration)
    {
        if (registration.CommandProviders.Count == 0)
        {
            return [];
        }

        var snapshots = new (ICommandCapability? Provider, PluginCommandDescriptor[]? Commands)[registration.CommandProviders.Count];
        for (var i = 0; i < registration.CommandProviders.Count; i++)
        {
            var provider = registration.CommandProviders[i];
            snapshots[i] = provider is null
                ? (null, null)
                : (provider, provider.Commands?.ToArray());
        }

        return snapshots;
    }

    private string? ValidateRegistrationConflictsLocked(
        string pluginId,
        PluginRegistration registration,
        IReadOnlyList<(ICommandCapability? Provider, PluginCommandDescriptor[]? Commands)> commandSnapshots)
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

        var seenCommands = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, commands) in commandSnapshots)
        {
            if (commands is null)
            {
                return $"Plugin '{pluginId}' declared a null command provider or command list.";
            }

            foreach (var command in commands)
            {
                if (!TryNormalizeCommandDescriptor(pluginId, command, out var normalized, out var commandError))
                {
                    return commandError;
                }

                if (!seenCommands.Add(normalized.Id))
                {
                    return $"Plugin '{pluginId}' declared duplicate command '{normalized.Id}'.";
                }

                if (seenCommands.Count > MaxCommandsPerPlugin)
                {
                    return $"Plugin '{pluginId}' exceeded the {MaxCommandsPerPlugin}-command limit.";
                }

                if (commandIndex.TryGetValue(normalized.Id, out var existing)
                    && !string.Equals(existing.PluginId, pluginId, StringComparison.Ordinal))
                {
                    return $"Command '{normalized.Id}' is already registered by plugin '{existing.PluginId}'.";
                }
            }
        }

        foreach (var slotProvider in registration.UiSlotProviders)
        {
            if (slotProvider?.SubscribedSlots is null || !slotProvider.SubscribedSlots.Any(s => !string.IsNullOrWhiteSpace(s)))
            {
                return $"Plugin '{pluginId}' declared a UI slot provider without any subscribed slots.";
            }
        }

        var seenMetadataIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in registration.MetadataProviders)
        {
            if (provider is null || string.IsNullOrWhiteSpace(provider.ProviderId))
            {
                return $"Plugin '{pluginId}' declared a metadata provider with a blank ProviderId.";
            }

            var normalizedId = provider.ProviderId.Trim();
            if (!seenMetadataIds.Add(normalizedId))
            {
                return $"Plugin '{pluginId}' declared duplicate metadata provider '{normalizedId}'.";
            }

            if (metadataIndex.TryGetValue(normalizedId, out var existing)
                && !string.Equals(existing.OwnerPluginId, pluginId, StringComparison.Ordinal))
            {
                return $"Metadata provider '{normalizedId}' is already registered by plugin '{existing.OwnerPluginId}'.";
            }

            if (!string.Equals(normalizedId, pluginId, StringComparison.Ordinal)
                && entriesById.ContainsKey(normalizedId))
            {
                return $"Metadata provider '{normalizedId}' collides with plugin ID '{normalizedId}'.";
            }
        }

        return null;
    }

    private void IndexCapabilitiesLocked(
        PluginEntry entry,
        IReadOnlyList<(ICommandCapability? Provider, PluginCommandDescriptor[]? Commands)> commandSnapshots)
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

        foreach (var (provider, commands) in commandSnapshots)
        {
            if (provider is null || commands is null)
            {
                continue;
            }

            foreach (var command in commands)
            {
                if (TryNormalizeCommandDescriptor(entry.Descriptor.Id, command, out var normalized, out _))
                {
                    commandIndex[normalized.Id] = (entry.Descriptor.Id, normalized, provider);
                }
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

        foreach (var slotProvider in registration.UiSlotProviders)
        {
            foreach (var slotId in slotProvider.SubscribedSlots
                .Where(slot => !string.IsNullOrWhiteSpace(slot))
                .Select(slot => slot.Trim())
                .Distinct(StringComparer.Ordinal))
            {
                if (!uiSlotIndex.TryGetValue(slotId, out var list))
                {
                    list = [];
                    uiSlotIndex[slotId] = list;
                }

                if (!list.Any(item => string.Equals(item.PluginId, entry.Descriptor.Id, StringComparison.Ordinal)
                        && ReferenceEquals(item.Capability, slotProvider)))
                {
                    list.Add((entry.Descriptor.Id, slotProvider));
                }
            }
        }

        foreach (var metadataProvider in registration.MetadataProviders)
        {
            if (!string.IsNullOrWhiteSpace(metadataProvider.ProviderId))
            {
                metadataIndex[metadataProvider.ProviderId.Trim()] = (entry.Descriptor.Id, metadataProvider);
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

        var commandKeysToRemove = new List<string>();
        foreach (var pair in commandIndex)
        {
            if (string.Equals(pair.Value.PluginId, entry.Descriptor.Id, StringComparison.Ordinal))
            {
                commandKeysToRemove.Add(pair.Key);
            }
        }

        foreach (var commandKey in commandKeysToRemove)
        {
            commandIndex.Remove(commandKey);
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

        foreach (var slotProvider in registration.UiSlotProviders)
        {
            if (slotProvider?.SubscribedSlots is null)
            {
                continue;
            }

            foreach (var rawSlot in slotProvider.SubscribedSlots)
            {
                if (string.IsNullOrWhiteSpace(rawSlot))
                {
                    continue;
                }

                var slotId = rawSlot.Trim();
                if (uiSlotIndex.TryGetValue(slotId, out var list))
                {
                    list.RemoveAll(item => string.Equals(item.PluginId, entry.Descriptor.Id, StringComparison.Ordinal));
                    if (list.Count == 0)
                    {
                        uiSlotIndex.Remove(slotId);
                    }
                }
            }
        }

        foreach (var metadataProvider in registration.MetadataProviders)
        {
            if (metadataProvider is null || string.IsNullOrWhiteSpace(metadataProvider.ProviderId))
            {
                continue;
            }

            var providerId = metadataProvider.ProviderId.Trim();
            if (metadataIndex.TryGetValue(providerId, out var existing)
                && string.Equals(existing.OwnerPluginId, entry.Descriptor.Id, StringComparison.Ordinal))
            {
                metadataIndex.Remove(providerId);
            }
        }

        settingsIndex.Remove(entry.Descriptor.Id);
    }

    private static PluginUiSlotContribution? SanitizeUiSlotContribution(
        string pluginId,
        string slotId,
        PluginUiSlotContribution? raw)
    {
        if (raw is null)
        {
            return null;
        }

        const int maxBadgesPerPlugin = 4;
        const int maxSectionsPerPlugin = 2;
        const int maxFieldsPerSection = 12;

        List<PluginUiBadge>? sanitizedBadges = null;
        if (raw.Badges is { Count: > 0 })
        {
            foreach (var badge in raw.Badges)
            {
                if (badge is null)
                {
                    continue;
                }

                var label = NormalizeDisplayText(badge.Label, 64);
                if (label is null)
                {
                    continue;
                }

                sanitizedBadges ??= [];
                var id = NormalizeDisplayText(badge.Id, 64) ?? $"{pluginId}.badge.{sanitizedBadges.Count}";
                var tooltip = NormalizeDisplayText(badge.Tooltip, 256);
                sanitizedBadges.Add(new PluginUiBadge(
                    id,
                    label,
                    badge.Tone,
                    tooltip,
                    TrySanitizeHttpsUrl(badge.LinkUrl)));

                if (sanitizedBadges.Count >= maxBadgesPerPlugin)
                {
                    break;
                }
            }
        }

        List<PluginUiSection>? sanitizedSections = null;
        if (raw.Sections is { Count: > 0 })
        {
            foreach (var section in raw.Sections)
            {
                if (section is null)
                {
                    continue;
                }

                var title = NormalizeDisplayText(section.Title, 80);
                if (title is null)
                {
                    continue;
                }

                sanitizedSections ??= [];
                var id = NormalizeDisplayText(section.Id, 64) ?? $"{pluginId}.section.{sanitizedSections.Count}";
                var summary = NormalizeDisplayText(section.Summary, 512);
                var sanitizedUrl = TrySanitizeHttpsUrl(section.LinkUrl);
                var linkLabel = sanitizedUrl is null ? null : NormalizeDisplayText(section.LinkLabel, 64);

                var sanitizedFields = new List<PluginUiSectionField>();
                if (section.Fields is { Count: > 0 })
                {
                    foreach (var field in section.Fields)
                    {
                        if (field is null)
                        {
                            continue;
                        }

                        var fieldLabel = NormalizeDisplayText(field.Label, 64);
                        var fieldValue = NormalizeDisplayText(field.Value, 256);
                        if (fieldLabel is null || fieldValue is null)
                        {
                            continue;
                        }

                        sanitizedFields.Add(new PluginUiSectionField(fieldLabel, fieldValue));
                        if (sanitizedFields.Count >= maxFieldsPerSection)
                        {
                            break;
                        }
                    }
                }

                sanitizedSections.Add(new PluginUiSection(
                    id,
                    title,
                    summary,
                    sanitizedFields,
                    linkLabel,
                    sanitizedUrl));

                if (sanitizedSections.Count >= maxSectionsPerPlugin)
                {
                    break;
                }
            }
        }

        if ((sanitizedBadges is null || sanitizedBadges.Count == 0)
            && (sanitizedSections is null || sanitizedSections.Count == 0))
        {
            return null;
        }

        return new PluginUiSlotContribution(
            pluginId,
            slotId,
            sanitizedBadges ?? (IReadOnlyList<PluginUiBadge>)[],
            sanitizedSections ?? (IReadOnlyList<PluginUiSection>)[]);
    }

    private static bool TryNormalizeCommandDescriptor(
        string pluginId,
        PluginCommandDescriptor? command,
        out PluginCommandDescriptor normalized,
        out string? error)
    {
        if (command is null || string.IsNullOrWhiteSpace(command.Id))
        {
            normalized = null!;
            error = $"Plugin '{pluginId}' declared a command with a blank Id.";
            return false;
        }

        var normalizedId = command.Id.Trim();
        if (normalizedId.Length > MaxCommandIdLength || normalizedId.Any(char.IsControl))
        {
            normalized = null!;
            error = $"Plugin '{pluginId}' declared an invalid command Id.";
            return false;
        }

        if (!Enum.IsDefined(command.Placement))
        {
            normalized = null!;
            error = $"Command '{normalizedId}' declared an invalid placement.";
            return false;
        }

        var normalizedLabel = NormalizeDisplayText(command.Label, MaxCommandLabelLength);
        if (normalizedLabel is null)
        {
            normalized = null!;
            error = $"Command '{normalizedId}' declared a blank Label.";
            return false;
        }

        var normalizedSourceId = command.SourceId is Guid source && source != Guid.Empty
            ? source
            : (Guid?)null;
        normalized = new PluginCommandDescriptor(normalizedId, pluginId, normalizedLabel, command.Placement, normalizedSourceId);
        error = null;
        return true;
    }

    private static string? NormalizeDisplayText(string? rawText, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        Span<char> buffer = rawText.Length <= 512
            ? stackalloc char[Math.Min(rawText.Length, maxLength)]
            : new char[maxLength];
        var written = 0;
        var span = rawText.AsSpan().Trim();
        for (var i = 0; i < span.Length && written < maxLength; i++)
        {
            var ch = span[i];
            if (!char.IsControl(ch))
            {
                buffer[written++] = ch;
            }
        }

        if (written == 0)
        {
            return null;
        }

        var result = new string(buffer[..written]).Trim();
        return result.Length == 0 ? null : result;
    }

    private static string? TrySanitizeHttpsUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Any(char.IsControl))
        {
            return null;
        }

        var trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0)
        {
            return null;
        }

        return uri.AbsoluteUri;
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

    private static bool IsReservedHostId(string? pluginId) =>
        string.Equals(pluginId?.Trim(), HostStatePluginId, StringComparison.OrdinalIgnoreCase);

    private bool? TryReadPersistedEnabledOverride(string pluginId)
    {
        try
        {
            var raw = settingsStore.Get(HostStatePluginId, EnabledKeyPrefix + pluginId);
            return bool.TryParse(raw, out var parsed) ? parsed : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            RecordDiagnostic(HostStatePluginId, "state-read-failed",
                $"Could not read persisted state for '{pluginId}': {exception.Message}");
            return null;
        }
    }

    private void TryPersistEnabledState(string pluginId, bool enabled)
    {
        try
        {
            settingsStore.Set(HostStatePluginId, EnabledKeyPrefix + pluginId, enabled ? "true" : "false");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            RecordDiagnostic(HostStatePluginId, "state-persist-failed",
                $"Could not persist state for '{pluginId}': {exception.Message}");
        }
    }

    private static IReadOnlyList<PluginSettingField> SanitizeSettingsSchema(IReadOnlyList<PluginSettingField>? rawFields)
    {
        if (rawFields is null || rawFields.Count == 0)
        {
            return [];
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var sanitized = new List<PluginSettingField>(Math.Min(rawFields.Count, MaxSettingsFieldsPerPlugin));
        foreach (var field in rawFields)
        {
            if (field is null || !Enum.IsDefined(field.Type) || string.IsNullOrWhiteSpace(field.Key))
            {
                continue;
            }

            var key = field.Key.Trim();
            if (key.Length > MaxSettingKeyLength || key.Any(char.IsControl) || !seenKeys.Add(key))
            {
                continue;
            }

            var label = NormalizeDisplayText(field.Label, MaxSettingLabelLength);
            if (label is null)
            {
                continue;
            }

            var description = NormalizeDisplayText(field.Description, MaxSettingDescriptionLength);
            IReadOnlyList<string>? choices = null;
            string? defaultValue;

            switch (field.Type)
            {
                case PluginSettingType.Boolean:
                    defaultValue = string.Equals(field.DefaultValue?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                        ? "true"
                        : "false";
                    break;
                case PluginSettingType.Integer:
                    defaultValue = int.TryParse(field.DefaultValue?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intDefault)
                        ? intDefault.ToString(CultureInfo.InvariantCulture)
                        : "0";
                    break;
                case PluginSettingType.Choice:
                    if (field.Choices is null || field.Choices.Count == 0)
                    {
                        continue;
                    }

                    var seenChoices = new HashSet<string>(StringComparer.Ordinal);
                    var cleanChoices = new List<string>(Math.Min(field.Choices.Count, MaxSettingChoices));
                    foreach (var rawChoice in field.Choices)
                    {
                        var choice = NormalizeDisplayText(rawChoice, MaxSettingLabelLength);
                        if (choice is not null && seenChoices.Add(choice))
                        {
                            cleanChoices.Add(choice);
                            if (cleanChoices.Count >= MaxSettingChoices)
                            {
                                break;
                            }
                        }
                    }

                    if (cleanChoices.Count == 0)
                    {
                        continue;
                    }

                    choices = cleanChoices;
                    var normalizedDefault = NormalizeDisplayText(field.DefaultValue, MaxSettingLabelLength);
                    defaultValue = normalizedDefault is not null && seenChoices.Contains(normalizedDefault)
                        ? normalizedDefault
                        : cleanChoices[0];
                    break;
                case PluginSettingType.Path:
                    defaultValue = SanitizePathDefault(field.DefaultValue);
                    break;
                default:
                    defaultValue = SanitizeStringDefault(field.DefaultValue);
                    break;
            }

            sanitized.Add(new PluginSettingField(key, label, field.Type, defaultValue, choices, description));
            if (sanitized.Count >= MaxSettingsFieldsPerPlugin)
            {
                break;
            }
        }

        return sanitized;
    }

    private static string GetTypeFallbackValue(PluginSettingField field) => field.Type switch
    {
        PluginSettingType.Boolean => "false",
        PluginSettingType.Integer => "0",
        PluginSettingType.Choice => field.Choices is { Count: > 0 } ? field.Choices[0] : string.Empty,
        _ => string.Empty,
    };

    private static bool TryValidateAndNormalizeSettingValue(
        PluginSettingField field,
        string? rawValue,
        out string normalized,
        out string? error)
    {
        switch (field.Type)
        {
            case PluginSettingType.Boolean:
                if (bool.TryParse(rawValue?.Trim(), out var boolVal))
                {
                    normalized = boolVal ? "true" : "false";
                    error = null;
                    return true;
                }

                normalized = string.Empty;
                error = $"{field.Label} must be true or false.";
                return false;

            case PluginSettingType.Integer:
                if (int.TryParse(rawValue?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                {
                    normalized = intVal.ToString(CultureInfo.InvariantCulture);
                    error = null;
                    return true;
                }

                normalized = string.Empty;
                error = $"{field.Label} must be a whole number.";
                return false;

            case PluginSettingType.Choice:
                var choice = rawValue?.Trim() ?? string.Empty;
                if (field.Choices is { Count: > 0 } && field.Choices.Contains(choice, StringComparer.Ordinal))
                {
                    normalized = choice;
                    error = null;
                    return true;
                }

                normalized = string.Empty;
                error = $"{field.Label} must match one of the allowed choices.";
                return false;

            case PluginSettingType.Path:
                var path = (rawValue ?? string.Empty).Trim();
                if (path.Any(char.IsControl))
                {
                    normalized = string.Empty;
                    error = $"{field.Label} cannot contain control characters.";
                    return false;
                }

                if (path.Length > MaxSettingPathLength)
                {
                    normalized = string.Empty;
                    error = $"{field.Label} exceeds {MaxSettingPathLength} characters.";
                    return false;
                }

                normalized = path;
                error = null;
                return true;

            default:
                var str = rawValue ?? string.Empty;
                if (str.Contains('\0'))
                {
                    normalized = string.Empty;
                    error = $"{field.Label} cannot contain NUL characters.";
                    return false;
                }

                if (str.Length > FilePluginSettingsStore.MaxValueLength)
                {
                    normalized = string.Empty;
                    error = $"{field.Label} exceeds {FilePluginSettingsStore.MaxValueLength} characters.";
                    return false;
                }

                normalized = str;
                error = null;
                return true;
        }
    }

    private static string? SanitizePathDefault(string? rawPath)
    {
        if (rawPath is null)
        {
            return null;
        }

        var trimmed = rawPath.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsControl) || trimmed.Length > MaxSettingPathLength)
        {
            return string.Empty;
        }

        return trimmed;
    }

    private static string? SanitizeStringDefault(string? rawValue)
    {
        if (rawValue is null)
        {
            return null;
        }

        var cleaned = rawValue.Replace("\0", string.Empty, StringComparison.Ordinal);
        return cleaned.Length > FilePluginSettingsStore.MaxValueLength
            ? cleaned[..FilePluginSettingsStore.MaxValueLength]
            : cleaned;
    }

    private void ValidateSurfaceAndId(PluginDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Id);
        if (IsReservedHostId(descriptor.Id))
        {
            throw new InvalidOperationException($"Plugin identifier '{descriptor.Id}' is reserved by the plugin host.");
        }

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

    /// <summary>Checks the live capability indexes without activating any plugin.</summary>
    public void ValidatePackageSourceOwnership(PluginDescriptor descriptor, string? replacingId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (FindSourceConflictLocked(descriptor, replacingId) is { } owner)
            {
                throw new InvalidDataException($"The package source id is already used by '{owner}'.");
            }
        }
    }

    private string? FindSourceConflictLocked(PluginDescriptor descriptor, string? ignoredOwner = null)
    {
        if (descriptor.SourceId == Guid.Empty)
        {
            return null;
        }

        if (descriptor.Capabilities.HasFlag(PluginCapabilityKind.LibrarySource))
        {
            if (sourceIndex.TryGetValue(descriptor.SourceId, out var existing) && existing.PluginId != ignoredOwner)
            {
                return existing.PluginId;
            }

            if (lazySourceOwners.TryGetValue(descriptor.SourceId, out var lazyOwner) && lazyOwner != ignoredOwner)
            {
                return lazyOwner;
            }
        }

        if (descriptor.Capabilities.HasFlag(PluginCapabilityKind.Launch))
        {
            if (launchIndex.TryGetValue(descriptor.SourceId, out var existing) && existing.PluginId != ignoredOwner)
            {
                return existing.PluginId;
            }

            if (lazyLaunchOwners.TryGetValue(descriptor.SourceId, out var lazyOwner) && lazyOwner != ignoredOwner)
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
