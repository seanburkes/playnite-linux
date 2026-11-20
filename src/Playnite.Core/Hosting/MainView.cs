using Playnite.SDK;
using Playnite.SDK.Models;

namespace Playnite.Core.Hosting;

/// <summary>Delegates every query to an optional main-view host.</summary>
public sealed class MainViewApi : IMainViewAPI
{
    /// <summary>Diagnostic recorded when no host was registered.</summary>
    public const string MissingHostDiagnostic =
        "No main-view host is registered; the shell query is unavailable.";

    private readonly IMainViewHost? host;

    /// <summary>Creates the API over an optional host.</summary>
    public MainViewApi(IMainViewHost? host = null) => this.host = host;

    /// <inheritdoc />
    public async Task<MainViewResponse> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is null)
        {
            return new MainViewResponse(HostOperationStatus.Unavailable, Diagnostic: MissingHostDiagnostic);
        }

        try
        {
            var state = await host.GetStateAsync(cancellationToken).ConfigureAwait(false);
            return state is null
                ? new MainViewResponse(
                    HostOperationStatus.Failed,
                    Diagnostic: "The main-view host returned no state.")
                : new MainViewResponse(HostOperationStatus.Completed, state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MainViewResponse(
                HostOperationStatus.Failed,
                Diagnostic: "The main-view host failed: " + exception.Message);
        }
    }

    /// <inheritdoc />
    public Task<MainViewGamesResponse> GetSelectedGamesAsync(CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task<IReadOnlyList<IGameView>>>? query =
            host is null ? null : host.GetSelectedGamesAsync;
        return QueryGamesAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MainViewGamesResponse> GetFilteredGamesAsync(CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task<IReadOnlyList<IGameView>>>? query =
            host is null ? null : host.GetFilteredGamesAsync;
        return QueryGamesAsync(query, cancellationToken);
    }

    private static async Task<MainViewGamesResponse> QueryGamesAsync(
        Func<CancellationToken, Task<IReadOnlyList<IGameView>>>? query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null)
        {
            return new MainViewGamesResponse(HostOperationStatus.Unavailable, diagnostic: MissingHostDiagnostic);
        }

        try
        {
            var games = await query(cancellationToken).ConfigureAwait(false);
            return games is null
                ? new MainViewGamesResponse(
                    HostOperationStatus.Failed,
                    diagnostic: "The main-view host returned no game list.")
                : new MainViewGamesResponse(HostOperationStatus.Completed, games);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MainViewGamesResponse(
                HostOperationStatus.Failed,
                diagnostic: "The main-view host failed: " + exception.Message);
        }
    }
}
