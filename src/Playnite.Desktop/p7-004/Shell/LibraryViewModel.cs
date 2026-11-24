using Playnite.Core.Database;
using Playnite.Core.Library;
using Playnite.Core.Queries;
using Playnite.Steam;
using Playnite.Steam.Sync;

namespace Playnite.Desktop.Shell;

/// <summary>One displayed library row, detached from the database session.</summary>
public sealed record LibraryRow(Guid Id, string Name, string GameId)
{
    internal uint? SteamPlayAppId { get; init; }
}

/// <summary>The bounded quick-filter selection owned by the p7-004 library query bar.</summary>
public enum LibraryFilterMode
{
    All,
    Installed,
    Uninstalled,
    Hidden,
    Favorite,
}

/// <summary>
/// Instance-owned library state for the ContentHost pilot. The database destination is the new
/// app-owned XDG data file; the frozen v10 fixture is only ever read by tests, never opened here.
/// Bounded p6-012 windows come from <see cref="LibraryService.ListAsync"/> in the selected order;
/// p7-004 adds the search term, quick filter and sort selection while preserving the p7-003
/// selection, details and paging behaviour.
/// </summary>
public sealed class LibraryViewModel
{
    public const int PageSize = 50;
    private static readonly Guid SteamPluginId = new(SteamLibrarySyncService.SteamPluginId);

    private readonly string databasePath;
    private LibraryDatabase? database;
    private LibraryService? service;
    private int pageOffset;
    private CancellationToken lifetimeToken;
    private CancellationToken currentQueryToken;
    private CancellationTokenSource? queryCts;
    private long generation;

    /// <summary>
    /// Test-only seam: when set, the query pipeline is replaced so a test can hold one query in
    /// flight while a superseding query completes. Production always uses the p6-001 service.
    /// </summary>
    internal Func<LibraryQuery, CancellationToken, Task<LibraryPage>>? QueryExecutor { get; set; }

    /// <summary>The token of the most recently started query, for targeted supersession handling.</summary>
    internal CancellationToken QueryToken => currentQueryToken;

    internal CancellationToken LifetimeToken => lifetimeToken;

    public LibraryViewModel(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
    }

    public IReadOnlyList<LibraryRow> Rows { get; private set; } = [];

    public LibraryRow? Selected { get; private set; }

    public int TotalCount { get; private set; }

    public string Status { get; private set; } = "unloaded";

    /// <summary>The active v10 name-search term; blank means an unfiltered list.</summary>
    public string SearchTerm { get; private set; } = string.Empty;

    /// <summary>The active bounded quick filter.</summary>
    public LibraryFilterMode FilterMode { get; private set; } = LibraryFilterMode.All;

    /// <summary>The active p6-012 sort key.</summary>
    public SortOrder SortBy { get; private set; } = SortOrder.Name;

    /// <summary>The active sort direction; it applies only to the primary key.</summary>
    public bool Descending { get; private set; }

    /// <summary>A stable, testable summary of the current query state.</summary>
    public string QueryStatus =>
        $"Search: {SearchTerm} | Filter: {FilterMode} | Sort: {SortBy} | Direction: {(Descending ? "desc" : "asc")}";

    public int PageCount => TotalCount == 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;

    public int CurrentPage => pageOffset / PageSize + 1;

    public bool CanGoPrevious => CurrentPage > 1;

    public bool CanGoNext => CurrentPage < PageCount;

