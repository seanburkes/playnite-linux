using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Playnite.Core.Database;
using Playnite.Core.Library;
using Playnite.Core.Models;
using Playnite.Core.Queries;
using Playnite.Desktop.Bootstrap;
using Playnite.Desktop.Fullscreen;
using Playnite.Desktop.Integration;
using Playnite.Platform.Sessions;

namespace Playnite.Desktop.Shell;

/// <summary>
/// The library grid/list/details experience with search/filter/sort/group controls, real local and
/// Steam artwork loading, rich metadata details, and on-demand Steam library sync. All p7-003 and
/// p7-004 control names, ContentHost hooks, and query-bar contracts remain preserved.
/// </summary>
public sealed partial class LibraryGridView : UserControl
{
    private const string SearchSmokeFlag = "--search-filter-sort-smoke";
    private const int CoverDecodeWidth = 320;
    private const int HeroDecodeWidth = 640;

    private static readonly SortOrder[] SortOrders =
    [
        SortOrder.Name,
        SortOrder.LastActivity,
        SortOrder.RecentActivity,
        SortOrder.ReleaseDate,
        SortOrder.Playtime,
        SortOrder.UserScore,
    ];

    private static readonly LibraryGroupBy[] GroupOrders =
    [
        LibraryGroupBy.None,
        LibraryGroupBy.InstallationStatus,
        LibraryGroupBy.ReleaseYear,
        LibraryGroupBy.PlayTime,
        LibraryGroupBy.UserScore,
        LibraryGroupBy.Name,
    ];

    private static readonly LibrarySourceFilter[] SourceOrders =
    [
        LibrarySourceFilter.All,
        LibrarySourceFilter.Steam,
        LibrarySourceFilter.Epic,
        LibrarySourceFilter.Gog,
        LibrarySourceFilter.Amazon,
        LibrarySourceFilter.Custom,
    ];

    private readonly BootstrapLocalization localization = new();
    private readonly bool searchSmoke;
    private readonly SteamPlayRequest steamPlay;
    private readonly HeroicPlayRequest heroicPlay;
    private readonly ILibrarySteamSync steamSync;
    private readonly ILibraryHeroicSync heroicSync;
    private readonly Func<string, LibraryArtworkLoader> artworkLoaderFactory;
    private DesktopGameSessionManager sessionManager;
    private readonly bool ownsSessionManager;
    private readonly Func<string, string?>? toolResolver;
    private readonly Button playButton;
    private readonly Button stopButton;
    private readonly Button configureLaunchButton;
    private readonly Button addGameButton;
    private readonly Border sessionBadge;
    private readonly TextBlock sessionBadgeText;
    private readonly TextBlock playStatus;
    private readonly Button fullscreenButton;
    private readonly Button syncSteamButton;
    private readonly Button syncHeroicButton;
    private readonly Button emptySyncButton;
    private readonly Button emptySyncHeroicButton;
    private readonly Button clearFiltersButton;
    private readonly Button gridViewButton;
    private readonly Button listViewButton;
    private readonly TextBox searchBox;
    private readonly ComboBox sortCombo;
    private readonly ComboBox groupCombo;
    private readonly ComboBox sourceCombo;
    private readonly ComboBox collectionCombo;
    private readonly Image detailsHeroImage;
    private readonly TextBlock detailsInitials;
    private readonly TextBlock syncStatus;
    private readonly List<LibraryCardItem> activeCards = [];
    private readonly List<Bitmap> activeCardBitmaps = [];

    private LibraryArtworkLoader? artworkLoader;
    private Bitmap? activeHeroBitmap;
    private CancellationTokenSource? artworkCts;
    private CancellationTokenSource? heroCts;
    private CancellationTokenSource? selectionCts;
    private CancellationTokenSource? navigationCts;
    private CancellationTokenSource? syncCts;
    private CancellationTokenSource? sessionCts;
    private int artworkGeneration;
    private int heroGeneration;
    private int selectionGeneration;
    private int navigationGeneration;
    private int syncGeneration;
    private int playGeneration;
    private int sessionGeneration;
    private int editorGeneration;
    private Guid? activeSessionGameId;
    private bool playPending;
    private bool syncPending;
    private bool fullscreenOpen;
    private bool sessionActive;
    private volatile bool sessionAcceptingUpdates;
    private bool stopPending;
    private bool editorPending;
    private bool listView;
    private bool updatingQueryControls;
    private bool rebuildingRows;
    private bool disposed;

    private LibraryViewModel? model;

    public LibraryGridView() : this(
        new SteamPlayRequest(new SteamPlayRequest.DesktopOpener()),
        true,
        new LibrarySteamSyncRunner(),
        null,
        null,
        null)
    {
    }

    internal LibraryGridView(SteamPlayRequest steamPlay, bool fullscreenPreview = false)
        : this(steamPlay, fullscreenPreview, new LibrarySteamSyncRunner(), null, null, null)
    {
    }

    internal LibraryGridView(
        SteamPlayRequest steamPlay,
        bool fullscreenPreview,
        ILibrarySteamSync steamSync,
        Func<string, LibraryArtworkLoader>? artworkLoaderFactory = null)
        : this(steamPlay, fullscreenPreview, steamSync, artworkLoaderFactory, null, null)
    {
    }

