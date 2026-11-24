using System.Globalization;
using Playnite.Core.Database;
using Playnite.Core.Library;
using Playnite.Core.Models;
using Playnite.Core.Queries;
using Playnite.Steam;
using Playnite.Steam.Sync;

namespace Playnite.Desktop.Shell;

/// <summary>One displayed library row, detached from the database session.</summary>
public sealed record LibraryRow(Guid Id, string Name, string GameId)
{
    internal uint? SteamPlayAppId { get; init; }

    public bool IsInstalled { get; init; }

    public bool Favorite { get; init; }

    public bool Hidden { get; init; }

    public bool IsSteam { get; init; }

    public ulong PlaytimeSeconds { get; init; }

    public int? ReleaseYear { get; init; }

    public int? UserScore { get; init; }

    public DateTime? LastActivity { get; init; }

    public string? CoverImage { get; init; }

    public string? BackgroundImage { get; init; }

    public string? Icon { get; init; }

    public string? Description { get; init; }

    public string? Version { get; init; }

    public string? InstallDirectory { get; init; }

    public string? GroupLabel { get; init; }

    public string Initials => ComputeInitials(Name);

    public string StatusLabel => IsInstalled ? "Installed" : "Not installed";

    public string SourceDisplay => IsSteam ? "Steam" : "Library";

    public string ReleaseYearDisplay => ReleaseYear is int year
        ? year.ToString(CultureInfo.InvariantCulture)
        : "Unknown";

    public string UserScoreDisplay => UserScore is int score
        ? string.Create(CultureInfo.InvariantCulture, $"{score}/100")
        : "Unrated";

    public string LastPlayedDisplay => LastActivity is DateTime dt && dt > DateTime.MinValue
        ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : "Never";

    public string PlaytimeDisplay
    {
        get
        {
            if (PlaytimeSeconds == 0)
            {
                return "Never played";
            }

            if (PlaytimeSeconds < 3600)
            {
                var minutes = Math.Max(1UL, PlaytimeSeconds / 60);
                return string.Create(CultureInfo.InvariantCulture, $"{minutes}m");
            }

            var hours = PlaytimeSeconds / 3600;
            var remMinutes = (PlaytimeSeconds % 3600) / 60;
            return remMinutes > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{hours}h {remMinutes}m")
                : string.Create(CultureInfo.InvariantCulture, $"{hours}h");
        }
    }

    public string CardSubtitle
    {
        get
        {
            var yearText = ReleaseYear is int year ? year.ToString(CultureInfo.InvariantCulture) : null;
            var playtimeText = PlaytimeSeconds > 0 ? PlaytimeDisplay : StatusLabel;
            return yearText is not null ? $"{yearText} · {playtimeText}" : playtimeText;
        }
    }

