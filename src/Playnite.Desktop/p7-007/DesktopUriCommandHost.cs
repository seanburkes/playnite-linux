using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Playnite.Desktop.Shell;
using Playnite.SDK;

namespace Playnite.Desktop.Integration;

/// <summary>
/// Implements <see cref="IUriCommandHost"/> for the Avalonia desktop shell, routing validated
/// <c>playnite://</c> commands to the live <see cref="MainWindow"/> and its child views.
/// </summary>
public sealed class DesktopUriCommandHost : IUriCommandHost
{
    private readonly Func<MainWindow?> windowProvider;
    private readonly Action? restoreWindow;

    public DesktopUriCommandHost(Func<MainWindow?> windowProvider, Action? restoreWindow = null)
    {
        this.windowProvider = windowProvider ?? throw new ArgumentNullException(nameof(windowProvider));
        this.restoreWindow = restoreWindow;
    }

    public ValueTask<UriCommandResult> StartGameAsync(
        Guid gameId,
        int? actionIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            UriCommandResult.Unavailable("Launching games from a playnite:// URI is not wired in this build."));
    }

    public ValueTask<UriCommandResult> ShowGameAsync(Guid gameId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = RestoreAndGetWindow();
        if (window is null)
        {
            return ValueTask.FromResult(UriCommandResult.Unavailable("The desktop shell window is not open."));
        }

        var grid = FindDescendant<LibraryGridView>(window);
        var list = grid?.FindControl<ListBox>("GameList");
        if (list is null)
        {
            return ValueTask.FromResult(UriCommandResult.Unavailable("The library view is not available."));
        }

        foreach (var item in list.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is LibraryRow row && row.Id == gameId)
            {
                list.SelectedItem = item;
                return ValueTask.FromResult(UriCommandResult.Completed());
            }
        }

        return ValueTask.FromResult(
            UriCommandResult.Failed($"Game '{gameId}' was not found in the current library view."));
    }

    public ValueTask<UriCommandResult> SearchAsync(string? term, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = RestoreAndGetWindow();
        if (window is null)
        {
            return ValueTask.FromResult(UriCommandResult.Unavailable("The desktop shell window is not open."));
        }

        var searchBox = FindDescendant<LibraryGridView>(window)?.FindControl<TextBox>("SearchBox");
        if (searchBox is null)
        {
            return ValueTask.FromResult(UriCommandResult.Unavailable("The library search box is not available."));
        }

        searchBox.Text = term ?? string.Empty;
        searchBox.Focus();
        return ValueTask.FromResult(UriCommandResult.Completed());
    }

    public ValueTask<UriCommandResult> ShowViewAsync(PlayniteViewTarget view, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = RestoreAndGetWindow();
        if (window is null)
        {
            return ValueTask.FromResult(UriCommandResult.Unavailable("The desktop shell window is not open."));
        }

        switch (view)
        {
            case PlayniteViewTarget.Desktop:
                return ValueTask.FromResult(UriCommandResult.Completed());

            default:
                return ValueTask.FromResult(
                    UriCommandResult.Unavailable($"View '{view}' is not available in this build."));
        }
    }

    public ValueTask<UriCommandResult> ApplyFilterAsync(
        PlayniteFilterTarget filter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(filter);
        var window = RestoreAndGetWindow();
        if (window is null)
        {
            return ValueTask.FromResult(UriCommandResult.Unavailable("The desktop shell window is not open."));
        }

        var grid = FindDescendant<LibraryGridView>(window);
        if (grid is null)
        {
            return ValueTask.FromResult(UriCommandResult.Unavailable("The library view is not available."));
        }

        if (filter.Field == PlayniteFilterField.Name)
        {
            var searchBox = grid.FindControl<TextBox>("SearchBox");
            if (searchBox is null)
            {
                return ValueTask.FromResult(UriCommandResult.Unavailable("The library search box is not available."));
            }

            searchBox.Text = filter.Value;
            return ValueTask.FromResult(UriCommandResult.Completed());
        }

        var isFalse = string.Equals(filter.Value, "false", StringComparison.OrdinalIgnoreCase);
        var buttonName = filter.Field switch
        {
            PlayniteFilterField.Installed => isFalse ? "AllFilterButton" : "InstalledFilterButton",
            PlayniteFilterField.Uninstalled => isFalse ? "AllFilterButton" : "UninstalledFilterButton",
            PlayniteFilterField.Hidden => isFalse ? "AllFilterButton" : "HiddenFilterButton",
            PlayniteFilterField.Favorite => isFalse ? "AllFilterButton" : "FavoriteFilterButton",
            _ => null,
        };

        if (buttonName is null)
        {
            return ValueTask.FromResult(
                UriCommandResult.Unavailable($"Filter '{filter.Field}' is not supported by the desktop grid."));
        }

        var button = grid.FindControl<Button>(buttonName);
        if (button is null)
        {
            return ValueTask.FromResult(UriCommandResult.Unavailable($"Filter button '{buttonName}' was not found."));
        }

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return ValueTask.FromResult(UriCommandResult.Completed());
    }

    private MainWindow? RestoreAndGetWindow()
    {
        var window = windowProvider();
        if (window is null)
        {
            return null;
        }

        if (restoreWindow is not null)
        {
            restoreWindow();
        }
        else
        {
            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        }

        return window;
    }

    private static T? FindDescendant<T>(Visual root) where T : Visual
    {
        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is T match)
            {
                return match;
            }
        }

        return null;
    }
}