    internal LibraryGridView(
        SteamPlayRequest steamPlay,
        bool fullscreenPreview,
        ILibrarySteamSync steamSync,
        Func<string, LibraryArtworkLoader>? artworkLoaderFactory,
        DesktopGameSessionManager? sessionManager,
        Func<string, string?>? toolResolver = null,
        ILibraryHeroicSync? heroicSync = null,
        HeroicPlayRequest? heroicPlay = null)
    {
        this.steamPlay = steamPlay;
        this.heroicPlay = heroicPlay ?? new HeroicPlayRequest(new SteamPlayRequest.DesktopOpener());
        this.steamSync = steamSync;
        this.heroicSync = heroicSync ?? new HeroicLibrarySyncRunner();
        this.artworkLoaderFactory = artworkLoaderFactory ?? (path => new LibraryArtworkLoader(path));
        ownsSessionManager = sessionManager is null;
        this.sessionManager = sessionManager ?? new DesktopGameSessionManager();
        this.toolResolver = toolResolver;
        AvaloniaXamlLoader.Load(this);

        playButton = this.FindControl<Button>("PlayButton")!;
        stopButton = this.FindControl<Button>("StopButton")!;
        configureLaunchButton = this.FindControl<Button>("ConfigureLaunchButton")!;
        addGameButton = this.FindControl<Button>("AddGameButton")!;
        sessionBadge = this.FindControl<Border>("SessionBadge")!;
        sessionBadgeText = this.FindControl<TextBlock>("SessionBadgeText")!;
        playStatus = this.FindControl<TextBlock>("PlayStatus")!;
        fullscreenButton = this.FindControl<Button>("FullscreenButton")!;
        fullscreenButton.IsVisible = fullscreenPreview;
        syncSteamButton = this.FindControl<Button>("SyncSteamButton")!;
        syncHeroicButton = this.FindControl<Button>("SyncHeroicButton")!;
        emptySyncButton = this.FindControl<Button>("EmptySyncButton")!;
        emptySyncHeroicButton = this.FindControl<Button>("EmptySyncHeroicButton")!;
        clearFiltersButton = this.FindControl<Button>("ClearFiltersButton")!;
        gridViewButton = this.FindControl<Button>("GridViewButton")!;
        listViewButton = this.FindControl<Button>("ListViewButton")!;
        searchBox = this.FindControl<TextBox>("SearchBox")!;
        sortCombo = this.FindControl<ComboBox>("SortCombo")!;
        groupCombo = this.FindControl<ComboBox>("GroupCombo")!;
        sourceCombo = this.FindControl<ComboBox>("SourceCombo")!;
        collectionCombo = this.FindControl<ComboBox>("CollectionCombo")!;
        detailsHeroImage = this.FindControl<Image>("DetailsHeroImage")!;
        detailsInitials = this.FindControl<TextBlock>("DetailsInitials")!;
        syncStatus = this.FindControl<TextBlock>("SyncStatus")!;

        playButton.Click += OnPlayClicked;
        stopButton.Click += OnStopClicked;
        configureLaunchButton.Click += OnConfigureLaunchClicked;
        addGameButton.Click += OnAddGameClicked;
        syncSteamButton.Click += OnSyncSteamClicked;
        syncHeroicButton.Click += OnSyncHeroicClicked;
        emptySyncButton.Click += OnSyncSteamClicked;
        emptySyncHeroicButton.Click += OnSyncHeroicClicked;
        clearFiltersButton.Click += OnClearFiltersClicked;
        gridViewButton.Click += (_, _) => SetViewMode(false);
        listViewButton.Click += (_, _) => SetViewMode(true);
        this.FindControl<ListBox>("GameList")!.SelectionChanged += OnSelectionChanged;
        this.FindControl<Button>("PreviousButton")!.Click += OnPreviousClick;
        this.FindControl<Button>("NextButton")!.Click += OnNextClick;

        searchBox.TextChanged += OnSearchChanged;
        this.FindControl<Button>("AllFilterButton")!.Click += OnFilterClicked;
        this.FindControl<Button>("InstalledFilterButton")!.Click += OnFilterClicked;
        this.FindControl<Button>("UninstalledFilterButton")!.Click += OnFilterClicked;
        this.FindControl<Button>("HiddenFilterButton")!.Click += OnFilterClicked;
        this.FindControl<Button>("FavoriteFilterButton")!.Click += OnFilterClicked;

        sortCombo.ItemsSource = SortOrders.Select(SortLabel).ToArray();
        sortCombo.SelectedIndex = 0;
        sortCombo.SelectionChanged += OnSortChanged;
        this.FindControl<Button>("SortDirectionButton")!.Click += OnSortDirectionClicked;

        groupCombo.ItemsSource = GroupOrders.Select(GroupLabel).ToArray();
        groupCombo.SelectedIndex = 0;
        groupCombo.SelectionChanged += OnGroupChanged;

        sourceCombo.ItemsSource = SourceOrders.Select(StoreCatalog.GetFilterLabel).ToArray();
        sourceCombo.SelectedIndex = 0;
        sourceCombo.SelectionChanged += OnSourceChanged;
        collectionCombo.SelectionChanged += (_, _) =>
        {
            if (CanDispatchQuery() && collectionCombo.SelectedItem is LibraryCollection collection)
            {
                StartQuery(model!.SetCollectionAsync(collection.Id), model.QueryToken);
            }
        };

        SetViewMode(false);
        ApplyLabels();
        searchSmoke = Environment.GetCommandLineArgs().Contains(SearchSmokeFlag, StringComparer.Ordinal);
        this.sessionManager.SessionUpdated += OnSessionUpdated;
        Unloaded += OnUnloaded;
    }

