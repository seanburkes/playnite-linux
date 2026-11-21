using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Playnite.Desktop.Shell;

/// <summary>
/// The library grid/list/details pilot. Rows are real selectable ListBox items with accessible
/// names; selection and paging are routed through the view-model by control events, and the
/// headless host drives those same events for load-bearing evidence.
/// </summary>
public sealed partial class LibraryGridView : UserControl
{
    private LibraryViewModel? model;

    public LibraryGridView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ListBox>("GameList")!.SelectionChanged += OnSelectionChanged;
        this.FindControl<Button>("PreviousButton")!.Click += OnPreviousClick;
        this.FindControl<Button>("NextButton")!.Click += OnNextClick;
    }

    /// <summary>Completes when the most recent control-driven selection has been applied.</summary>
    internal Task SelectionTask { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the most recent control-driven page navigation has been applied.</summary>
    internal Task NavigationTask { get; private set; } = Task.CompletedTask;

    public void Apply(LibraryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        model = viewModel;
        RebuildRows();
        Refresh();
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
    }

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
}
