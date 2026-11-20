using Playnite.Core.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using CoreLibraryGroupBy = Playnite.Core.Library.LibraryGroupBy;
using CoreSortOrder = Playnite.Core.Queries.SortOrder;

namespace Playnite.Core.Hosting;

/// <summary>
/// Projects a Core game onto the SDK's read-only view. The Core model stays free of the
/// abstraction's types; a host wraps a game when it hands one to <see cref="IMainViewAPI"/>.
/// </summary>
public sealed class GameViewAdapter : IGameView
{
    private readonly Game game;

    /// <summary>Wraps an existing Core game.</summary>
    public GameViewAdapter(Game game) =>
        this.game = game ?? throw new ArgumentNullException(nameof(game));

    /// <inheritdoc />
    public Guid Id => game.Id;

    /// <inheritdoc />
    public string Name => game.Name ?? string.Empty;
}

/// <summary>
/// Maps Core query enums to and from the SDK host enums. Both keep the same v10 values, so a
/// host can translate a Core query without redefining either contract.
/// </summary>
public static class MainViewEnumAdapter
{
    /// <summary>Maps a Core sort order to the SDK contract.</summary>
    public static SortOrder ToSdk(CoreSortOrder order) =>
        Checked<SortOrder>((int)order, nameof(order));

    /// <summary>Maps an SDK sort order to the Core enum.</summary>
    public static CoreSortOrder ToCore(SortOrder order) =>
        Checked<CoreSortOrder>((int)order, nameof(order));

    /// <summary>Maps a Core grouping key to the SDK contract.</summary>
    public static LibraryGroupBy ToSdk(CoreLibraryGroupBy grouping) =>
        Checked<LibraryGroupBy>((int)grouping, nameof(grouping));

    /// <summary>Maps an SDK grouping key to the Core enum.</summary>
    public static CoreLibraryGroupBy ToCore(LibraryGroupBy grouping) =>
        Checked<CoreLibraryGroupBy>((int)grouping, nameof(grouping));

    private static TEnum Checked<TEnum>(int value, string parameter)
        where TEnum : struct, Enum
    {
        var mapped = (TEnum)Enum.ToObject(typeof(TEnum), value);
        if (!Enum.IsDefined(mapped))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                value,
                "Unknown " + parameter + " value.");
        }

        return mapped;
    }
}

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
