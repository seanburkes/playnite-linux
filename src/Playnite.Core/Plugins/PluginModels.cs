namespace Playnite.Core.Plugins;

/// <summary>Application surfaces a plugin package can target independently.</summary>
[Flags]
public enum PluginSurface
{
    None = 0,
    Core = 1,
    Desktop = 2,
    Fullscreen = 4,
    Theme = 8,
}

/// <summary>Capability families declared by a plugin manifest and indexed by the host.</summary>
[Flags]
public enum PluginCapabilityKind
{
    None = 0,
    LibrarySource = 1,
    Launch = 2,
    Metadata = 4,
    Commands = 8,
    EventHooks = 16,
    Settings = 32,
    UiSlots = 64,
    Theme = 128,
}

/// <summary>How a plugin is hosted inside the runtime.</summary>
public enum PluginLoadKind
{
    BuiltIn = 0,
    ExternalAssembly = 1,
}

/// <summary>Lifecycle state of a plugin registered in <see cref="PluginHost"/>.</summary>
public enum PluginLifecycleState
{
    Registered = 0,
    Active = 1,
    Disabled = 2,
    Faulted = 3,
    Disposed = 4,
}

/// <summary>Relative entrypoints per runtime surface declared in a v2 plugin manifest.</summary>
public sealed record PluginEntryPoints(
    string? Core = null,
    string? Desktop = null,
    string? Fullscreen = null,
    string? Theme = null,
    string? EntryType = null);

/// <summary>
/// Metadata describing a built-in or manifest-discovered plugin without forcing assembly load.
/// </summary>
public sealed record PluginDescriptor(
    string Id,
    Guid SourceId,
    string Name,
    Version Version,
    PluginLoadKind LoadKind,
    PluginSurface Surfaces,
    PluginCapabilityKind Capabilities,
    PluginEntryPoints EntryPoints,
    string? RootDirectory = null,
    string? Description = null,
    bool EnabledByDefault = true);

/// <summary>Diagnostic emitted during plugin discovery, manifest parsing, loading, or execution.</summary>
public sealed record PluginDiagnostic(string PluginId, string Code, string Message);

/// <summary>Observable host status for one registered plugin.</summary>
public sealed record PluginRecord(
    PluginDescriptor Descriptor,
    PluginLifecycleState State,
    bool IsMaterialized,
    string? LastError = null);

/// <summary>Result of scanning an external plugins root directory.</summary>
public sealed record PluginDiscoveryResult(
    IReadOnlyList<PluginDescriptor> Discovered,
    IReadOnlyList<PluginDiagnostic> Diagnostics);
