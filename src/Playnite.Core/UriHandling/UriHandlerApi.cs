using Playnite.SDK;
using Playnite.SDK.Events;

namespace Playnite.Core.UriHandling;

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
            return new UriDispatchResult(UriDispatchStatus.Rejected, null, error ?? "The URI is invalid.");
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
