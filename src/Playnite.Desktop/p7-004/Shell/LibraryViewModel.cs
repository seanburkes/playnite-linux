using System.Collections.ObjectModel;
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
    public Guid PluginId { get; init; }

    internal uint? SteamPlayAppId { get; init; }

    internal string? HeroicPlayUri { get; init; }

    internal string? LunaPlayUrl { get; init; }

    public bool IsInstalled { get; init; }

    public bool Favorite { get; init; }

    public bool OverrideInstallState { get; init; }

    internal string? MetadataSources { get; init; }

    public bool Hidden { get; init; }

    public bool IsSteam { get; init; }

    public ulong PlaytimeSeconds { get; init; }

    public ulong PlayCount { get; init; }

    public bool HasFilePlayAction { get; init; }

    internal bool HasEmulatorPlayAction { get; init; }

    internal bool HasTrackedPlayAction => HasFilePlayAction || HasEmulatorPlayAction;

    public string LaunchTargetDisplay { get; init; } = "Not configured";

    public bool CanPlay => SteamPlayAppId is not null || HasTrackedPlayAction || HeroicPlayUri is not null || LunaPlayUrl is not null;

    public string PlayCountDisplay => PlayCount switch
    {
        0 => "0",
        1 => "1 play",
        _ => string.Create(CultureInfo.InvariantCulture, $"{PlayCount} plays"),
    };

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

    public string StatusLabel => PluginId == StoreCatalog.LunaPluginId
        ? "Cloud streaming"
        : IsInstalled ? "Installed" : "Not installed";

    public string SourceDisplay => PluginId != Guid.Empty
        ? StoreCatalog.GetDisplayName(PluginId)
        : IsSteam ? "Steam" : "Library";

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
internal sealed record LibraryCollection(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

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

    /// <summary>The active store source filter.</summary>
    internal IReadOnlyList<LibraryCollection> Collections { get; private set; } = [new(null, "All collections")];

    internal Guid? CollectionId { get; private set; }

    internal string? ActiveCollectionName => Collections.FirstOrDefault(collection => collection.Id == CollectionId && collection.Id.HasValue)?.Name;

    internal Task SetCollectionAsync(Guid? collectionId)
    {
        CollectionId = collectionId;
        return ApplyQueryAsync();
    }

    public LibrarySourceFilter SourceFilter { get; private set; } = LibrarySourceFilter.All;

    /// <summary>Groups returned for the active page when <see cref="GroupBy"/> is active.</summary>
    public IReadOnlyList<LibraryGroup> Groups { get; private set; } = [];

    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchTerm)
        || FilterMode != LibraryFilterMode.All
        || GroupBy != LibraryGroupBy.None
        || SourceFilter != LibrarySourceFilter.All
        || CollectionId is not null;

    /// <summary>A stable, testable summary of the current query state.</summary>
    public string QueryStatus
    {
        get
        {
            var baseStatus = GroupBy == LibraryGroupBy.None
                ? $"Search: {SearchTerm} | Filter: {FilterMode} | Sort: {SortBy} | Direction: {(Descending ? "desc" : "asc")}"
                : $"Search: {SearchTerm} | Filter: {FilterMode} | Sort: {SortBy} | Direction: {(Descending ? "desc" : "asc")} | Group: {GroupBy}";
            if (SourceFilter != LibrarySourceFilter.All)
            {
                baseStatus += $" | Source: {SourceFilter}";
            }
            return ActiveCollectionName is { } name ? $"{baseStatus} | Collection: {name}" : baseStatus;
        }
    }

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
        var categories = await database!.ReadAsync(session => session.Metadata<Category>().GetAllAsync(), cancellationToken).ConfigureAwait(false);
        var collectionId = categories.Any(category => category.Value.Id == CollectionId) ? CollectionId : null;
        var query = new LibraryQuery(
            Filter: CreateFilter(collectionId),
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

        CollectionId = collectionId;
        var collections = new LibraryCollection[] { new(null, "All collections") }
            .Concat(categories.Select(category => new LibraryCollection(category.Value.Id, category.Value.Name ?? "(unnamed)"))
                .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(category => category.Id))
            .ToArray();
        if (!collections.SequenceEqual(Collections))
        {
            Collections = collections;
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

    /// <summary>Filters the library to a single store source (or All) and reloads page 1.</summary>
    public Task SetSourceFilterAsync(LibrarySourceFilter sourceFilter, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(sourceFilter))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFilter));
        }

        SourceFilter = sourceFilter;
        return ApplyQueryAsync(cancellationToken);
    }

    /// <summary>Resets search, filter, store source, and grouping back to defaults and reloads page 1.</summary>
    public Task ResetFiltersAsync(CancellationToken cancellationToken = default)
    {
        SearchTerm = string.Empty;
        FilterMode = LibraryFilterMode.All;
        GroupBy = LibraryGroupBy.None;
        SourceFilter = LibrarySourceFilter.All;
        CollectionId = null;
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

    private LibraryFilter? CreateFilter(Guid? collectionId)
    {
        var hasStoreFilter = StoreCatalog.TryGetFilterPluginId(SourceFilter, out var pluginId);
        if (FilterMode == LibraryFilterMode.All && !hasStoreFilter && collectionId is null)
        {
            return null;
        }

        var settings = new FilterSettings();
        switch (FilterMode)
        {
            case LibraryFilterMode.Installed:
                settings.IsInstalled = true;
                break;
            case LibraryFilterMode.Uninstalled:
                settings.IsUnInstalled = true;
                break;
            case LibraryFilterMode.Hidden:
                settings.Hidden = true;
                break;
            case LibraryFilterMode.Favorite:
                settings.Favorite = true;
                break;
        }

        if (hasStoreFilter)
        {
            settings.Library = new IdItemFilterItemProperties { Ids = [pluginId] };
        }

        if (collectionId is Guid categoryId)
        {
            settings.Category = new IdItemFilterItemProperties { Ids = [categoryId] };
        }

        return LibraryFilter.FromSettings(settings);
    }

    private async Task<LibraryRow> LoadDetailsAsync(LibraryRow row, CancellationToken cancellationToken)
    {
        return await database!.ReadAsync(async session =>
        {
            var stored = await session.Games.GetAsync(row.Id).ConfigureAwait(false);
            if (stored is null)
            {
                return row with
                {
                    SteamPlayAppId = null,
                    HeroicPlayUri = null,
                    LunaPlayUrl = null,
                    HasFilePlayAction = false,
                    HasEmulatorPlayAction = false,
                    LaunchTargetDisplay = "Not configured",
                };
            }

            var game = stored.Value;
            var origin = await session.StoreMetadata.GetAsync(row.Id).ConfigureAwait(false);
            var source = StoreCatalog.GetDisplayName(game.PluginId);
            var fields = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(game.Description))
            {
                fields.Add($"Description · {(game.Description == origin?.ImportedDescription ? source : "Library")}");
            }
            if (!string.IsNullOrWhiteSpace(game.CoverImage))
            {
                fields.Add($"Cover · {(game.CoverImage == origin?.ImportedCover ? source : "Library")}");
            }

            return CreateRow(game, row.GroupLabel) with { MetadataSources = string.Join("  |  ", fields) };
        }, cancellationToken).ConfigureAwait(false);
    }

    internal Task<TitlePreferenceState> ReadTitlePreferencesAsync(Guid? gameId) =>
        database!.ReadAsync(async session =>
        {
            var preference = await session.Titles.GetLibraryPreferenceAsync().ConfigureAwait(false);
            if (gameId is not Guid id)
            {
                return new TitlePreferenceState(preference, null, null, []);
            }

            var game = await session.Games.GetAsync(id).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Game {id} was not found.");
            return new TitlePreferenceState(preference, CreateRow(game.Value),
                await session.Titles.GetAsync(id).ConfigureAwait(false),
                await session.Titles.GetLinkedTitlesAsync(id).ConfigureAwait(false));
        }, LifetimeToken);

    internal Task<IEnumerable<StoreTitleCandidate>> FindLinkCandidatesAsync(Guid gameId, string search) =>
        database!.ReadAsync(session => session.Titles.FindLinkCandidatesAsync(gameId, search), LifetimeToken);

    internal async Task SaveTitlePreferenceAsync(Guid? gameId, TitlePreferenceSelection selection)
    {
        var previousSelection = Selected?.Id;
        await database!.WriteAsync(async session =>
        {
            var now = DateTime.UtcNow;
            if (gameId is Guid id && selection.UnlinkProviderId is Guid unlink)
            {
                await session.Titles.UnlinkAsync(id, unlink, now).ConfigureAwait(false);
            }
            else if (gameId is Guid game)
            {
                await session.Titles.SetGamePreferenceAsync(game, selection.ProviderId, now,
                    selection.PersonalTitle, selection.LinkSourceId).ConfigureAwait(false);
            }
            else
            {
                await session.Titles.SetLibraryPreferenceAsync(selection.ProviderId ?? Guid.Empty, now).ConfigureAwait(false);
            }
            return true;
        }, LifetimeToken).ConfigureAwait(false);
        await ReloadCurrentPageAsync(LifetimeToken).ConfigureAwait(false);
        if ((gameId ?? previousSelection) is Guid selectedId && Rows.Any(row => row.Id == selectedId))
        {
            await SelectByIdAsync(selectedId, LifetimeToken).ConfigureAwait(false);
        }
    }

    internal Task<StoredGame?> GetStoredGameAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var currentService = service ?? throw new InvalidOperationException("The library has not been opened.");
        return currentService.GetAsync(id, cancellationToken);
    }

    internal async Task<bool> SelectByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var match = Rows.FirstOrDefault(row => row.Id == id);
        if (match is not null)
        {
            await SelectAsync(match, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var stored = await GetStoredGameAsync(id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return false;
        }

        Selected = CreateRow(stored.Value);
        return true;
    }

    internal async Task<Guid> AddCustomGameAsync(
        GameLaunchEditorResult input,
        Func<string, string?>? toolResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (service is null)
        {
            throw new InvalidOperationException("The library has not been opened.");
        }

        var plan = GameLaunchPlanner.CreatePlayAction(
            input.ExecutablePath,
            input.Arguments,
            input.WorkingDirectory,
            input.UseGameMode,
            input.UseMangoHud,
            toolResolver);
        if (plan.Action is null || !string.IsNullOrWhiteSpace(plan.Error))
        {
            throw new ArgumentException(plan.Error ?? "Invalid launch configuration.", nameof(input));
        }

        var name = input.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Game name is required.", nameof(input));
        }

        var db = database ?? await LibraryDatabase.OpenAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var workDir = string.IsNullOrWhiteSpace(input.WorkingDirectory)
            ? Path.GetDirectoryName(input.ExecutablePath.Trim()) ?? string.Empty
            : input.WorkingDirectory.Trim();
        var game = new Game(id, "custom-" + id.ToString("N")[..8], name)
        {
            IsInstalled = true,
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            InstallDirectory = string.IsNullOrWhiteSpace(workDir) ? null : workDir,
            GameActions = [plan.Action],
        };

        await db.WriteAsync(async session =>
        {
            await session.Games.AddAsync(new StoredGame(game)).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        await ReloadCurrentPageAsync(cancellationToken).ConfigureAwait(false);
        await SelectByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return id;
    }

    internal async Task UpdateLaunchActionAsync(
        Guid gameId,
        GameLaunchEditorResult input,
        Func<string, string?>? toolResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (service is null)
        {
            throw new InvalidOperationException("The library has not been opened.");
        }

        var plan = GameLaunchPlanner.CreatePlayAction(
            input.ExecutablePath,
            input.Arguments,
            input.WorkingDirectory,
            input.UseGameMode,
            input.UseMangoHud,
            toolResolver);
        if (plan.Action is null || !string.IsNullOrWhiteSpace(plan.Error))
        {
            throw new ArgumentException(plan.Error ?? "Invalid launch configuration.", nameof(input));
        }

        var db = database ?? await LibraryDatabase.OpenAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await db.WriteAsync(async session =>
        {
            var existing = await session.Games.GetAsync(gameId).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Game {gameId} was not found.");
            var game = existing.Value;
            var newName = input.Name.Trim();
            var renamed = !string.IsNullOrWhiteSpace(newName) && game.Name != newName;
            if (renamed)
            {
                game.Name = newName;
            }

            game.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
            game.IsInstalled = true;
            if (game.PluginId != Guid.Empty)
            {
                game.OverrideInstallState = true;
            }
            var resolvedWorkDir = string.IsNullOrWhiteSpace(input.WorkingDirectory)
                ? Path.GetDirectoryName(input.ExecutablePath.Trim())
                : input.WorkingDirectory.Trim();
            game.InstallDirectory = string.IsNullOrWhiteSpace(resolvedWorkDir) ? null : resolvedWorkDir;

            var existingActions = game.GameActions?.Where(action => action is not null).ToList() ?? [];
            var filePlayIndex = existingActions.FindIndex(action =>
                action.IsPlayAction && action.Type == GameActionType.File);
            if (filePlayIndex >= 0)
            {
                existingActions[filePlayIndex] = plan.Action;
            }
            else
            {
                existingActions.Insert(0, plan.Action);
            }

            game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>(existingActions);
            await session.Games.UpdateAsync(new StoredGame(game, existing.LegacyJson)).ConfigureAwait(false);
            if (renamed && await session.Titles.GetAsync(gameId).ConfigureAwait(false) is not null)
            {
                await session.Titles.SetGamePreferenceAsync(gameId, Guid.Empty, DateTime.UtcNow, newName).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);

        await ReloadCurrentPageAsync(cancellationToken).ConfigureAwait(false);
        await SelectByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<Guid> SaveLunaGameAsync(LunaGameInput input, Guid? gameId, CancellationToken cancellationToken)
    {
        var db = database ?? throw new InvalidOperationException("The library has not been opened.");
        var name = input.Name.Trim();
        var url = LunaPlayRequest.ParseGameUrl(input.Url)
            ?? throw new ArgumentException(LunaPlayRequest.InvalidUrlMessage, nameof(input));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = gameId ?? Guid.NewGuid();
        await db.WriteAsync(async session =>
        {
            var existing = gameId is null ? null : await session.Games.GetAsync(id).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Game {id} was not found.");
            var game = existing?.Value ?? new Game(id, url.AbsolutePath, name) { PluginId = StoreCatalog.LunaPluginId };
            if (game.PluginId != StoreCatalog.LunaPluginId)
            {
                throw new InvalidOperationException("Only Amazon Luna games can be edited here.");
            }

            game.GameId = url.AbsolutePath;
            var renamed = game.Name != name;
            game.Name = name;
            var action = LunaPlayRequest.FindPlayAction(game);
            if (action is null)
            {
                action = new GameAction { Name = "Play", Type = GameActionType.URL, IsPlayAction = true };
                game.GameActions ??= [];
                game.GameActions.Insert(0, action);
            }

            action.Path = url.AbsoluteUri;
            if (existing is null)
            {
                await session.Games.AddAsync(new StoredGame(game)).ConfigureAwait(false);
            }
            else
            {
                await session.Games.UpdateAsync(new StoredGame(game, existing.LegacyJson)).ConfigureAwait(false);
                if (renamed && await session.Titles.GetAsync(id).ConfigureAwait(false) is not null)
                {
                    await session.Titles.SetGamePreferenceAsync(id, Guid.Empty, DateTime.UtcNow, name).ConfigureAwait(false);
                }
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);

        await ReloadCurrentPageAsync(cancellationToken).ConfigureAwait(false);
        await SelectByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return id;
    }

    internal async Task<(int Added, int Updated)> ImportSnesFolderAsync(
        string folder, SnesRomProfile profile, string? core, CancellationToken cancellationToken)
    {
        var db = database ?? throw new InvalidOperationException("The library has not been opened.");
        var paths = await Task.Run(() => SnesRomProfile.Scan(folder, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        var counts = await db.WriteAsync(async session =>
        {
            var added = 0;
            var updated = 0;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = SnesRomProfile.LibraryId(path);
                var existing = await session.Games.GetAsync(id).ConfigureAwait(false);
                if (existing is null)
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var game = new Game(id, path, name)
                    {
                        PluginId = SnesRomProfile.SourceId,
                        IsInstalled = true,
                        Roms = new ObservableCollection<GameRom> { new(name, path) },
                        GameActions = [profile.CreateAction(core)],
                    };
                    await session.Games.AddAsync(new StoredGame(game)).ConfigureAwait(false);
                    added++;
                }
                else
                {
                    var game = existing.Value;
                    if (game.PluginId != SnesRomProfile.SourceId || game.GameId != path)
                    {
                        throw new InvalidOperationException("The SNES ROM has a conflicting library identity.");
                    }

                    var action = SnesRomProfile.FindAction(game);
                    if (action?.EmulatorId == profile.Id && action.Path == core)
                    {
                        continue;
                    }

                    game.GameActions ??= [];
                    if (action is not null)
                    {
                        game.GameActions.Remove(action);
                    }

                    game.GameActions.Insert(0, profile.CreateAction(core));
                    await session.Games.UpdateAsync(existing).ConfigureAwait(false);
                    updated++;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (added, updated);
        }, cancellationToken).ConfigureAwait(false);

        await ReloadCurrentPageAsync(cancellationToken).ConfigureAwait(false);
        return counts;
    }

    private static LibraryRow CreateRow(Game game, string? groupLabel = null)
    {
        var isSteam = game.PluginId == SteamPluginId;
        uint? steamAppId = isSteam && game.IsInstalled
            && FlatpakSteamLauncher.TryParseAppId(game.GameId, out var appId) && appId != 0 ? appId : null;
        var heroicUri = game.IsInstalled
            && StoreCatalog.TryBuildHeroicLaunchUri(game.PluginId, game.GameId, out var builtHeroicUri)
            ? builtHeroicUri
            : null;
        var isLuna = game.PluginId == StoreCatalog.LunaPluginId;
        var lunaUrl = LunaPlayRequest.ParseGameUrl(LunaPlayRequest.FindPlayAction(game)?.Path)?.AbsoluteUri;
        var emulator = SnesRomProfile.FindAction(game);
        var hasFilePlay = !isLuna && GameLaunchPlanner.FindFilePlayAction(game) is not null;
        return new LibraryRow(game.Id, game.Name ?? "(unnamed)", game.GameId ?? string.Empty)
        {
            PluginId = game.PluginId,
            SteamPlayAppId = steamAppId,
            HeroicPlayUri = heroicUri,
            LunaPlayUrl = lunaUrl,
            HasFilePlayAction = hasFilePlay,
            HasEmulatorPlayAction = emulator is not null,
            LaunchTargetDisplay = isLuna
                ? lunaUrl is not null ? "Amazon Luna (Google Chrome)" : "Invalid Amazon Luna game link"
                : emulator?.Name ?? GameLaunchPlanner.DescribeLaunchTarget(game, game.PluginId, steamAppId),
            IsInstalled = game.IsInstalled,
            Favorite = game.Favorite,
            OverrideInstallState = game.OverrideInstallState,
            Hidden = game.Hidden,
            IsSteam = isSteam,
            PlaytimeSeconds = game.Playtime,
            PlayCount = game.PlayCount,
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