    private static string ComputeInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            return string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]));
        }

        var single = parts[0];
        return single.Length >= 2
            ? string.Concat(char.ToUpperInvariant(single[0]), char.ToUpperInvariant(single[1]))
            : char.ToUpperInvariant(single[0]).ToString();
    }
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
/// p7-004 adds the search term, quick filter, sort, grouping, artwork metadata, and on-demand
/// Steam sync reload while preserving the p7-003 selection, details and paging behaviour.
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
    private CancellationTokenSource? lifetimeCts;
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

    internal CancellationToken LifetimeToken => lifetimeCts?.Token ?? lifetimeToken;

    internal string DatabasePath => databasePath;

    internal LibraryViewModel CreateIndependentView() => new(databasePath);

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

    /// <summary>The active library grouping key.</summary>
    public LibraryGroupBy GroupBy { get; private set; } = LibraryGroupBy.None;

    /// <summary>Groups returned for the active page when <see cref="GroupBy"/> is active.</summary>
    public IReadOnlyList<LibraryGroup> Groups { get; private set; } = [];

    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchTerm)
        || FilterMode != LibraryFilterMode.All
        || GroupBy != LibraryGroupBy.None;

    /// <summary>A stable, testable summary of the current query state.</summary>
    public string QueryStatus => GroupBy == LibraryGroupBy.None
        ? $"Search: {SearchTerm} | Filter: {FilterMode} | Sort: {SortBy} | Direction: {(Descending ? "desc" : "asc")}"
        : $"Search: {SearchTerm} | Filter: {FilterMode} | Sort: {SortBy} | Direction: {(Descending ? "desc" : "asc")} | Group: {GroupBy}";

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
        lifetimeCts?.Cancel();
        lifetimeCts?.Dispose();
        lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetimeToken = lifetimeCts.Token;
        await LoadPageAsync(0, lifetimeToken).ConfigureAwait(false);
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
            GroupBy: GroupBy,
            Offset: offset,
            Limit: PageSize);

        var page = await QueryPageAsync(service, query, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var groupLabels = BuildGroupLabels(query.GroupBy, page.Groups);

        // Compute the whole result before publishing it. A superseded request never writes its
        // rows, selection or status over the current query's result.
        var rows = page.Games
            .Select(game => CreateRow(game.Value, groupLabels.GetValueOrDefault(game.Value.Id)))
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
        Groups = page.Groups;
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
    /// page under the current term, filter, sort and group. Paging is always reset to page 1.
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

    /// <summary>Sets the grouping key and reloads page 1.</summary>
    public Task SetGroupByAsync(LibraryGroupBy groupBy, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(groupBy))
        {
            throw new ArgumentOutOfRangeException(nameof(groupBy));
        }

        GroupBy = groupBy;
        return ApplyQueryAsync(cancellationToken);
    }

    /// <summary>Resets search, filter, and grouping back to defaults and reloads page 1.</summary>
    public Task ResetFiltersAsync(CancellationToken cancellationToken = default)
    {
        SearchTerm = string.Empty;
        FilterMode = LibraryFilterMode.All;
        GroupBy = LibraryGroupBy.None;
        return ApplyQueryAsync(cancellationToken);
    }

    /// <summary>Reloads the active page after an out-of-band sync or edit, clamping past-end offsets.</summary>
    public async Task ReloadCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        await LoadPageAsync(pageOffset, cancellationToken).ConfigureAwait(false);
        if (pageOffset > 0 && Rows.Count == 0 && TotalCount > 0)
        {
            var lastValidOffset = ((TotalCount - 1) / PageSize) * PageSize;
            await LoadPageAsync(lastValidOffset, cancellationToken).ConfigureAwait(false);
        }
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

        return CreateRow(stored.Value, row.GroupLabel);
    }

    private static LibraryRow CreateRow(Game game, string? groupLabel = null)
    {
        var isSteam = game.PluginId == SteamPluginId;
        uint? steamAppId = isSteam && game.IsInstalled
            && FlatpakSteamLauncher.TryParseAppId(game.GameId, out var appId) && appId != 0 ? appId : null;
        return new LibraryRow(game.Id, game.Name ?? "(unnamed)", game.GameId ?? string.Empty)
        {
            SteamPlayAppId = steamAppId,
            IsInstalled = game.IsInstalled,
            Favorite = game.Favorite,
            Hidden = game.Hidden,
            IsSteam = isSteam,
            PlaytimeSeconds = game.Playtime,
            ReleaseYear = game.ReleaseYear,
            UserScore = game.UserScore,
            LastActivity = game.LastActivity,
            CoverImage = game.CoverImage,
            BackgroundImage = game.BackgroundImage,
            Icon = game.Icon,
            Description = game.Description,
            Version = game.Version,
            InstallDirectory = game.InstallDirectory,
            GroupLabel = groupLabel,
        };
    }

    private static Dictionary<Guid, string> BuildGroupLabels(LibraryGroupBy groupBy, IReadOnlyList<LibraryGroup> groups)
    {
        var map = new Dictionary<Guid, string>();
        if (groupBy == LibraryGroupBy.None || groups.Count == 0)
        {
            return map;
        }

        foreach (var group in groups)
        {
            var label = FormatGroupKey(groupBy, group.Key);
            foreach (var game in group.Games)
            {
                map[game.Value.Id] = label;
            }
        }

        return map;
    }

    internal static string FormatGroupKey(LibraryGroupBy groupBy, IComparable? key) => groupBy switch
    {
        LibraryGroupBy.InstallationStatus when key is Playnite.SDK.Models.InstallationStatus status =>
            status == Playnite.SDK.Models.InstallationStatus.Installed ? "Installed" : "Not installed",
        LibraryGroupBy.ReleaseYear => key is int year ? year.ToString(CultureInfo.InvariantCulture) : "Unknown year",
        LibraryGroupBy.PlayTime when key is PlaytimeCategory category => category switch
        {
            PlaytimeCategory.NotPlayed => "Never played",
            PlaytimeCategory.LessThenHour => "< 1 hour",
            PlaytimeCategory.O1_10 => "1–10 hours",
            PlaytimeCategory.O10_100 => "10–100 hours",
            PlaytimeCategory.O100_500 => "100–500 hours",
            PlaytimeCategory.O500_1000 => "500–1000 hours",
            PlaytimeCategory.O1000plus => "1000+ hours",
            _ => category.ToString(),
        },
        LibraryGroupBy.UserScore or LibraryGroupBy.CriticScore or LibraryGroupBy.CommunityScore when key is ScoreGroup score => score switch
        {
            ScoreGroup.None => "Unrated",
            ScoreGroup.O0x => "0–9",
            ScoreGroup.O1x => "10–19",
            ScoreGroup.O2x => "20–29",
            ScoreGroup.O3x => "30–39",
            ScoreGroup.O4x => "40–49",
            ScoreGroup.O5x => "50–59",
            ScoreGroup.O6x => "60–69",
            ScoreGroup.O7x => "70–79",
            ScoreGroup.O8x => "80–89",
            ScoreGroup.O9x => "90–100",
            _ => score.ToString(),
        },
        _ => key?.ToString() ?? "None",
    };

    /// <summary>Releases the instance-owned references; the host view calls this on unload.</summary>
    public void Dispose()
    {
        lifetimeCts?.Cancel();
        lifetimeCts?.Dispose();
        lifetimeCts = null;
        queryCts?.Cancel();
        queryCts?.Dispose();
        queryCts = null;
        currentQueryToken = default;
        Rows = [];
        Groups = [];
        Selected = null;
        service = null;
        database = null;
        Status = "disposed";
    }
}
