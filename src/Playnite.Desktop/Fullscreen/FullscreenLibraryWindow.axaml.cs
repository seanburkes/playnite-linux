using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Shell;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;

namespace Playnite.Desktop.Fullscreen;

public sealed partial class FullscreenLibraryWindow : Window
{
    private readonly LibraryViewModel model;
    private readonly SteamPlayRequest steamPlay;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ListBox games;
    private bool busy;

    public FullscreenLibraryWindow() : this(
        new LibraryViewModel(Path.Combine(XdgDirectories.Resolve(new LinuxEnvironment()).Data, "library.sqlite")),
        new SteamPlayRequest(new SteamPlayRequest.DesktopOpener()))
    {
    }

    internal FullscreenLibraryWindow(LibraryViewModel model, SteamPlayRequest steamPlay)
    {
        this.model = model;
        this.steamPlay = steamPlay;
        AvaloniaXamlLoader.Load(this);
        games = this.FindControl<ListBox>("GameList")!;
        games.SelectionChanged += (_, _) => UpdateActions();
        games.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += OnOpened;
    }

    internal Task OperationTask { get; private set; } = Task.CompletedTask;

    internal async Task RunAsync(Window owner)
    {
        try
        {
            await ShowDialog(owner);
        }
        finally
        {
            await lifetime.CancelAsync();
            await OperationTask;
            model.Dispose();
            lifetime.Dispose();
        }
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        await LoadPageAsync(() => model.LoadAsync(lifetime.Token));
        if (!lifetime.IsCancellationRequested)
        {
            games.Focus();
        }
    }

    private void OnExit(object? sender, RoutedEventArgs args) => Close();

    private void OnWindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None)
        {
            args.Handled = true;
            Close();
        }
    }

    private async void OnGridKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.KeyModifiers != KeyModifiers.None || games.SelectedIndex < 0)
        {
            return;
        }

        if (args.Key == Key.Enter)
        {
            args.Handled = true;
            await PlayAsync();
            return;
        }

        var columns = (int)Resources["FullscreenColumns"]!;
        var offset = args.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -columns,
            Key.Down => columns,
            _ => 0,
        };
        if (offset == 0)
        {
            return;
        }

        args.Handled = true;
        var next = games.SelectedIndex + offset;
        if (next >= 0 && next < games.ItemCount)
        {
            games.SelectedIndex = next;
            games.ScrollIntoView(next);
        }
    }

    private async void OnPrevious(object? sender, RoutedEventArgs args) =>
        await LoadPageAsync(() => model.PreviousPageAsync(lifetime.Token));

    private async void OnNext(object? sender, RoutedEventArgs args) =>
        await LoadPageAsync(() => model.NextPageAsync(lifetime.Token));

    private async void OnPlay(object? sender, RoutedEventArgs args) => await PlayAsync();

    private Task LoadPageAsync(Func<Task> load) => StartOperation(async () =>
    {
        SetStatus("Loading library…");
        await load();
        if (lifetime.IsCancellationRequested)
        {
            return;
        }

        games.ItemsSource = model.Rows;
        games.SelectedIndex = model.Rows.Count == 0 ? -1 : 0;
        if (games.SelectedIndex >= 0)
        {
            games.ScrollIntoView(games.SelectedIndex);
        }
        this.FindControl<TextBlock>("PageText")!.Text = model.PageIndicator;
        SetStatus(model.Rows.Count == 0 ? "No games in the library." : "");
    });

    private Task PlayAsync()
    {
        if (games.SelectedItem is not LibraryRow { SteamPlayAppId: uint appId } game)
        {
            return Task.CompletedTask;
        }

        return StartOperation(async () =>
        {
            SetStatus($"Requesting launch for {game.Name}…");
            var result = await steamPlay.SendAsync(appId, game.Name, lifetime.Token);
            if (!lifetime.IsCancellationRequested)
            {
                SetStatus(result);
            }
        });
    }

    private Task StartOperation(Func<Task> operation)
    {
        if (busy || lifetime.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        return OperationTask = CompleteOperationAsync(operation);
    }

    private async Task CompleteOperationAsync(Func<Task> operation)
    {
        busy = true;
        UpdateActions();
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Fullscreen library operation failed ({exception.GetType().Name}).");
            SetStatus("The fullscreen library operation failed.");
        }
        finally
        {
            busy = false;
            if (!lifetime.IsCancellationRequested)
            {
                UpdateActions();
            }
        }
    }

    private void UpdateActions()
    {
        this.FindControl<Button>("PlayButton")!.IsEnabled =
            !busy && games.SelectedItem is LibraryRow { SteamPlayAppId: not null };
        this.FindControl<Button>("PreviousButton")!.IsEnabled = !busy && model.CanGoPrevious;
        this.FindControl<Button>("NextButton")!.IsEnabled = !busy && model.CanGoNext;
    }

    private void SetStatus(string status) => this.FindControl<TextBlock>("StatusText")!.Text = status;
}