    /// <summary>Completes when the most recent control-driven selection has been applied.</summary>
    internal Task SelectionTask { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the most recent control-driven page navigation has been applied.</summary>
    internal Task NavigationTask { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the most recent control-driven query change has been applied.</summary>
    internal Task QueryTask { get; private set; } = Task.CompletedTask;

    internal Task PlayTask { get; private set; } = Task.CompletedTask;

    internal Task SessionTask { get; private set; } = Task.CompletedTask;

    internal Task StopTask { get; private set; } = Task.CompletedTask;

    internal Task EditorTask { get; private set; } = Task.CompletedTask;

    internal Task UnloadTask { get; private set; } = Task.CompletedTask;

    internal Task SyncTask { get; private set; } = Task.CompletedTask;

    internal Task ArtworkTask { get; private set; } = Task.CompletedTask;

    internal Task HeroArtworkTask { get; private set; } = Task.CompletedTask;

    internal bool IsListViewMode => listView;

    public void Apply(LibraryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (ownsSessionManager && sessionManager.IsDisposed)
        {
            sessionManager.SessionUpdated -= OnSessionUpdated;
            sessionManager = new DesktopGameSessionManager();
        }

        sessionManager.SessionUpdated -= OnSessionUpdated;
        sessionManager.SessionUpdated += OnSessionUpdated;

        var keepSession = sessionActive;
        CancelLocalOperations(preserveActiveSession: keepSession);
        disposed = false;
        syncPending = false;
        playPending = false;
        fullscreenOpen = false;
        editorPending = false;
        if (!keepSession)
        {
            activeSessionGameId = null;
            stopPending = false;
            playStatus.Text = string.Empty;
        }

        syncStatus.Text = string.Empty;
        model = viewModel;
        RebuildRows();
        Refresh();
        if (searchSmoke)
        {
            _ = EmitSearchSmokeAsync();
        }
    }

    private void CancelLocalOperations(bool preserveActiveSession = false)
    {
        selectionGeneration++;
        navigationGeneration++;
        syncGeneration++;
        playGeneration++;
        editorGeneration++;

        selectionCts?.Cancel();
        selectionCts?.Dispose();
        selectionCts = null;

        navigationCts?.Cancel();
        navigationCts?.Dispose();
        navigationCts = null;

        syncCts?.Cancel();
        syncCts?.Dispose();
        syncCts = null;

        if (!preserveActiveSession)
        {
            Interlocked.Increment(ref sessionGeneration);
            sessionAcceptingUpdates = false;
            sessionCts?.Cancel();
            sessionCts?.Dispose();
            sessionCts = null;
        }
    }

    private void RebuildRows()
    {
        if (model is null)
        {
            return;
        }

        DisposeCardBitmaps();

        var list = this.FindControl<ListBox>("GameList")!;
        rebuildingRows = true;
        try
        {
            list.Items.Clear();
            var selectedIndex = -1;
            for (var index = 0; index < model.Rows.Count; index++)
            {
                var row = model.Rows[index];
                var card = new LibraryCardItem(row);
                activeCards.Add(card);

                var item = new ListBoxItem
                {
                    Name = "Row" + index.ToString(CultureInfo.InvariantCulture),
                    Tag = row,
                    DataContext = card,
                    Content = new TextBlock
                    {
                        Text = row.Name,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                AutomationProperties.SetName(item, row.Name);
                item.Classes.Set("grid-card", !listView);
                item.Classes.Set("list-row", listView);
                list.Items.Add(item);
                if (model.Selected is not null && model.Selected.Id == row.Id)
                {
                    selectedIndex = index;
                }
            }

            // Keep the real control's selection in step with the view-model: first row after a load or
            // page reset, -1 for an empty library, and the same identity when a user-selected row
            // survives a rebuild.
            list.SelectedIndex = selectedIndex;
        }
        finally
        {
            rebuildingRows = false;
        }

        SelectionTask = Task.CompletedTask;
        StartCardArtworkLoading();
    }

    private void Refresh()
    {
        if (model is null)
        {
            return;
        }

        RefreshDetails(model.Selected);
        RefreshLibraryActions();
        this.FindControl<TextBlock>("LibraryCountText")!.Text =
            string.Create(CultureInfo.InvariantCulture, $"({model.TotalCount})");
        this.FindControl<TextBlock>("PageText")!.Text = model.PageIndicator;
        this.FindControl<Border>("EmptyState")!.IsVisible = model.Rows.Count == 0;
        clearFiltersButton.IsVisible = model.HasActiveFilters;
        this.FindControl<TextBlock>("QueryStatus")!.Text =
            $"{localization.Resolve(LOC.SearchLabel)}: {model.SearchTerm} | " +
            $"{localization.Resolve(LOC.Filter)}: {FilterLabel(model.FilterMode)} | " +
            $"{localization.Resolve(LOC.MenuSortByTitle)}: {SortLabel(model.SortBy)} " +
            $"{localization.Resolve(model.Descending ? LOC.MenuSortDescending : LOC.MenuSortAscending)}" +
            (model.GroupBy != LibraryGroupBy.None
                ? $" | {localization.Resolve(LOC.MenuGroupByTitle)}: {GroupLabel(model.GroupBy)}"
                : string.Empty) +
            (model.SourceFilter != LibrarySourceFilter.All
                ? $" | Source: {StoreCatalog.GetFilterLabel(model.SourceFilter)}"
                : string.Empty) +
            (model.ActiveCollectionName is { } collection ? $" | Collection: {collection}" : string.Empty);
        this.FindControl<Button>("SortDirectionButton")!.Content =
            localization.Resolve(model.Descending ? LOC.MenuSortDescending : LOC.MenuSortAscending);
        SyncQueryControlsFromModel();
        UpdateFilterHighlights();
    }

    private void SyncQueryControlsFromModel()
    {
        if (model is null)
        {
            return;
        }

        updatingQueryControls = true;
        try
        {
            var currentText = searchBox.Text ?? string.Empty;
            if (!string.Equals(currentText.Trim(), model.SearchTerm, StringComparison.Ordinal))
            {
                searchBox.Text = model.SearchTerm;
            }

            var sortIndex = Array.IndexOf(SortOrders, model.SortBy);
            if (sortIndex >= 0 && sortCombo.SelectedIndex != sortIndex)
            {
                sortCombo.SelectedIndex = sortIndex;
            }

            var groupIndex = Array.IndexOf(GroupOrders, model.GroupBy);
            if (groupIndex >= 0 && groupCombo.SelectedIndex != groupIndex)
            {
                groupCombo.SelectedIndex = groupIndex;
            }

            if (!ReferenceEquals(collectionCombo.ItemsSource, model.Collections))
            {
                collectionCombo.ItemsSource = model.Collections;
            }

            collectionCombo.SelectedItem = model.Collections.FirstOrDefault(collection => collection.Id == model.CollectionId);
            var sourceIndex = Array.IndexOf(SourceOrders, model.SourceFilter);
            if (sourceIndex >= 0 && sourceCombo.SelectedIndex != sourceIndex)
            {
                sourceCombo.SelectedIndex = sourceIndex;
            }
        }
        finally
        {
            updatingQueryControls = false;
        }
    }

    private void RefreshDetails(LibraryRow? selected)
    {
        this.FindControl<Button>("GameTitleButton")!.IsVisible = selected is { PluginId: var pluginId } && pluginId != Guid.Empty;
        this.FindControl<TextBlock>("DetailsName")!.Text = selected?.Name ?? "(none)";
        this.FindControl<TextBlock>("DetailsId")!.Text = selected?.GameId ?? "(none)";
        detailsInitials.Text = selected?.Initials ?? "?";
        this.FindControl<TextBlock>("DetailsStatus")!.Text = selected is { OverrideInstallState: true } ? $"{selected.StatusLabel} · Manual" : selected?.StatusLabel ?? "—";
        this.FindControl<TextBlock>("DetailsSource")!.Text = selected?.SourceDisplay ?? "—";
        this.FindControl<TextBlock>("DetailsPlaytime")!.Text = selected?.PlaytimeDisplay ?? "—";
        this.FindControl<TextBlock>("DetailsLastPlayed")!.Text = selected?.LastPlayedDisplay ?? "—";
        this.FindControl<TextBlock>("DetailsReleaseYear")!.Text = selected?.ReleaseYearDisplay ?? "—";
        this.FindControl<TextBlock>("DetailsUserScore")!.Text = selected?.UserScoreDisplay ?? "—";
        this.FindControl<TextBlock>("DetailsVersion")!.Text =
            !string.IsNullOrWhiteSpace(selected?.Version) ? selected.Version : "—";
        this.FindControl<TextBlock>("DetailsInstallDir")!.Text =
            !string.IsNullOrWhiteSpace(selected?.InstallDirectory) ? selected.InstallDirectory : "—";
        this.FindControl<TextBlock>("DetailsPlayCount")!.Text = selected?.PlayCountDisplay ?? "—";
        this.FindControl<TextBlock>("DetailsLaunchTarget")!.Text = selected?.LaunchTargetDisplay ?? "—";
        this.FindControl<TextBlock>("DetailsDescription")!.Text =
            !string.IsNullOrWhiteSpace(selected?.Description) ? selected.Description : string.Empty;

        var sources = this.FindControl<TextBlock>("MetadataSources")!;
        sources.Text = selected?.MetadataSources;
        sources.IsVisible = !string.IsNullOrEmpty(sources.Text);
        StartHeroArtworkLoading(selected);
    }

    private void SetViewMode(bool useList)
    {
        listView = useList;
        var list = this.FindControl<ListBox>("GameList")!;
        var panelKey = useList ? "LibraryListPanel" : "LibraryGridPanel";
        if (this.TryFindResource(panelKey, out var resource) && resource is ITemplate<Panel?> template)
        {
            list.ItemsPanel = template;
        }

        foreach (var item in list.Items.OfType<ListBoxItem>())
        {
            item.Classes.Set("grid-card", !useList);
            item.Classes.Set("list-row", useList);
        }

        gridViewButton.Classes.Set("active", !useList);
        listViewButton.Classes.Set("active", useList);
    }

    private void UpdateFilterHighlights()
    {
        if (model is null)
        {
            return;
        }

        this.FindControl<Button>("AllFilterButton")!.Classes.Set("active", model.FilterMode == LibraryFilterMode.All);
        this.FindControl<Button>("InstalledFilterButton")!.Classes.Set("active", model.FilterMode == LibraryFilterMode.Installed);
        this.FindControl<Button>("UninstalledFilterButton")!.Classes.Set("active", model.FilterMode == LibraryFilterMode.Uninstalled);
        this.FindControl<Button>("HiddenFilterButton")!.Classes.Set("active", model.FilterMode == LibraryFilterMode.Hidden);
        this.FindControl<Button>("FavoriteFilterButton")!.Classes.Set("active", model.FilterMode == LibraryFilterMode.Favorite);
    }

    private void ApplyLabels()
    {
        playButton.Content = localization.Resolve(LOC.PlayGame);
        gridViewButton.Content = localization.Resolve(LOC.GridViewLabel);
        listViewButton.Content = localization.Resolve(LOC.ListViewLabel);
        this.FindControl<TextBox>("SearchBox")!.PlaceholderText = localization.Resolve(LOC.SearchLabel);
        this.FindControl<Button>("AllFilterButton")!.Content = localization.Resolve(LOC.QuickFilterAllGames);
        this.FindControl<Button>("InstalledFilterButton")!.Content = localization.Resolve(LOC.GameIsInstalledTitle);
        this.FindControl<Button>("UninstalledFilterButton")!.Content = localization.Resolve(LOC.GameIsUnInstalledTitle);
        this.FindControl<Button>("HiddenFilterButton")!.Content = localization.Resolve(LOC.GameHiddenTitle);
        this.FindControl<Button>("FavoriteFilterButton")!.Content = localization.Resolve(LOC.GameFavoriteTitle);
    }

    private string FilterLabel(LibraryFilterMode mode) => localization.Resolve(mode switch
    {
        LibraryFilterMode.All => LOC.QuickFilterAllGames,
        LibraryFilterMode.Installed => LOC.GameIsInstalledTitle,
        LibraryFilterMode.Uninstalled => LOC.GameIsUnInstalledTitle,
        LibraryFilterMode.Hidden => LOC.GameHiddenTitle,
        LibraryFilterMode.Favorite => LOC.GameFavoriteTitle,
        _ => LOC.QuickFilterAllGames,
    });

    private string SortLabel(SortOrder order) => localization.Resolve(order switch
    {
        SortOrder.Name => LOC.NameLabel,
        SortOrder.LastActivity => LOC.GameLastActivityTitle,
        SortOrder.RecentActivity => LOC.RecentActivityLabel,
        SortOrder.ReleaseDate => LOC.GameReleaseDateTitle,
        SortOrder.Playtime => LOC.StatsTotalPlayTime,
        SortOrder.UserScore => LOC.UserScore,
        _ => LOC.NameLabel,
    });

    private string GroupLabel(LibraryGroupBy groupBy) => groupBy switch
    {
        LibraryGroupBy.None => localization.Resolve(LOC.None),
        LibraryGroupBy.InstallationStatus => localization.Resolve(LOC.GameInstallationStatus),
        LibraryGroupBy.ReleaseYear => localization.Resolve(LOC.GameReleaseYearTitle),
        LibraryGroupBy.PlayTime => localization.Resolve(LOC.TimePlayed),
        LibraryGroupBy.UserScore => localization.Resolve(LOC.UserScore),
        LibraryGroupBy.Name => localization.Resolve(LOC.NameLabel),
        _ => localization.Resolve(LOC.None),
    };

    private LibraryArtworkLoader? GetOrCreateArtworkLoader()
    {
        if (disposed || model is null)
        {
            return null;
        }

        artworkLoader ??= artworkLoaderFactory(model.DatabasePath);
        return artworkLoader;
    }

    private void StartCardArtworkLoading()
    {
        var cards = activeCards.ToArray();
        var generation = ++artworkGeneration;
        artworkCts?.Cancel();
        artworkCts?.Dispose();
        artworkCts = null;

        if (cards.Length == 0 || model is null || disposed)
        {
            ArtworkTask = Task.CompletedTask;
            return;
        }

        artworkCts = CancellationTokenSource.CreateLinkedTokenSource(model.LifetimeToken);
        var task = LoadCardsArtworkAsync(cards, generation, artworkCts.Token);
        ArtworkTask = task;
        _ = ObserveUiTaskAsync(task, "artwork");
    }

    private async Task LoadCardsArtworkAsync(
        LibraryCardItem[] cards,
        int generation,
        CancellationToken cancellationToken)
    {
        var loader = GetOrCreateArtworkLoader();
        if (loader is null)
        {
            return;
        }

        foreach (var card in cards)
        {
            if (cancellationToken.IsCancellationRequested || disposed || generation != artworkGeneration)
            {
                return;
            }

            Bitmap? bitmap;
            try
            {
                bitmap = await loader.LoadBitmapAsync(card.Row, ArtworkSlot.Cover, CoverDecodeWidth, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (bitmap is null)
            {
                continue;
            }

            if (cancellationToken.IsCancellationRequested || disposed || generation != artworkGeneration)
            {
                bitmap.Dispose();
                return;
            }

            activeCardBitmaps.Add(bitmap);
            card.SetCoverBitmap(bitmap);
        }
    }

    private void StartHeroArtworkLoading(LibraryRow? selected)
    {
        var generation = ++heroGeneration;
        heroCts?.Cancel();
        heroCts?.Dispose();
        heroCts = null;
        DisposeHeroBitmap();

        if (selected is null || model is null || disposed)
        {
            HeroArtworkTask = Task.CompletedTask;
            return;
        }

        heroCts = CancellationTokenSource.CreateLinkedTokenSource(model.LifetimeToken);
        var task = LoadHeroArtworkAsync(selected, generation, heroCts.Token);
        HeroArtworkTask = task;
        _ = ObserveUiTaskAsync(task, "hero artwork");
    }

    private async Task LoadHeroArtworkAsync(
        LibraryRow selected,
        int generation,
        CancellationToken cancellationToken)
    {
        var loader = GetOrCreateArtworkLoader();
        if (loader is null)
        {
            return;
        }

        Bitmap? bitmap;
        try
        {
            bitmap = await loader.LoadBitmapAsync(selected, ArtworkSlot.Hero, HeroDecodeWidth, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (bitmap is null)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || disposed || generation != heroGeneration)
        {
            bitmap.Dispose();
            return;
        }

        DisposeHeroBitmap();
        activeHeroBitmap = bitmap;
        detailsHeroImage.Source = bitmap;
        detailsHeroImage.IsVisible = true;
        detailsInitials.IsVisible = false;
    }

    private void DisposeCardBitmaps()
    {
        artworkGeneration++;
        artworkCts?.Cancel();
        artworkCts?.Dispose();
        artworkCts = null;

        foreach (var card in activeCards)
        {
            card.SetCoverBitmap(null);
        }

        activeCards.Clear();
        foreach (var bitmap in activeCardBitmaps)
        {
            bitmap.Dispose();
        }

        activeCardBitmaps.Clear();
    }

    private void DisposeHeroBitmap()
    {
        detailsHeroImage.Source = null;
        detailsHeroImage.IsVisible = false;
        detailsInitials.IsVisible = true;
        activeHeroBitmap?.Dispose();
        activeHeroBitmap = null;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs eventArgs)
    {
        disposed = true;
        heroGeneration++;
        sessionManager.SessionUpdated -= OnSessionUpdated;
        CancelLocalOperations();
        heroCts?.Cancel();
        heroCts?.Dispose();
        heroCts = null;
        DisposeCardBitmaps();
        DisposeHeroBitmap();
        artworkLoader?.Dispose();
        artworkLoader = null;
        UnloadTask = ShutdownSessionsOnUnloadAsync();
        _ = ObserveUiTaskAsync(UnloadTask, "session unload");
    }

    private async Task ShutdownSessionsOnUnloadAsync()
    {
        if (ownsSessionManager)
        {
            await sessionManager.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await sessionManager.CancelAndWaitActiveSessionAsync().ConfigureAwait(false);
        }
    }

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        RefreshLibraryActions();
        if (rebuildingRows
            || disposed
            || model is not { } library
            || library.LifetimeToken.IsCancellationRequested
            || eventArgs.AddedItems.OfType<ListBoxItem>().FirstOrDefault()?.Tag is not LibraryRow row)
        {
            return;
        }

        var generation = ++selectionGeneration;
        selectionCts?.Cancel();
        selectionCts?.Dispose();
        selectionCts = CancellationTokenSource.CreateLinkedTokenSource(library.LifetimeToken);
        var task = ApplySelectionAsync(library, row, generation, selectionCts.Token);
        SelectionTask = task;
        await ObserveUiTaskAsync(task, "selection").ConfigureAwait(true);
    }

    private async Task ApplySelectionAsync(
        LibraryViewModel library,
        LibraryRow row,
        int generation,
        CancellationToken token)
    {
        try
        {
            await library.SelectAsync(row, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || library.LifetimeToken.IsCancellationRequested)
        {
            return;
        }
        catch (InvalidOperationException) when (disposed || library.LifetimeToken.IsCancellationRequested)
        {
            return;
        }

        if (disposed || generation != selectionGeneration || !ReferenceEquals(model, library) || token.IsCancellationRequested)
        {
            return;
        }

        Refresh();
    }

    private LibraryRow? SelectedPlayRow()
    {
        var selected = model?.Selected;
        var item = this.FindControl<ListBox>("GameList")!.SelectedItem as ListBoxItem;
        return selected is not null && selected.CanPlay && item?.Tag is LibraryRow row && row.Id == selected.Id
            ? selected : null;
    }

    private void RefreshLibraryActions()
    {
        var available = model is not null
            && !disposed
            && !model.LifetimeToken.IsCancellationRequested
            && !playPending
            && !syncPending
            && !fullscreenOpen
            && !sessionActive
            && !editorPending;
        this.FindControl<Button>("PreviousButton")!.IsEnabled = available && model!.CanGoPrevious;
        this.FindControl<Button>("NextButton")!.IsEnabled = available && model!.CanGoNext;
        playButton.IsEnabled = available && SelectedPlayRow() is not null;
        stopButton.IsVisible = sessionActive;
        stopButton.IsEnabled = sessionActive && !stopPending && !disposed;
        sessionBadge.IsVisible = sessionActive && model?.Selected?.Id == activeSessionGameId;
        configureLaunchButton.IsEnabled = available && model?.Selected is not null;
        addGameButton.IsEnabled = available;
        syncSteamButton.IsEnabled = available;
        syncHeroicButton.IsEnabled = available;
        emptySyncButton.IsEnabled = available;
        emptySyncHeroicButton.IsEnabled = available;
        fullscreenButton.IsEnabled = available && fullscreenButton.IsVisible;
    }

    private bool TryBeginSync(out LibraryViewModel library)
    {
        if (disposed || syncPending || playPending || editorPending || fullscreenOpen || sessionActive
            || model is not { } current || current.LifetimeToken.IsCancellationRequested)
        {
            library = null!;
            return false;
        }

        library = current;
        return true;
    }

    private async void OnSyncSteamClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (!TryBeginSync(out var library))
        {
            return;
        }

        var task = RunSyncAsync(library, "Syncing Steam library…", "Steam sync", steamSync.SyncAsync, LibrarySteamSyncRunner.FormatSummary);
        SyncTask = task;
        await ObserveUiTaskAsync(task, "steam sync").ConfigureAwait(true);
    }

    private async void OnSyncHeroicClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (!TryBeginSync(out var library))
        {
            return;
        }

        var task = RunSyncAsync(library, "Syncing Heroic libraries…", "Heroic sync", heroicSync.SyncAsync, HeroicLibrarySyncRunner.FormatSummary);
        SyncTask = task;
        await ObserveUiTaskAsync(task, "heroic sync").ConfigureAwait(true);
    }

    private async Task RunSyncAsync<TResult>(
        LibraryViewModel library,
        string pendingMessage,
        string operationLabel,
        Func<string, CancellationToken, Task<TResult>> syncFunc,
        Func<TResult, string> formatSummary)
    {
        var generation = ++syncGeneration;
        syncPending = true;
        RefreshLibraryActions();
        syncStatus.Text = pendingMessage;

        syncCts?.Cancel();
        syncCts?.Dispose();
        syncCts = CancellationTokenSource.CreateLinkedTokenSource(library.LifetimeToken);
        var token = syncCts.Token;

        try
        {
            var result = await syncFunc(library.DatabasePath, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (disposed || generation != syncGeneration || !ReferenceEquals(model, library) || library.LifetimeToken.IsCancellationRequested)
            {
                return;
            }

            await library.ReloadCurrentPageAsync(token).ConfigureAwait(true);
            if (disposed || generation != syncGeneration || !ReferenceEquals(model, library) || token.IsCancellationRequested)
            {
                return;
            }

            RebuildRows();
            Refresh();
            syncStatus.Text = formatSummary(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || library.LifetimeToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            if (disposed || generation != syncGeneration || !ReferenceEquals(model, library) || token.IsCancellationRequested || library.LifetimeToken.IsCancellationRequested)
            {
                return;
            }

            Console.Error.WriteLine($"{operationLabel} failed ({exception.GetType().Name}).");
            syncStatus.Text = $"{operationLabel} failed: {exception.Message}";
        }
        finally
        {
            if (generation == syncGeneration && ReferenceEquals(model, library))
            {
                syncPending = false;
                if (!disposed && !library.LifetimeToken.IsCancellationRequested)
                {
                    RefreshLibraryActions();
                }
            }
        }
    }

    private void OnClearFiltersClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (model is null)
        {
            return;
        }

        updatingQueryControls = true;
        try
        {
            searchBox.Text = string.Empty;
            groupCombo.SelectedIndex = 0;
            sourceCombo.SelectedIndex = 0;
        }
        finally
        {
            updatingQueryControls = false;
        }

        StartQuery(model.ResetFiltersAsync(), model.QueryToken);
    }

    private async void OnOpenFullscreen(object? sender, RoutedEventArgs eventArgs)
    {
        if (!fullscreenButton.IsEnabled || model is not { } library
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        fullscreenOpen = true;
        RefreshLibraryActions();
        try
        {
            var window = new FullscreenLibraryWindow(library.CreateIndependentView(), steamPlay, sessionManager, heroicPlay);
            await window.RunAsync(owner);
        }
        catch (Exception exception)
        {
            if (!disposed && ReferenceEquals(model, library) && !library.LifetimeToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Could not open the fullscreen library ({exception.GetType().Name}).");
                playStatus.Text = "Could not open the fullscreen library.";
            }
        }
        finally
        {
            fullscreenOpen = false;
            if (!disposed && ReferenceEquals(model, library) && !library.LifetimeToken.IsCancellationRequested)
            {
                RefreshLibraryActions();
                fullscreenButton.Focus();
            }
        }
    }

    private async void OnPlayClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (!playButton.IsEnabled || disposed || model is not { } library || SelectedPlayRow() is not { } game)
        {
            return;
        }

        if (game.HasFilePlayAction)
        {
            var sessionTask = RunTrackedSessionAsync(library, game);
            SessionTask = sessionTask;
            PlayTask = sessionTask;
            await ObserveUiTaskAsync(sessionTask, "game session").ConfigureAwait(true);
            return;
        }

        if (game.SteamPlayAppId is uint appId)
        {
            var task = RequestPlayAsync(library, appId, game.Name, library.LifetimeToken);
            PlayTask = task;
            await ObserveUiTaskAsync(task, "play").ConfigureAwait(true);
            return;
        }

        if (!string.IsNullOrEmpty(game.HeroicPlayUri))
        {
            var task = RequestHeroicPlayAsync(library, game.PluginId, game.GameId, game.Name, library.LifetimeToken);
            PlayTask = task;
            await ObserveUiTaskAsync(task, "heroic play").ConfigureAwait(true);
        }
    }

    private async Task RunTrackedSessionAsync(LibraryViewModel library, LibraryRow game)
    {
        var generation = Interlocked.Increment(ref sessionGeneration);
        sessionActive = true;
        sessionAcceptingUpdates = true;
        activeSessionGameId = game.Id;
        stopPending = false;
        sessionBadgeText.Text = "Starting";
        RefreshLibraryActions();
        playStatus.Text = $"Starting {game.Name}…";

        sessionCts?.Cancel();
        sessionCts?.Dispose();
        sessionCts = CancellationTokenSource.CreateLinkedTokenSource(library.LifetimeToken);
        var token = sessionCts.Token;

        try
        {
            var stored = await library.GetStoredGameAsync(game.Id, token).ConfigureAwait(true);
            if (disposed || generation != Volatile.Read(ref sessionGeneration) || !ReferenceEquals(model, library) || token.IsCancellationRequested)
            {
                return;
            }

            if (stored is null)
            {
                playStatus.Text = $"Game '{game.Name}' could not be loaded from the library.";
                return;
            }

            var plan = GameLaunchPlanner.TryBuildLaunchRequest(stored.Value);
            if (plan.Request is null)
            {
                playStatus.Text = plan.Error ?? $"'{game.Name}' could not be launched.";
                return;
            }

            var completed = await sessionManager.RunSessionAsync(
                library.DatabasePath,
                game.Id,
                game.Name,
                plan.Request,
                token).ConfigureAwait(true);

            sessionAcceptingUpdates = false;
            sessionActive = false;
            activeSessionGameId = null;
            stopPending = false;

            if (disposed || generation != Volatile.Read(ref sessionGeneration) || !ReferenceEquals(model, library) || library.LifetimeToken.IsCancellationRequested)
            {
                return;
            }

            await library.ReloadCurrentPageAsync(library.LifetimeToken).ConfigureAwait(true);
            await library.SelectByIdAsync(game.Id, library.LifetimeToken).ConfigureAwait(true);
            if (disposed || generation != Volatile.Read(ref sessionGeneration) || !ReferenceEquals(model, library))
            {
                return;
            }

            RebuildRows();
            Refresh();
            playStatus.Text = completed.StatusMessage;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || library.LifetimeToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            if (disposed || generation != Volatile.Read(ref sessionGeneration) || !ReferenceEquals(model, library))
            {
                return;
            }

            Console.Error.WriteLine($"Game session failed ({exception.GetType().Name}).");
            playStatus.Text = $"Could not launch {game.Name}: {exception.Message}";
        }
        finally
        {
            sessionAcceptingUpdates = false;
            if (generation == Volatile.Read(ref sessionGeneration))
            {
                sessionActive = false;
                activeSessionGameId = null;
                stopPending = false;
                if (!disposed && ReferenceEquals(model, library) && !library.LifetimeToken.IsCancellationRequested)
                {
                    RefreshLibraryActions();
                }
            }
        }
    }

    private void OnSessionUpdated(ActiveGameSession active)
    {
        var expectedGeneration = Volatile.Read(ref sessionGeneration);
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed
                || !sessionActive
                || !sessionAcceptingUpdates
                || expectedGeneration != Volatile.Read(ref sessionGeneration)
                || active.State is GameSessionState.Stopped or GameSessionState.Cancelled or GameSessionState.Failed)
            {
                return;
            }

            if (stopPending && active.State == GameSessionState.Running)
            {
                return;
            }

            sessionBadgeText.Text = active.State switch
            {
                GameSessionState.Running when active.IsDegraded => "Running (degraded)",
                GameSessionState.Running => "Running",
                GameSessionState.Stopping => "Stopping",
                _ => active.State.ToString(),
            };
            playStatus.Text = active.StatusMessage;
        });
    }

    private async void OnStopClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (!stopButton.IsEnabled || !sessionActive || stopPending || disposed)
        {
            return;
        }

        stopPending = true;
        RefreshLibraryActions();
        playStatus.Text = "Stopping active session…";
        var task = StopSessionWithFallbackAsync();
        StopTask = task;
        await ObserveUiTaskAsync(task, "stop session").ConfigureAwait(true);
    }

    private async Task StopSessionWithFallbackAsync()
    {
        try
        {
            var stopped = await sessionManager.StopActiveSessionAsync(CancellationToken.None).ConfigureAwait(true);
            if (!stopped && sessionCts is { IsCancellationRequested: false } cts)
            {
                await cts.CancelAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            if (!disposed)
            {
                Console.Error.WriteLine($"Stop session failed ({exception.GetType().Name}).");
                playStatus.Text = $"Could not stop session: {exception.Message}";
            }
        }
        finally
        {
            stopPending = false;
            if (!disposed)
            {
                RefreshLibraryActions();
            }
        }
    }

    private async void OnAddGameClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (!addGameButton.IsEnabled || disposed || editorPending || model is not { } library
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var task = OpenAddGameEditorAsync(library, owner);
        EditorTask = task;
        await ObserveUiTaskAsync(task, "add game").ConfigureAwait(true);
    }

    private async void OnTitlePreferencesClicked(object? sender, RoutedEventArgs args)
    {
        var task = OpenTitlePreferencesAsync(null);
        EditorTask = task;
        await ObserveUiTaskAsync(task, "title preferences").ConfigureAwait(true);
    }

    private async void OnGameTitleClicked(object? sender, RoutedEventArgs args)
    {
        if (model?.Selected is { } game)
        {
            var task = OpenTitlePreferencesAsync(game);
            EditorTask = task;
            await ObserveUiTaskAsync(task, "title preference").ConfigureAwait(true);
        }
    }

    private async Task OpenTitlePreferencesAsync(LibraryRow? game)
    {
        if (disposed || editorPending || model is not { } library || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        editorPending = true;
        try
        {
            var state = await library.ReadTitlePreferencesAsync(game?.Id).ConfigureAwait(true);
            if (disposed || !ReferenceEquals(model, library))
            {
                return;
            }

            var dialog = new TitlePreferenceWindow(state, library.FindLinkCandidatesAsync);
            var choice = await dialog.ShowDialog<TitlePreferenceSelection?>(owner).ConfigureAwait(true);
            if (choice is not null && !disposed && ReferenceEquals(model, library))
            {
                await library.SaveTitlePreferenceAsync(game?.Id, choice).ConfigureAwait(true);
                if (!disposed && ReferenceEquals(model, library))
                {
                    RebuildRows();
                    Refresh();
                }
            }
        }
        finally
        {
            editorPending = false;
        }
    }

    private async void OnConfigureLaunchClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (!configureLaunchButton.IsEnabled || disposed || editorPending || model is not { } library
            || library.Selected is not { } selectedRow
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var task = OpenConfigureLaunchEditorAsync(library, selectedRow, owner);
        EditorTask = task;
        await ObserveUiTaskAsync(task, "configure launch").ConfigureAwait(true);
    }

    internal Func<GameLaunchEditorWindow, Window, Task<GameLaunchEditorResult?>>? EditorDialogRunner { get; set; }

    private async Task OpenAddGameEditorAsync(LibraryViewModel library, Window owner)
    {
        var generation = ++editorGeneration;
        editorPending = true;
        RefreshLibraryActions();
        try
        {
            var editor = new GameLaunchEditorWindow(isNewGame: true, initialGame: null, toolResolver: toolResolver);
            var runner = EditorDialogRunner ?? ((dlg, parent) => dlg.ShowDialog<GameLaunchEditorResult?>(parent));
            var result = await runner(editor, owner).ConfigureAwait(true);
            if (result is null || disposed || generation != editorGeneration || !ReferenceEquals(model, library) || library.LifetimeToken.IsCancellationRequested)
            {
                return;
            }

            await SaveCustomGameAsync(result).ConfigureAwait(true);
        }
        finally
        {
            if (generation == editorGeneration)
            {
                editorPending = false;
                if (!disposed && ReferenceEquals(model, library) && !library.LifetimeToken.IsCancellationRequested)
                {
                    RefreshLibraryActions();
                }
            }
        }
    }

    private async Task OpenConfigureLaunchEditorAsync(LibraryViewModel library, LibraryRow selectedRow, Window owner)
    {
        var generation = ++editorGeneration;
        editorPending = true;
        RefreshLibraryActions();
        try
        {
            var stored = await library.GetStoredGameAsync(selectedRow.Id, library.LifetimeToken).ConfigureAwait(true);
            if (stored is null || disposed || generation != editorGeneration || !ReferenceEquals(model, library) || library.LifetimeToken.IsCancellationRequested)
            {
                return;
            }

            var editor = new GameLaunchEditorWindow(isNewGame: false, initialGame: stored.Value, toolResolver: toolResolver);
            var runner = EditorDialogRunner ?? ((dlg, parent) => dlg.ShowDialog<GameLaunchEditorResult?>(parent));
            var result = await runner(editor, owner).ConfigureAwait(true);
            if (result is null || disposed || generation != editorGeneration || !ReferenceEquals(model, library) || library.LifetimeToken.IsCancellationRequested)
            {
                return;
            }

            await SaveLaunchConfigurationAsync(selectedRow.Id, result).ConfigureAwait(true);
        }
        finally
        {
            if (generation == editorGeneration)
            {
                editorPending = false;
                if (!disposed && ReferenceEquals(model, library) && !library.LifetimeToken.IsCancellationRequested)
                {
                    RefreshLibraryActions();
                }
            }
        }
    }

    internal async Task<Guid> SaveCustomGameAsync(GameLaunchEditorResult input)
    {
        var library = model ?? throw new InvalidOperationException("No library model is attached.");
        var id = await library.AddCustomGameAsync(input, toolResolver, library.LifetimeToken).ConfigureAwait(true);
        if (!disposed && ReferenceEquals(model, library))
        {
            RebuildRows();
            Refresh();
            playStatus.Text = $"Added '{input.Name}'.";
        }

        return id;
    }

    internal async Task SaveLaunchConfigurationAsync(Guid gameId, GameLaunchEditorResult input)
    {
        var library = model ?? throw new InvalidOperationException("No library model is attached.");
        await library.UpdateLaunchActionAsync(gameId, input, toolResolver, library.LifetimeToken).ConfigureAwait(true);
        if (!disposed && ReferenceEquals(model, library))
        {
            RebuildRows();
            Refresh();
            playStatus.Text = $"Saved launch configuration for '{input.Name}'.";
        }
    }

    private Task RequestPlayAsync(LibraryViewModel library, uint appId, string name, CancellationToken cancellationToken) =>
        RequestUriPlayAsync(library, name, token => steamPlay.SendAsync(appId, name, token), cancellationToken);

    private Task RequestHeroicPlayAsync(
        LibraryViewModel library,
        Guid pluginId,
        string gameId,
        string name,
        CancellationToken cancellationToken) =>
        RequestUriPlayAsync(library, name, token => heroicPlay.SendAsync(pluginId, gameId, name, token), cancellationToken);

    private async Task RequestUriPlayAsync(
        LibraryViewModel library,
        string name,
        Func<CancellationToken, Task<string>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        var generation = ++playGeneration;
        playPending = true;
        RefreshLibraryActions();
        playStatus.Text = $"Requesting launch for {name}…";
        try
        {
            var result = await dispatchAsync(cancellationToken).ConfigureAwait(true);
            if (!disposed && generation == playGeneration && ReferenceEquals(model, library) && !cancellationToken.IsCancellationRequested)
            {
                playStatus.Text = result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Unloading the library cancels the wait, not the external store launcher process.
            return;
        }
        finally
        {
            if (generation == playGeneration && ReferenceEquals(model, library))
            {
                playPending = false;
                if (!disposed && !cancellationToken.IsCancellationRequested)
                {
                    RefreshLibraryActions();
                }
            }
        }
    }

    private async void OnPreviousClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (disposed || syncPending || fullscreenOpen || sessionActive || model is not { } library || library.LifetimeToken.IsCancellationRequested)
        {
            return;
        }

        var generation = ++navigationGeneration;
        navigationCts?.Cancel();
        navigationCts?.Dispose();
        navigationCts = CancellationTokenSource.CreateLinkedTokenSource(library.LifetimeToken);
        var token = navigationCts.Token;
        var task = ApplyNavigationAsync(library, library.PreviousPageAsync(token), generation, token);
        NavigationTask = task;
        await ObserveUiTaskAsync(task, "navigation").ConfigureAwait(true);
    }

    private async void OnNextClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (disposed || syncPending || fullscreenOpen || sessionActive || model is not { } library || library.LifetimeToken.IsCancellationRequested)
        {
            return;
        }

        var generation = ++navigationGeneration;
        navigationCts?.Cancel();
        navigationCts?.Dispose();
        navigationCts = CancellationTokenSource.CreateLinkedTokenSource(library.LifetimeToken);
        var token = navigationCts.Token;
        var task = ApplyNavigationAsync(library, library.NextPageAsync(token), generation, token);
        NavigationTask = task;
        await ObserveUiTaskAsync(task, "navigation").ConfigureAwait(true);
    }

    private async Task ApplyNavigationAsync(
        LibraryViewModel library,
        Task navigation,
        int generation,
        CancellationToken token)
    {
        try
        {
            await navigation.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || library.LifetimeToken.IsCancellationRequested)
        {
            return;
        }
        catch (InvalidOperationException) when (disposed || library.LifetimeToken.IsCancellationRequested)
        {
            return;
        }

        if (disposed || generation != navigationGeneration || !ReferenceEquals(model, library) || token.IsCancellationRequested)
        {
            return;
        }

        RebuildRows();
        Refresh();
    }

    private bool CanDispatchQuery() =>
        model is { } current
        && !disposed
        && !updatingQueryControls
        && !current.LifetimeToken.IsCancellationRequested;

    private void OnSearchChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        if (!CanDispatchQuery())
        {
            return;
        }

        StartQuery(model!.SetSearchTermAsync(searchBox.Text ?? string.Empty), model.QueryToken);
    }

    private void OnFilterClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (!CanDispatchQuery() || sender is not Button button)
        {
            return;
        }

        var mode = button.Name switch
        {
            "InstalledFilterButton" => LibraryFilterMode.Installed,
            "UninstalledFilterButton" => LibraryFilterMode.Uninstalled,
            "HiddenFilterButton" => LibraryFilterMode.Hidden,
            "FavoriteFilterButton" => LibraryFilterMode.Favorite,
            _ => LibraryFilterMode.All,
        };
        StartQuery(model!.SetFilterAsync(mode), model.QueryToken);
    }

    private void OnSortChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (!CanDispatchQuery() || sortCombo.SelectedIndex < 0 || sortCombo.SelectedIndex >= SortOrders.Length)
        {
            return;
        }

        StartQuery(model!.SetSortAsync(SortOrders[sortCombo.SelectedIndex], model.Descending), model.QueryToken);
    }