    public string PageIndicator => $"Page {CurrentPage} / {PageCount}";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new ArgumentException("The library path must have a parent directory.", nameof(databasePath));
        Directory.CreateDirectory(directory);
        database = await LibraryDatabase.OpenAsync(databasePath, cancellationToken).ConfigureAwait(false);
        service = new LibraryService(database);
        lifetimeToken = cancellationToken;
        await LoadPageAsync(0, cancellationToken).ConfigureAwait(false);
    }

    public Task LoadPageAsync(int offset, CancellationToken cancellationToken = default)
        => LoadPageAsyncCore(offset, cancellationToken, Interlocked.Increment(ref generation));

    private async Task LoadPageAsyncCore(int offset, CancellationToken cancellationToken, long requestGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var service = this.service ?? throw new InvalidOperationException("The library has not been opened.");
        cancellationToken.ThrowIfCancellationRequested();
        var query = new LibraryQuery(
            Filter: CreateFilter(),
            SortBy: SortBy,
            Descending: Descending,
            GroupBy: LibraryGroupBy.None,
            Offset: offset,
            Limit: PageSize);

        var page = await QueryPageAsync(service, query, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Compute the whole result before publishing it. A superseded request never writes its
        // rows, selection or status over the current query's result.
        var rows = page.Games
            .Select(game => new LibraryRow(game.Value.Id, game.Value.Name ?? "(unnamed)", game.Value.GameId))
            .ToArray();
        var first = rows.Length > 0 ? rows[0] : null;
        var selected = first is null ? null : await LoadDetailsAsync(first, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (requestGeneration != Volatile.Read(ref generation))
        {
            return;
        }

        pageOffset = offset;
        TotalCount = page.TotalCount;
        Rows = rows;
        Selected = selected;
        Status = TotalCount == 0 ? "empty" : "loaded";
    }

    private async Task<LibraryPage> QueryPageAsync(LibraryService service, LibraryQuery query, CancellationToken cancellationToken)
    {
        if (QueryExecutor is not null)
        {
            return await QueryExecutor(query, cancellationToken).ConfigureAwait(false);
        }

        // A blank term uses the bounded p6-012 list path; a real term goes through the v10 matcher.
        return string.IsNullOrWhiteSpace(SearchTerm)
            ? await service.ListAsync(query, cancellationToken).ConfigureAwait(false)
            : await service.SearchAsync(SearchTerm, query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a query change: cancels the previous in-flight query and reloads the first bounded
    /// page under the current term, filter and sort. Paging is always reset to page 1.
    /// </summary>
    public Task ApplyQueryAsync(CancellationToken cancellationToken = default)
    {
        queryCts?.Cancel();
        queryCts?.Dispose();
        var token = cancellationToken == default ? lifetimeToken : cancellationToken;
        queryCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        currentQueryToken = queryCts.Token;
        return LoadPageAsync(0, currentQueryToken);
    }

    /// <summary>Sets the v10 name-search term (trimmed) and reloads page 1.</summary>
    public Task SetSearchTermAsync(string term, CancellationToken cancellationToken = default)
    {
        SearchTerm = term?.Trim() ?? string.Empty;
        return ApplyQueryAsync(cancellationToken);
    }

    /// <summary>Sets the bounded quick filter and reloads page 1.</summary>
    public Task SetFilterAsync(LibraryFilterMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        FilterMode = mode;
        return ApplyQueryAsync(cancellationToken);
    }

    /// <summary>Sets the sort key and direction and reloads page 1.</summary>
    public Task SetSortAsync(SortOrder sortBy, bool descending, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(sortBy))
        {
            throw new ArgumentOutOfRangeException(nameof(sortBy));
        }

        SortBy = sortBy;
        Descending = descending;
        return ApplyQueryAsync(cancellationToken);
    }

    /// <summary>Flips the sort direction and reloads page 1.</summary>
    public Task SetDescendingAsync(bool descending, CancellationToken cancellationToken = default)
    {
        Descending = descending;
        return ApplyQueryAsync(cancellationToken);
    }

    /// <summary>Loads the selected row's details through the service; used by the real row control.</summary>
    public async Task SelectAsync(LibraryRow? row, CancellationToken cancellationToken = default)
    {
        if (row is null)
        {
            Selected = null;
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Selected = await LoadDetailsAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task NextPageAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGoNext)
        {
            return;
        }

        await LoadPageAsync(CurrentPage * PageSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task PreviousPageAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGoPrevious)
        {
            return;
        }

        await LoadPageAsync((CurrentPage - 2) * PageSize, cancellationToken).ConfigureAwait(false);
    }

    private LibraryFilter? CreateFilter() => FilterMode switch
    {
        LibraryFilterMode.All => null,
        LibraryFilterMode.Installed => LibraryFilter.FromSettings(new FilterSettings { IsInstalled = true }),
        LibraryFilterMode.Uninstalled => LibraryFilter.FromSettings(new FilterSettings { IsUnInstalled = true }),
        LibraryFilterMode.Hidden => LibraryFilter.FromSettings(new FilterSettings { Hidden = true }),
        LibraryFilterMode.Favorite => LibraryFilter.FromSettings(new FilterSettings { Favorite = true }),
        _ => null,
    };

    private async Task<LibraryRow> LoadDetailsAsync(LibraryRow row, CancellationToken cancellationToken)
    {
        var stored = await service!.GetAsync(row.Id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return row with { SteamPlayAppId = null };
        }

        var game = stored.Value;
        uint? steamAppId = game.PluginId == SteamPluginId && game.IsInstalled
            && FlatpakSteamLauncher.TryParseAppId(game.GameId, out var appId) && appId != 0 ? appId : null;
        return row with { Name = game.Name ?? "(unnamed)", GameId = game.GameId, SteamPlayAppId = steamAppId };
    }

    /// <summary>Releases the instance-owned references; the host view calls this on unload.</summary>
    public void Dispose()
    {
        queryCts?.Cancel();
        queryCts?.Dispose();
        queryCts = null;
        currentQueryToken = default;
        Rows = [];
        Selected = null;
        service = null;
        database = null;
        Status = "disposed";
    }
}
