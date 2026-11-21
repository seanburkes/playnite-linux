using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;

namespace Playnite.Desktop.Shell;

/// <summary>
/// p7-003 content host, moved from p7-002 by a one-to-one Cross-Unit-Move with the CLR namespace
/// and class preserved, so ShellView's existing <c>using:Playnite.Desktop.Shell</c> reference stays
/// byte-for-byte valid. The grid, its view-model and the database destination are instance-owned
/// here; no static locator reaches p7-001's private ServiceRegistry.
///
/// p7-005 composes the game edit/metadata view beside the untouched p7-004 grid: it listens to the
/// existing GameList selection event, reads the selected ListBoxItem tag (a p7-004 LibraryRow),
/// loads the full StoredGame through its own second database wrapper, and after a save reloads the
/// current page and re-applies the grid. No p7-004 file is edited in place.
/// </summary>
public sealed partial class ContentHostView : UserControl
{
    private const string LibrarySmokeFlag = "--library-smoke";
    private const string GameEditSmokeFlag = "--game-edit-smoke";

    private readonly CancellationTokenSource lifetime = new();
    private readonly LibraryViewModel model;
    private readonly LibraryGridView grid;
    private readonly GameEditView editView;
    private readonly string databasePath;
    private readonly bool smoke;
    private readonly bool gameEditSmoke;

    private GameEditStore? editStore;
    private GameEditViewModel? editModel;