    private void OnSortDirectionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (!CanDispatchQuery())
        {
            return;
        }

        StartQuery(model!.SetDescendingAsync(!model.Descending), model.QueryToken);
    }

    private void OnGroupChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (!CanDispatchQuery() || groupCombo.SelectedIndex < 0 || groupCombo.SelectedIndex >= GroupOrders.Length)
        {
            return;
        }

        StartQuery(model!.SetGroupByAsync(GroupOrders[groupCombo.SelectedIndex]), model.QueryToken);
    }

    private void OnSourceChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (!CanDispatchQuery() || sourceCombo.SelectedIndex < 0 || sourceCombo.SelectedIndex >= SourceOrders.Length)
        {
            return;
        }

        StartQuery(model!.SetSourceFilterAsync(SourceOrders[sourceCombo.SelectedIndex]), model.QueryToken);
    }

    /// <summary>
    /// Supersession is a normal query outcome. The shared helper awaits the query, treats a
    /// cancellation of the active query as a no-op, and only refreshes the controls for a
    /// still-current result. No async-void query handler can leak an OperationCanceledException.
    /// </summary>
    private void StartQuery(Task query, CancellationToken token)
    {
        var uiTask = ApplyQueryAsync(query, token);
        QueryTask = uiTask;
        _ = ObserveQueryAsync(uiTask);
    }

    private async Task ApplyQueryAsync(Task query, CancellationToken token)
    {
        try
        {
            await query.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }

        RebuildRows();
        Refresh();
    }

    private static async Task ObserveQueryAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.WriteLine("query: failed=" + exception.Message);
        }
    }

    private static async Task ObserveUiTaskAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            Console.Error.WriteLine($"Library {operation} canceled unexpectedly ({exception.GetType().Name}).");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Library {operation} failed ({exception.GetType().Name}).");
        }
    }

    // Smoke-only drivers: they raise the same routed events a user raises, so removing any control
    // or event wiring makes the headless assertions fail instead of passing on a model-only path.
    internal void SelectRowForSmoke(int index)
    {
        var list = this.FindControl<ListBox>("GameList")!;
        if (index >= 0 && index < list.Items.Count)
        {
            list.SelectedIndex = index;
        }
    }

    internal void ClickPreviousForSmoke() =>
        this.FindControl<Button>("PreviousButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal void ClickNextForSmoke() =>
        this.FindControl<Button>("NextButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// Drives the real query controls and reads the applied controls before emitting each marker.
    /// Removing any of the search/filter/sort event wirings fails the smoke.
    /// </summary>
    private async Task EmitSearchSmokeAsync()
    {
        if (model!.Rows.Count == 0)
        {
            var emptyValid = EmitQuery("initial", string.Empty, "Page 1 / 1",
                "Search:  | Filter: All | Sort: Name | Direction: asc");
            if (emptyValid)
            {
                Console.WriteLine("search-filter-sort: controls-loaded");
            }

            return;
        }

        var allValid = EmitQuery("initial", "Alpha Game,Bravo Game,Charlie Game,Delta Game,Echo Hidden,Foxtrot Game",
            "Page 1 / 1", "Search:  | Filter: All | Sort: Name | Direction: asc");

        SetSearchForSmoke("Bravo");
        await QueryTask;
        allValid &= EmitQuery("search", "Bravo Game", "Page 1 / 1",
            "Search: Bravo | Filter: All | Sort: Name | Direction: asc");

        SetSearchForSmoke(string.Empty);
        await QueryTask;
        ClickFilterForSmoke("FavoriteFilterButton");
        await QueryTask;
        allValid &= EmitQuery("filter", "Bravo Game,Delta Game", "Page 1 / 1",
            "Search:  | Filter: Favorite | Sort: Name | Direction: asc");

        SetSortIndexForSmoke(5); // UserScore
        await QueryTask;
        allValid &= EmitQuery("sort", "Delta Game,Bravo Game", "Page 1 / 1",
            "Search:  | Filter: Favorite | Sort: UserScore | Direction: asc");

        this.FindControl<Button>("SortDirectionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await QueryTask;
        allValid &= EmitQuery("sort-desc", "Bravo Game,Delta Game", "Page 1 / 1",
            "Search:  | Filter: Favorite | Sort: UserScore | Direction: desc");

        ClickFilterForSmoke("AllFilterButton");
        await QueryTask;
        SetSortIndexForSmoke(0); // Name
        await QueryTask;
        this.FindControl<Button>("SortDirectionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await QueryTask;
        allValid &= EmitQuery("reset", "Alpha Game,Bravo Game,Charlie Game,Delta Game,Echo Hidden,Foxtrot Game",
            "Page 1 / 1", "Search:  | Filter: All | Sort: Name | Direction: asc");

        allValid &= await EmitSupersedeAsync();

        if (allValid)
        {
            Console.WriteLine("search-filter-sort: controls-loaded");
        }
    }

    /// <summary>
    /// Holds the first query in flight, completes a superseding query, then releases the first.
    /// A stale result or an unhandled cancellation fails the marker and the host assertions.
    /// </summary>
    private async Task<bool> EmitSupersedeAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        model!.QueryExecutor = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }

            return SmokePage(calls == 1 ? "Alpha Game" : "Charlie Game");
        };

        SetSearchForSmoke("Alpha");
        await entered.Task;
        var superseded = QueryTask;
        ClickFilterForSmoke("AllFilterButton");
        await QueryTask;
        release.TrySetResult();
        await superseded;
        model.QueryExecutor = null;
        return EmitQuery("supersede", "Charlie Game", "Page 1 / 1",
            "Search: Alpha | Filter: All | Sort: Name | Direction: asc");
    }

    private static LibraryPage SmokePage(string name) => new(
        [new StoredGame(new Game(Guid.NewGuid(), "smoke-" + name, name))], 1, []);

    private void SetSearchForSmoke(string text)
    {
        searchBox.Text = text;
        searchBox.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent, searchBox));
    }

    private void ClickFilterForSmoke(string name) =>
        this.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private void SetSortIndexForSmoke(int index) => sortCombo.SelectedIndex = index;

    private bool EmitQuery(string key, string expectedNames, string expectedPage, string expectedState)
    {
        var current = model!;
        var list = this.FindControl<ListBox>("GameList")!;
        var names = string.Join(",", list.Items.OfType<ListBoxItem>()
            .Select(item => (item.Content as TextBlock)?.Text ?? "<missing>"));
        var page = this.FindControl<TextBlock>("PageText")!.Text;
        var status = this.FindControl<TextBlock>("QueryStatus")!.Text;
        var filterButtons = new[]
        {
            ("AllFilterButton", "All games filter"),
            ("InstalledFilterButton", "Installed games filter"),
            ("UninstalledFilterButton", "Uninstalled games filter"),
            ("HiddenFilterButton", "Hidden games filter"),
            ("FavoriteFilterButton", "Favorite games filter"),
        };
        var semantic = filterButtons.All(pair =>
            string.Equals(AutomationProperties.GetName(this.FindControl<Button>(pair.Item1)!), pair.Item2, StringComparison.Ordinal))
            && string.Equals(AutomationProperties.GetName(searchBox), "Search games", StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(sortCombo), "Sort by", StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(this.FindControl<Button>("SortDirectionButton")!), "Sort direction", StringComparison.Ordinal);
        var valid =
            string.Equals(names, expectedNames, StringComparison.Ordinal)
            && string.Equals(page, expectedPage, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(status)
            && string.Equals(current.QueryStatus, expectedState, StringComparison.Ordinal)
            && list.SelectedIndex == (current.Rows.Count > 0 ? 0 : -1)
            && string.Equals(this.FindControl<TextBlock>("DetailsName")!.Text, current.Selected?.Name ?? "(none)", StringComparison.Ordinal)
            && string.Equals(this.FindControl<TextBlock>("DetailsId")!.Text, current.Selected?.GameId ?? "(none)", StringComparison.Ordinal)
            && semantic;

        Console.WriteLine(
            $"search-filter-sort: {key}=rows:{names};total:{current.TotalCount};page:{page};"
            + $"state:{current.QueryStatus};semantic:{semantic};valid:{valid.ToString().ToLowerInvariant()}");
        return valid;
    }
}
