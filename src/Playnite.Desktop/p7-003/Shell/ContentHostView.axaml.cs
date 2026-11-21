using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;

namespace Playnite.Desktop.Shell;

/// <summary>
/// p7-003 content host. Moved from p7-002 by a one-to-one Cross-Unit-Move with the CLR namespace
/// and class preserved, so ShellView's existing <c>using:Playnite.Desktop.Shell</c> reference stays
/// byte-for-byte valid. The grid, its view-model and the database destination are instance-owned
/// here; no static locator reaches p7-001's private ServiceRegistry.
/// </summary>
public sealed partial class ContentHostView : UserControl
{
    private const string LibrarySmokeFlag = "--library-smoke";

    private readonly CancellationTokenSource lifetime = new();
    private readonly LibraryViewModel model;
    private readonly LibraryGridView grid;
    private readonly bool smoke;

    public ContentHostView()
    {
        AvaloniaXamlLoader.Load(this);
        smoke = Environment.GetCommandLineArgs().Contains(LibrarySmokeFlag, StringComparer.Ordinal);
        var directories = XdgDirectories.Resolve(new LinuxEnvironment());
        model = new LibraryViewModel(Path.Combine(directories.Data, "library.sqlite"));
        grid = this.FindControl<LibraryGridView>("LibraryGrid")!;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await model.LoadAsync(lifetime.Token);
            grid.Apply(model);
            if (smoke)
            {
                await EmitLibrarySmokeAsync();
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
}
