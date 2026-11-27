using Playnite.Core.Models;

namespace Playnite.Core.Plugins;

/// <summary>Host-facing descriptor of a game source contributed by a plugin.</summary>
public sealed record LibrarySourceInfo(
    Guid SourceId,
    string DisplayName,
    string FilterLabel,
    string? DefaultStatusLabel = null,
    bool SupportsSync = false,
    bool IsCloudStreaming = false);

/// <summary>Marks a plugin capability that contributes a library source identity and labels.</summary>
public interface ILibrarySourceCapability
{
    /// <summary>Gets the library source descriptor.</summary>
    LibrarySourceInfo Source { get; }
}

/// <summary>Host-executed launch mechanism produced by a launch capability.</summary>
public enum PluginLaunchKind
{
    None = 0,
    TrackedProcess = 1,
    UriHandoff = 2,
    BrowserHandoff = 3,
}

/// <summary>
/// Pure launch plan returned by a plugin; the host owns validation and process/URI execution.
/// </summary>
public sealed record PluginLaunchSpec(
    PluginLaunchKind Kind,
    string TargetDisplay,
    string? Executable = null,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    string? Uri = null,
    string? Error = null)
{
    /// <summary>Gets whether the launch specification can be executed by the host.</summary>
    public bool CanLaunch => Kind != PluginLaunchKind.None && Error is null;
}

/// <summary>Resolves launch descriptions and executable/URI plans for games owned by a source.</summary>
public interface ILaunchCapability
{
    /// <summary>Gets the owning source identifier matched against <see cref="Game.PluginId"/>.</summary>
    Guid SourceId { get; }

    /// <summary>Describes the launch target for UI display without spawning processes.</summary>
    PluginLaunchSpec DescribeLaunch(Game game);

    /// <summary>Resolves a concrete host-executable launch plan or diagnostic error.</summary>
    PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null);
}

/// <summary>UI placement for a plugin-contributed command.</summary>
public enum PluginCommandPlacement
{
    MainMenu = 0,
    LibraryToolbar = 1,
    GameContext = 2,
}

/// <summary>Descriptor for a command contributed by a plugin.</summary>
public sealed record PluginCommandDescriptor(
    string Id,
    string PluginId,
    string Label,
    PluginCommandPlacement Placement,
    Guid? SourceId = null);

/// <summary>Invocation context passed to <see cref="ICommandCapability.ExecuteAsync"/>.</summary>
public sealed record PluginCommandContext(
    Guid? GameId,
    IReadOnlyDictionary<string, string>? Arguments = null);

/// <summary>Outcome of executing a plugin command.</summary>
public sealed record PluginCommandResult(bool Succeeded, string Message);

/// <summary>Provides host-invocable commands.</summary>
public interface ICommandCapability
{
    /// <summary>Gets the commands registered by this capability.</summary>
    IReadOnlyList<PluginCommandDescriptor> Commands { get; }

    /// <summary>Executes one command by identifier.</summary>
    Task<PluginCommandResult> ExecuteAsync(
        string commandId,
        PluginCommandContext context,
        CancellationToken cancellationToken);
}

/// <summary>Canonical host lifecycle event names.</summary>
public static class PluginEvents
{
    public const string AppStarted = "app.started";
    public const string LibrarySynced = "library.synced";
    public const string GameStarting = "game.starting";
    public const string GameStopped = "game.stopped";
}

/// <summary>Event payload dispatched through <see cref="PluginHost.PublishEventAsync"/>.</summary>
public sealed record PluginEvent(
    string Name,
    DateTimeOffset Timestamp,
    Guid? GameId = null,
    Guid? SourceId = null,
    IReadOnlyDictionary<string, string>? Payload = null);

/// <summary>Receives indexed lifecycle events published by the host.</summary>
public interface IEventHookCapability
{
    /// <summary>Gets the exact event names subscribed to by this capability.</summary>
    IReadOnlyList<string> SubscribedEvents { get; }

    /// <summary>Handles a published host event.</summary>
    Task OnEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken);
}

/// <summary>Declarative setting field types for host-rendered plugin settings.</summary>
public enum PluginSettingType
{
    String = 0,
    Path = 1,
    Boolean = 2,
    Integer = 3,
    Choice = 4,
}

/// <summary>Declarative definition of one plugin configuration field.</summary>
public sealed record PluginSettingField(
    string Key,
    string Label,
    PluginSettingType Type,
    string? DefaultValue = null,
    IReadOnlyList<string>? Choices = null,
    string? Description = null);

/// <summary>Exposes declarative settings metadata for a plugin.</summary>
public interface IPluginSettingsCapability
{
    /// <summary>Gets the ordered setting fields declared by the plugin.</summary>
    IReadOnlyList<PluginSettingField> Fields { get; }
}
