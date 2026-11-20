namespace Playnite.Core.UriHandling;

/// <summary>Arguments passed to a registered plugin URI source, mirroring the v10 shape.</summary>
public sealed class PlayniteUriEventArgs
{
    /// <summary>Gets or sets the decoded arguments after the source segment.</summary>
    public string[] Arguments { get; set; } = [];
}

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

    internal static UriDispatchResult Rejected(string diagnostic) =>
        new(UriDispatchStatus.Rejected, null, diagnostic);
}

/// <summary>
/// Parses, validates and dispatches the application's <c>playnite://</c> URI. Application
/// commands go to <see cref="IUriCommandHost"/>; plugin sources go to their registered
/// callback. Malformed or hostile input is rejected before either path runs, and neither path
/// uses a shell. Host and handler failures become a result instead of crashing the caller.
/// </summary>
public sealed class UriHandlerApi : IUriHandlerAPI
{
    /// <summary>The source name reserved for application commands.</summary>
    public const string ReservedSource = PlayniteUriRoute.AppSource;

    /// <summary>Startup diagnostic when no command host is registered.</summary>
    public const string MissingHostDiagnostic =
        "No URI command host is registered; application URI commands are unavailable.";

    private readonly IUriCommandHost? host;
    private readonly Dictionary<string, Action<PlayniteUriEventArgs>> handlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    /// <summary>Creates a handler over an optional command host.</summary>
    public UriHandlerApi(IUriCommandHost? host = null) => this.host = host;

    /// <summary>Gets a snapshot of the registered plugin source names.</summary>
    public IReadOnlyList<string> RegisteredSources
    {
        get
        {
            lock (gate)
            {
                return handlers.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void RegisterSource(string source, Action<PlayniteUriEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!PlayniteUriRoute.IsValidSourceName(source))
        {
            throw new ArgumentException(
                $"A URI source must be 1-{PlayniteUriRoute.MaxSourceLength} letters, digits, '.', '_', '~' or '-'.",
                nameof(source));
        }

        if (source.Equals(ReservedSource, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{ReservedSource}' is a reserved URI source.", nameof(source));
        }

        lock (gate)
        {
            if (!handlers.TryAdd(source, handler))
            {
                throw new InvalidOperationException($"URI source '{source}' is already registered.");
            }
        }
    }

    /// <inheritdoc />
    public void RemoveSource(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        lock (gate)
        {
            var existing = handlers.Keys.FirstOrDefault(
                key => key.Equals(source, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                handlers.Remove(existing);
            }
        }
    }

    /// <summary>
    /// Validates and dispatches one external URI. Caller cancellation propagates; every other
    /// handler or host failure is returned as <see cref="UriDispatchStatus.Failed"/>.
    /// </summary>
    public async Task<UriDispatchResult> DispatchAsync(
        string? uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!PlayniteUriRoute.TryParse(uri, out var route, out var error) || route is null)
        {
            return UriDispatchResult.Rejected(error ?? "The URI is invalid.");
        }

        if (route.Target == PlayniteUriTarget.CustomSource)
        {
            return DispatchSource(route, cancellationToken);
        }

        if (host is null)
        {
            return new UriDispatchResult(UriDispatchStatus.Unavailable, route, MissingHostDiagnostic);
        }

        try
        {
            var result = route.Target switch
            {
                PlayniteUriTarget.StartGame => await host
                    .StartGameAsync(route.GameId!.Value, route.ActionIndex, cancellationToken)
                    .ConfigureAwait(false),
                PlayniteUriTarget.ShowGame => await host
                    .ShowGameAsync(route.GameId!.Value, cancellationToken)
                    .ConfigureAwait(false),
                PlayniteUriTarget.Search => await host
                    .SearchAsync(route.SearchTerm, cancellationToken)
                    .ConfigureAwait(false),
                PlayniteUriTarget.ShowView => await host
                    .ShowViewAsync(route.View!.Value, cancellationToken)
                    .ConfigureAwait(false),
                PlayniteUriTarget.Filter => await host
                    .ApplyFilterAsync(route.Filter!, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown URI target {route.Target}."),
            };

            return Map(route, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new UriDispatchResult(
                UriDispatchStatus.Failed,
                route,
                $"The URI command host failed: {exception.GetType().Name}.");
        }
    }

    private UriDispatchResult DispatchSource(PlayniteUriRoute route, CancellationToken cancellationToken)
    {
        Action<PlayniteUriEventArgs>? handler;
        lock (gate)
        {
            handlers.TryGetValue(route.Source, out handler);
        }

        if (handler is null)
        {
            return new UriDispatchResult(
                UriDispatchStatus.UnhandledSource,
                route,
                $"URI source '{route.Source}' is not registered.");
        }

        try
        {
            handler(new PlayniteUriEventArgs { Arguments = route.Arguments.ToArray() });
            return new UriDispatchResult(UriDispatchStatus.Completed, route);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new UriDispatchResult(
                UriDispatchStatus.Failed,
                route,
                $"URI source '{route.Source}' failed: {exception.GetType().Name}.");
        }
    }

    private static UriDispatchResult Map(PlayniteUriRoute route, UriCommandResult? result)
    {
        if (result is null)
        {
            return new UriDispatchResult(
                UriDispatchStatus.Failed,
                route,
                "The URI command host returned no result.");
        }

        var status = result.Status switch
        {
            UriCommandStatus.Completed => UriDispatchStatus.Completed,
            UriCommandStatus.Unavailable => UriDispatchStatus.Unavailable,
            UriCommandStatus.Failed => UriDispatchStatus.Failed,
            _ => UriDispatchStatus.Failed,
        };

        return new UriDispatchResult(status, route, result.Diagnostic);
    }
}
