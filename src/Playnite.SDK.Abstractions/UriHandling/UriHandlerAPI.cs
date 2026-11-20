using Playnite.SDK.Events;

namespace Playnite.SDK;

/// <summary>The host-facing URI source registration surface. Names follow the v10 SDK.</summary>
public interface IUriHandlerAPI
{
    /// <summary>Registers a source. The name is case-insensitive and <c>playnite</c> is reserved.</summary>
    void RegisterSource(string source, Action<PlayniteUriEventArgs> handler);

    /// <summary>Removes a source. An absent source is ignored, as in v10.</summary>
    void RemoveSource(string source);
}

/// <summary>How the host handled one application URI command.</summary>
public enum UriCommandStatus
{
    /// <summary>The host accepted and started the command.</summary>
    Completed = 0,

    /// <summary>The host cannot run the command in this session.</summary>
    Unavailable,

    /// <summary>The host was asked to run the command and failed.</summary>
    Failed,
}

/// <summary>The host's answer to one application URI command.</summary>
public sealed record UriCommandResult(UriCommandStatus Status, string? Diagnostic = null)
{
    /// <summary>A completed command.</summary>
    public static UriCommandResult Completed() => new(UriCommandStatus.Completed);

    /// <summary>An unavailable command with a diagnostic.</summary>
    public static UriCommandResult Unavailable(string diagnostic) => new(UriCommandStatus.Unavailable, diagnostic);

    /// <summary>A failed command with a diagnostic.</summary>
    public static UriCommandResult Failed(string diagnostic) => new(UriCommandStatus.Failed, diagnostic);
}

/// <summary>
/// The injectable command seam for <c>playnite://playnite</c> routes. The shell implements it;
/// the fake in the tests records every call. No method starts a process or opens a socket.
/// </summary>
public interface IUriCommandHost
{
    /// <summary>Launches a game, optionally through one play action.</summary>
    ValueTask<UriCommandResult> StartGameAsync(Guid gameId, int? actionIndex, CancellationToken cancellationToken);

    /// <summary>Selects a game in the main view.</summary>
    ValueTask<UriCommandResult> ShowGameAsync(Guid gameId, CancellationToken cancellationToken);

    /// <summary>Opens the search view with an optional term.</summary>
    ValueTask<UriCommandResult> SearchAsync(string? term, CancellationToken cancellationToken);

    /// <summary>Switches the main view.</summary>
    ValueTask<UriCommandResult> ShowViewAsync(PlayniteViewTarget view, CancellationToken cancellationToken);

    /// <summary>Applies one stored-field library filter.</summary>
    ValueTask<UriCommandResult> ApplyFilterAsync(PlayniteFilterTarget filter, CancellationToken cancellationToken);
}

/// <summary>How one external URI was handled.</summary>
public enum UriDispatchStatus
{
    /// <summary>The command or source handler ran successfully.</summary>
    Completed = 0,

    /// <summary>The URI is malformed or targets an unsupported command; nothing ran.</summary>
    Rejected,

    /// <summary>The URI names a plugin source that has no registered handler.</summary>
    UnhandledSource,

    /// <summary>No command host is available for an application route.</summary>
    Unavailable,

    /// <summary>The source handler or command host failed.</summary>
    Failed,
}

/// <summary>The result of one dispatch. A malformed URI is a result, never an exception.</summary>
public sealed record UriDispatchResult(UriDispatchStatus Status, PlayniteUriRoute? Route, string? Diagnostic = null)
{
    /// <summary>True when the command ran successfully.</summary>
    public bool Succeeded => Status == UriDispatchStatus.Completed;
}
