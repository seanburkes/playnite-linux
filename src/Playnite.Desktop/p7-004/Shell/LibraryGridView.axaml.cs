using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Core.Queries;
using Playnite.Desktop.Bootstrap;

namespace Playnite.Desktop.Shell;

/// <summary>
/// The library grid/list/details pilot with the p7-004 search/filter/sort query bar. Rows are real
/// selectable ListBox items with accessible names; selection, paging and query changes are routed
/// through the view-model by control events, and the headless host drives those same events for
/// load-bearing evidence. The p7-003 control names, hooks and behaviour are preserved.
/// </summary>
public sealed partial class LibraryGridView : UserControl
{
    private const string SearchSmokeFlag = "--search-filter-sort-smoke";

    private static readonly SortOrder[] SortOrders =
    [
        SortOrder.Name,
        SortOrder.LastActivity,
        SortOrder.RecentActivity,
        SortOrder.ReleaseDate,
        SortOrder.Playtime,
        SortOrder.UserScore,
    ];

    private readonly BootstrapLocalization localization = new();
    private readonly bool searchSmoke;

    private LibraryViewModel? model;

    public LibraryGridView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ListBox>("GameList")!.SelectionChanged += OnSelectionChanged;
        this.FindControl<Button>("PreviousButton")!.Click += OnPreviousClick;
        this.FindControl<Button>("NextButton")!.Click += OnNextClick;

        this.FindControl<TextBox>("SearchBox")!.TextChanged += OnSearchChanged;
        this.FindControl<Button>("AllFilterButton")!.Click += OnFilterClicked;
        this.FindControl<Button>("InstalledFilterButton")!.Click += OnFilterClicked;
        this.FindControl<Button>("UninstalledFilterButton")!.Click += OnFilterClicked;
        this.FindControl<Button>("HiddenFilterButton")!.Click += OnFilterClicked;
        this.FindControl<Button>("FavoriteFilterButton")!.Click += OnFilterClicked;

        var sortCombo = this.FindControl<ComboBox>("SortCombo")!;
        sortCombo.ItemsSource = SortOrders.Select(SortLabel).ToArray();
        sortCombo.SelectedIndex = 0;
        sortCombo.SelectionChanged += OnSortChanged;
        this.FindControl<Button>("SortDirectionButton")!.Click += OnSortDirectionClicked;