    public ContentHostView()
    {
        AvaloniaXamlLoader.Load(this);
        var args = Environment.GetCommandLineArgs();
        smoke = args.Contains(LibrarySmokeFlag, StringComparer.Ordinal);
        gameEditSmoke = args.Contains(GameEditSmokeFlag, StringComparer.Ordinal);
        var directories = XdgDirectories.Resolve(new LinuxEnvironment());
        databasePath = Path.Combine(directories.Data, "library.sqlite");
        model = new LibraryViewModel(databasePath);
        grid = this.FindControl<LibraryGridView>("LibraryGrid")!;
        editView = this.FindControl<GameEditView>("GameEdit")!;

        grid.FindControl<ListBox>("GameList")!.SelectionChanged += OnEditSelectionChanged;
        editView.SaveRequested += OnSaveRequested;
        editView.CancelRequested += OnCancelRequested;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Completes when the selection-driven edit load has been applied.</summary>
    internal Task EditLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the control-driven save, page reload and grid re-apply are applied.</summary>
    internal Task EditSaveTask { get; private set; } = Task.CompletedTask;

    private async void OnLoaded(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            // Load the library first: this creates the app-owned XDG directory that the edit
            // store's per-operation connections also need, preserving the p7-003 empty-library path.
            await model.LoadAsync(lifetime.Token);
            editStore = await GameEditStore.OpenAsync(databasePath, lifetime.Token);
            editModel = new GameEditViewModel(editStore);
            editView.Apply(editModel);
            grid.Apply(model);
            await EditLoadTask;
            if (smoke)
            {
                await EmitLibrarySmokeAsync();
            }

            if (gameEditSmoke)
            {
                await EmitGameEditSmokeAsync();
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine("library: load-failed=" + exception.Message);
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs eventArgs)
    {
        lifetime.Cancel();
        model.Dispose();
    }

    private async void OnEditSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (editModel is null
            || eventArgs.AddedItems.OfType<ListBoxItem>().FirstOrDefault()?.Tag is not LibraryRow row)
        {
            return;
        }

        EditLoadTask = LoadEditAsync(row.Id);
        await EditLoadTask;
    }

    private async Task LoadEditAsync(Guid id)
    {
        try
        {
            await editModel!.LoadAsync(id, lifetime.Token).ConfigureAwait(false);
            editView.Apply(editModel);
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancellation is a normal outcome: there is nothing left to apply.
            return;
        }
        catch (Exception exception)
        {
            Console.WriteLine("game-edit: load-failed=" + exception.Message);
        }
    }

    private async void OnSaveRequested(object? sender, EventArgs eventArgs)
    {
        EditSaveTask = SaveAndRefreshAsync();
        await EditSaveTask;
    }

    private async Task SaveAndRefreshAsync()
    {
        if (editModel is null)
        {
            return;
        }

        try
        {
            var saved = await editModel.SaveAsync(lifetime.Token).ConfigureAwait(false);
            if (!saved)
            {
                editView.Apply(editModel);
                return;
            }

            var offset = (model.CurrentPage - 1) * LibraryViewModel.PageSize;
            await model.LoadPageAsync(offset, lifetime.Token).ConfigureAwait(false);
            grid.Apply(model);
            await EditLoadTask.ConfigureAwait(false);
            editView.Apply(editModel);
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancellation is a normal outcome: the draft is left untouched in memory.
            return;
        }
        catch (Exception exception)
        {
            Console.WriteLine("game-edit: save-failed=" + exception.Message);
        }
    }

    private async void OnCancelRequested(object? sender, EventArgs eventArgs)
    {
        EditLoadTask = CancelEditAsync();
        await EditLoadTask;
    }

    private async Task CancelEditAsync()
    {
        if (editModel is null)
        {
            return;
        }

        var id = editModel.GameId;
        if (id == Guid.Empty)
        {
            editModel.Clear();
            editView.Apply(editModel);
            return;
        }

        await LoadEditAsync(id).ConfigureAwait(false);
    }

    /// <summary>
    /// Drives the real ListBox and pager buttons through their routed events, then reads the real
    /// applied controls before emitting each marker. Removing either event wiring fails the smoke.
    /// </summary>
    private async Task EmitLibrarySmokeAsync()
    {
        var allValid = EmitPage("page1");
        var list = grid.FindControl<ListBox>("GameList")!;
        if (list.Items.Count > 1)
        {
            var index = Math.Min(2, list.Items.Count - 1);
            grid.SelectRowForSmoke(index);
            await grid.SelectionTask;
            allValid &= EmitSelection(index, "selection");
        }

        grid.ClickNextForSmoke();
        await grid.NavigationTask;
        allValid &= EmitPage("next-result");

        // A second Next at the last page, and a Previous at the first page, must be no-ops.
        grid.ClickNextForSmoke();
        await grid.NavigationTask;
        allValid &= EmitPage("next-at-last");

        grid.ClickPreviousForSmoke();
        await grid.NavigationTask;
        allValid &= EmitPage("back");

        grid.ClickPreviousForSmoke();
        await grid.NavigationTask;
        allValid &= EmitPage("prev-at-first");

        if (allValid)
        {
            Console.WriteLine("library: grid-loaded");
        }
    }

    private bool EmitPage(string key)
    {
        var list = grid.FindControl<ListBox>("GameList")!;
        var firstItem = list.Items.OfType<ListBoxItem>().FirstOrDefault();
        var firstBlock = firstItem?.Content as TextBlock;
        var pageText = grid.FindControl<TextBlock>("PageText")!;
        var previous = grid.FindControl<Button>("PreviousButton")!;
        var next = grid.FindControl<Button>("NextButton")!;
        var detailsName = grid.FindControl<TextBlock>("DetailsName")!;
        var detailsId = grid.FindControl<TextBlock>("DetailsId")!;
        var expectedFirst = model.Rows.Count > 0 ? model.Rows[0] : null;
        var expectedSelected = model.Selected is null
            ? -1
            : Array.FindIndex(model.Rows.ToArray(), row => row.Id == model.Selected.Id);
        var valid =
            list.Items.Count == model.Rows.Count
            && string.Equals(pageText.Text, model.PageIndicator, StringComparison.Ordinal)
            && previous.IsEnabled == model.CanGoPrevious
            && next.IsEnabled == model.CanGoNext
            && list.SelectedIndex == expectedSelected
            && string.Equals(detailsName.Text, model.Selected?.Name ?? "(none)", StringComparison.Ordinal)
            && string.Equals(detailsId.Text, model.Selected?.GameId ?? "(none)", StringComparison.Ordinal)
            && (expectedFirst is null
                ? firstBlock is null
                : firstBlock is not null
                    && string.Equals(firstBlock.Text, expectedFirst.Name, StringComparison.Ordinal)
                    && string.Equals(AutomationProperties.GetName(firstItem!), expectedFirst.Name, StringComparison.Ordinal));

        Console.WriteLine(
            $"library: {key}=count:{list.Items.Count};total:{model.TotalCount};page:{pageText.Text};"
            + $"first:{firstBlock?.Text ?? "<missing>"};selected:{list.SelectedIndex};"
            + $"details:{detailsName.Text}|{detailsId.Text};"
            + $"prev:{previous.IsEnabled.ToString().ToLowerInvariant()};"
            + $"next:{next.IsEnabled.ToString().ToLowerInvariant()};"
            + $"semantic:{AutomationProperties.GetName(pageText)};"
            + $"valid:{valid.ToString().ToLowerInvariant()}");
        return valid;
    }

    private bool EmitSelection(int index, string key)
    {
        var list = grid.FindControl<ListBox>("GameList")!;
        var item = (ListBoxItem)list.Items[index]!;
        var block = item.Content as TextBlock;
        var expected = model.Rows[index];
        var detailsName = grid.FindControl<TextBlock>("DetailsName")!;
        var detailsId = grid.FindControl<TextBlock>("DetailsId")!;
        var valid =
            string.Equals(block?.Text, expected.Name, StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(item), expected.Name, StringComparison.Ordinal)
            && model.Selected?.Id == expected.Id
            && string.Equals(detailsName.Text, model.Selected?.Name, StringComparison.Ordinal)
            && string.Equals(detailsId.Text, model.Selected?.GameId, StringComparison.Ordinal);

        Console.WriteLine(
            $"library: {key}=index:{index};row:{block?.Text ?? "<missing>"};"
            + $"details:{detailsName.Text}|{detailsId.Text};"
            + $"semantic:{AutomationProperties.GetName(item)}|{AutomationProperties.GetName(detailsName)}|"
            + $"{AutomationProperties.GetName(detailsId)};valid:{valid.ToString().ToLowerInvariant()}");
        return valid;
    }

    /// <summary>
    /// Headless game-edit evidence. The marker values come from the applied controls and a fresh
    /// read through the second database wrapper, so removing the save wiring, a field collection
    /// or a semantic name fails the assertion instead of passing on a model-only path.
    /// </summary>
    private async Task EmitGameEditSmokeAsync()
    {
        if (editModel is null)
        {
            return;
        }

        var semantic =
            string.Equals(AutomationProperties.GetName(editView.FindControl<TextBox>("NameBox")!), "Game name", StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(editView.FindControl<TextBox>("DescriptionBox")!), "Game description", StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(editView.FindControl<TextBox>("UserScoreBox")!), "Game user score", StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(editView.FindControl<TextBlock>("MetadataText")!), "Read-only game metadata", StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(editView.FindControl<Button>("SaveButton")!), "Save game", StringComparison.Ordinal);

        if (!editModel.IsLoaded)
        {
            var emptyValid = model.Rows.Count == 0
                && string.Equals(editView.TextOf("StatusText"), "empty", StringComparison.Ordinal)
                && string.Equals(editView.TextOf("GameIdText"), "(none)", StringComparison.Ordinal)
                && string.Equals(editView.TextOf("MetadataText"), "(none)", StringComparison.Ordinal)
                && !editView.FindControl<Button>("SaveButton")!.IsEnabled
                && semantic;
            Console.WriteLine(
                $"game-edit: empty=rows:{model.Rows.Count};status:{editView.TextOf("StatusText")};"
                + $"semantic:{semantic.ToString().ToLowerInvariant()};valid:{emptyValid.ToString().ToLowerInvariant()}");
            if (emptyValid)
            {
                Console.WriteLine("game-edit: controls-loaded");
            }

            return;
        }

        var identity = editModel.GameIdentity;
        var loadedValid =
            string.Equals(editView.TextOf("GameIdText"), identity, StringComparison.Ordinal)
            && string.Equals(editView.TextOf("StatusText"), "loaded", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(editView.TextOf("MetadataText"))
            && semantic;
        Console.WriteLine(
            $"game-edit: loaded=id:{identity};status:{editView.TextOf("StatusText")};"
            + $"metadata:{editView.TextOf("MetadataText")};semantic:{semantic.ToString().ToLowerInvariant()};"
            + $"valid:{loadedValid.ToString().ToLowerInvariant()}");

        editView.SetTextForSmoke("NameBox", "Edited Smoke Game");
        editView.SetTextForSmoke("DescriptionBox", "edited description");
        editView.SetTextForSmoke("UserScoreBox", "77");
        editView.SetCheckForSmoke("FavoriteCheck", true);
        editView.ClickSaveForSmoke();
        await EditSaveTask;

        var stored = await editStore!.GetAsync(editModel.GameId, lifetime.Token);
        var savedValid =
            loadedValid
            && string.Equals(editView.InputTextOf("NameBox"), "Edited Smoke Game", StringComparison.Ordinal)
            && string.Equals(stored?.Value.Name, "Edited Smoke Game", StringComparison.Ordinal)
            && string.Equals(stored?.Value.Description, "edited description", StringComparison.Ordinal)
            && stored?.Value.UserScore == 77
            && stored?.Value.Favorite == true
            && string.Equals(model.Selected?.Name, "Edited Smoke Game", StringComparison.Ordinal)
            && !string.Equals(editView.TextOf("StatusText"), "save-failed", StringComparison.Ordinal);
        Console.WriteLine(
            $"game-edit: saved=id:{identity};name:{editView.InputTextOf("NameBox")};"
            + $"stored:{stored?.Value.Name ?? "<missing>"};score:{stored?.Value.UserScore?.ToString() ?? "<none>"};"
            + $"detail:{model.Selected?.Name ?? "(none)"};"
            + $"valid:{savedValid.ToString().ToLowerInvariant()}");

        if (savedValid)
        {
            Console.WriteLine("game-edit: controls-loaded");
        }
    }
}