        ApplyLabels();
        searchSmoke = Environment.GetCommandLineArgs().Contains(SearchSmokeFlag, StringComparer.Ordinal);
    }

    /// <summary>Completes when the most recent control-driven selection has been applied.</summary>
    internal Task SelectionTask { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the most recent control-driven page navigation has been applied.</summary>
    internal Task NavigationTask { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the most recent control-driven query change has been applied.</summary>
    internal Task QueryTask { get; private set; } = Task.CompletedTask;

    public void Apply(LibraryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        model = viewModel;
        RebuildRows();
        Refresh();
        if (searchSmoke)
        {
            _ = EmitSearchSmokeAsync();
        }
    }

    private void RebuildRows()
    {
        if (model is null)
        {
            return;
        }

        var list = this.FindControl<ListBox>("GameList")!;
        list.Items.Clear();
        var selectedIndex = -1;
        for (var index = 0; index < model.Rows.Count; index++)
        {
            var row = model.Rows[index];
            var item = new ListBoxItem
            {
                Name = "Row" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Tag = row,
                Content = new TextBlock { Text = row.Name },
            };
            AutomationProperties.SetName(item, row.Name);
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

    private void Refresh()
    {
        if (model is null)
        {
            return;
        }

        this.FindControl<TextBlock>("DetailsName")!.Text = model.Selected?.Name ?? "(none)";
        this.FindControl<TextBlock>("DetailsId")!.Text = model.Selected?.GameId ?? "(none)";
        this.FindControl<TextBlock>("PageText")!.Text = model.PageIndicator;
        this.FindControl<Button>("PreviousButton")!.IsEnabled = model.CanGoPrevious;
        this.FindControl<Button>("NextButton")!.IsEnabled = model.CanGoNext;
        this.FindControl<TextBlock>("QueryStatus")!.Text =
            $"{localization.Resolve(LOC.SearchLabel)}: {model.SearchTerm} | " +
            $"{localization.Resolve(LOC.Filter)}: {FilterLabel(model.FilterMode)} | " +
            $"{localization.Resolve(LOC.MenuSortByTitle)}: {SortLabel(model.SortBy)} " +
            $"{localization.Resolve(model.Descending ? LOC.MenuSortDescending : LOC.MenuSortAscending)}";
        this.FindControl<Button>("SortDirectionButton")!.Content =
            localization.Resolve(model.Descending ? LOC.MenuSortDescending : LOC.MenuSortAscending);
    }

    private void ApplyLabels()
    {
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

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (model is null || eventArgs.AddedItems.OfType<ListBoxItem>().FirstOrDefault()?.Tag is not LibraryRow row)
        {
            return;
        }

        SelectionTask = ApplySelectionAsync(row);
        await SelectionTask;
    }

    private async Task ApplySelectionAsync(LibraryRow row)
    {
        await model!.SelectAsync(row);
        Refresh();
    }

    private async void OnPreviousClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (model is null)
        {
            return;
        }

        NavigationTask = ApplyNavigationAsync(model.PreviousPageAsync());
        await NavigationTask;
    }

    private async void OnNextClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (model is null)
        {
            return;
        }

        NavigationTask = ApplyNavigationAsync(model.NextPageAsync());
        await NavigationTask;
    }

    private async Task ApplyNavigationAsync(Task navigation)
    {
        await navigation;
        RebuildRows();
        Refresh();
    }

    private async void OnSearchChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        if (model is null)
        {
            return;
        }

        QueryTask = ApplyQueryAsync(model.SetSearchTermAsync(this.FindControl<TextBox>("SearchBox")!.Text ?? string.Empty));
        await QueryTask;
    }

    private async void OnFilterClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (model is null || sender is not Button button)
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
        QueryTask = ApplyQueryAsync(model.SetFilterAsync(mode));
        await QueryTask;
    }

    private async void OnSortChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        var combo = this.FindControl<ComboBox>("SortCombo")!;
        if (model is null || combo.SelectedIndex < 0 || combo.SelectedIndex >= SortOrders.Length)
        {
            return;
        }

        QueryTask = ApplyQueryAsync(model.SetSortAsync(SortOrders[combo.SelectedIndex], model.Descending));
        await QueryTask;
    }

    private async void OnSortDirectionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (model is null)
        {
            return;
        }

        QueryTask = ApplyQueryAsync(model.SetDescendingAsync(!model.Descending));
        await QueryTask;
    }

    private async Task ApplyQueryAsync(Task query)
    {
        await query;
        RebuildRows();
        Refresh();
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

        if (allValid)
        {
            Console.WriteLine("search-filter-sort: controls-loaded");
        }
    }

    private void SetSearchForSmoke(string text)
    {
        var box = this.FindControl<TextBox>("SearchBox")!;
        box.Text = text;
        box.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent, box));
    }

    private void ClickFilterForSmoke(string name) =>
        this.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private void SetSortIndexForSmoke(int index) => this.FindControl<ComboBox>("SortCombo")!.SelectedIndex = index;

    private bool EmitQuery(string key, string expectedNames, string expectedPage, string expectedState)
    {
        var current = model!;
        var list = this.FindControl<ListBox>("GameList")!;
        var names = string.Join(",", list.Items.OfType<ListBoxItem>()
            .Select(item => (item.Content as TextBlock)?.Text ?? "<missing>"));
        var page = this.FindControl<TextBlock>("PageText")!.Text;
        var status = this.FindControl<TextBlock>("QueryStatus")!.Text;
        var search = this.FindControl<TextBox>("SearchBox")!;
        var sort = this.FindControl<ComboBox>("SortCombo")!;
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
            && string.Equals(AutomationProperties.GetName(search), "Search games", StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(sort), "Sort by", StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(this.FindControl<Button>("SortDirectionButton")!), "Sort direction", StringComparison.Ordinal);
        var valid =
            string.Equals(names, expectedNames, StringComparison.Ordinal)
            && string.Equals(page, expectedPage, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(status)
            && string.Equals(current.QueryStatus, expectedState, StringComparison.Ordinal)
            && list.SelectedIndex == (current.Rows.Count > 0 ? 0 : -1)
            && semantic;

        Console.WriteLine(
            $"search-filter-sort: {key}=rows:{names};total:{current.TotalCount};page:{page};"
            + $"state:{current.QueryStatus};semantic:{semantic};valid:{valid.ToString().ToLowerInvariant()}");
        return valid;
    }
}
