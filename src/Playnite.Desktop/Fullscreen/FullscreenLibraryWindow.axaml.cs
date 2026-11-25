using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
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
    private readonly DesktopGameSessionManager? sessions;
    private readonly HeroicPlayRequest heroicPlay;
    private SdlGamepad? controller;
    private DispatcherTimer? controllerTimer;
    private LibraryFilterMode filter;
    private Guid? returnGame;
    private bool trackedLaunch;
    private bool launchPending;
    private bool handedOffFocus;
    private bool waitForFocusReturn;
    private bool busy;

    public FullscreenLibraryWindow() : this(
        new LibraryViewModel(Path.Combine(XdgDirectories.Resolve(new LinuxEnvironment()).Data, "library.sqlite")),
        new SteamPlayRequest(new SteamPlayRequest.DesktopOpener()))
    {
    }

    internal FullscreenLibraryWindow(LibraryViewModel model, SteamPlayRequest steamPlay,
        DesktopGameSessionManager? sessions = null, HeroicPlayRequest? heroicPlay = null)
    {
        this.model = model;
        this.steamPlay = steamPlay;
        this.sessions = sessions;
        this.heroicPlay = heroicPlay ?? new HeroicPlayRequest(new SteamPlayRequest.DesktopOpener());
        AvaloniaXamlLoader.Load(this);
        games = this.FindControl<ListBox>("GameList")!;
        games.SelectionChanged += (_, _) =>
        {
            UpdateActions();
            Control<ScrollViewer>("DetailsPanel").IsVisible = false;
        };
        foreach (var key in "abcdefghijklmnopqrstuvwxyz0123456789")
        {
            var button = new Button { Content = key.ToString() };
            button.Click += (_, _) => SearchEditor.Text += key;
            Control<UniformGrid>("SearchKeys").Children.Add(button);
        }
        Closed += (_, _) => StopController();
        Activated += (_, _) => RestoreGameFocus();
        Deactivated += (_, _) => handedOffFocus |= returnGame.HasValue;
        games.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += OnOpened;
    }

    internal Task OperationTask { get; private set; } = Task.CompletedTask;

    internal async Task RunAsync(Window owner)
    {
        if (sessions is not null)
        {
            sessions.SessionUpdated += OnSessionUpdated;
        }

        try
        {
            await ShowDialog(owner);
        }
        finally
        {
            if (sessions is not null)
            {
                sessions.SessionUpdated -= OnSessionUpdated;
            }

            StopController();
            await lifetime.CancelAsync();
            await OperationTask;
            model.Dispose();
            lifetime.Dispose();
        }
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        StartController();
        await LoadPageAsync(() => model.LoadAsync(lifetime.Token));
        if (!lifetime.IsCancellationRequested)
        {
            games.Focus();
        }
    }

    private void OnExit(object? sender, RoutedEventArgs args) => Back();

    private void OnWindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None)
        {
            args.Handled = true;
            Back();
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

        var direction = args.Key switch
        {
            Key.Left => GamepadButton.Left,
            Key.Right => GamepadButton.Right,
            Key.Up => GamepadButton.Up,
            Key.Down => GamepadButton.Down,
            _ => (GamepadButton?)null,
        };
        if (direction is { } button)
        {
            args.Handled = true;
            MoveInGrid(button);
        }
    }

    private bool MoveInGrid(GamepadButton button)
    {
        var columns = (int)Resources["FullscreenColumns"]!;
        var index = games.SelectedIndex;
        var next = button switch
        {
            GamepadButton.Left when index % columns > 0 => index - 1,
            GamepadButton.Right when index % columns < columns - 1 => index + 1,
            GamepadButton.Up => index - columns,
            GamepadButton.Down => index + columns,
            _ => -1,
        };
        if (index < 0 || next < 0 || next >= games.ItemCount)
        {
            return false;
        }

        games.SelectedIndex = next;
        games.ScrollIntoView(next);
        return true;
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
        if (games.SelectedItem is not LibraryRow { CanPlay: true } game)
        {
            return Task.CompletedTask;
        }

        return StartOperation(async () =>
        {
            returnGame = game.Id;
            launchPending = true;
            handedOffFocus = false;
            waitForFocusReturn = !game.HasFilePlayAction || sessions is null;
            SetStatus($"Starting {game.Name}…");
            try
            {
                var result = game switch
                {
                    { HasFilePlayAction: true } when sessions is not null => await TrackAsync(game),
                    { SteamPlayAppId: uint appId } => await steamPlay.SendAsync(appId, game.Name, lifetime.Token),
                    { HeroicPlayUri: not null } => await heroicPlay.SendAsync(game.PluginId, game.GameId, game.Name, lifetime.Token),
                    _ => $"No launch handler is available for {game.Name}.",
                };
                if (!lifetime.IsCancellationRequested)
                {
                    SetStatus(result);
                }
            }
            finally
            {
                launchPending = false;
                RestoreGameFocus();
            }
        });
    }

    private async Task<string> TrackAsync(LibraryRow game)
    {
        var stored = await model.GetStoredGameAsync(game.Id, lifetime.Token);
        if (stored is null)
        {
            return $"Could not load {game.Name}.";
        }

        var plan = GameLaunchPlanner.TryBuildLaunchRequest(stored.Value);
        if (plan.Request is null)
        {
            return plan.Error ?? $"Could not launch {game.Name}.";
        }

        trackedLaunch = true;
        try
        {
            var completed = await sessions!.RunSessionAsync(model.DatabasePath, game.Id, game.Name, plan.Request, lifetime.Token);
            await model.ReloadCurrentPageAsync(lifetime.Token);
            games.ItemsSource = model.Rows;
            return completed.StatusMessage;
        }
        finally
        {
            trackedLaunch = false;
        }
    }

    private void OnSessionUpdated(ActiveGameSession session) => Dispatcher.UIThread.Post(() =>
    {
        if (trackedLaunch && IsVisible && returnGame == session.GameId)
        {
            SetStatus(session.StatusMessage);
        }
    });

    private void RestoreGameFocus()
    {
        if (!IsActive || launchPending || returnGame is not Guid id)
        {
            return;
        }

        if (!waitForFocusReturn || handedOffFocus)
        {
            returnGame = null;
        }

        var index = Array.FindIndex(model.Rows.ToArray(), row => row.Id == id);
        if (index >= 0)
        {
            games.SelectedIndex = index;
            games.ScrollIntoView(index);
            games.Focus();
        }
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
            !busy && games.SelectedItem is LibraryRow { CanPlay: true } row
            && (!row.HasFilePlayAction || sessions is not null || row.SteamPlayAppId is not null || row.HeroicPlayUri is not null);
        Control<Button>("SearchButton").IsEnabled = !busy;
        Control<Button>("FilterButton").IsEnabled = !busy;
        Control<Button>("DetailsButton").IsEnabled = !busy && games.SelectedIndex >= 0;
        this.FindControl<Button>("PreviousButton")!.IsEnabled = !busy && model.CanGoPrevious;
        this.FindControl<Button>("NextButton")!.IsEnabled = !busy && model.CanGoNext;
    }

    private T Control<T>(string name) where T : Control => this.FindControl<T>(name)!;


    private TextBox SearchEditor => Control<TextBox>("SearchInput");
    private StackPanel SearchControls => Control<StackPanel>("SearchPanel");

    private void OnSearch(object? sender, RoutedEventArgs args)
    {
        SearchEditor.Text = model.SearchTerm;
        SearchControls.IsVisible = true;
        Control<UniformGrid>("SearchKeys").Children[0].Focus(NavigationMethod.Directional);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            args.Handled = true;
            Control<Button>("ApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private void OnSpace(object? sender, RoutedEventArgs args) => SearchEditor.Text += " ";

    private void OnErase(object? sender, RoutedEventArgs args)
    {
        var value = SearchEditor.Text ?? "";
        var elements = StringInfo.ParseCombiningCharacters(value);
        if (elements.Length > 0)
        {
            SearchEditor.Text = value[..elements[^1]];
        }
    }

    private void OnClear(object? sender, RoutedEventArgs args) => SearchEditor.Text = "";

    private async void OnApplySearch(object? sender, RoutedEventArgs args)
    {
        SearchControls.IsVisible = false;
        await LoadPageAsync(() => model.SetSearchTermAsync(SearchEditor.Text ?? "", lifetime.Token));
        games.Focus();
    }

    private void OnCancelSearch(object? sender, RoutedEventArgs args)
    {
        SearchControls.IsVisible = false;
        Control<Button>("SearchButton").Focus();
    }

    private async void OnFilter(object? sender, RoutedEventArgs args)
    {
        var filters = Enum.GetValues<LibraryFilterMode>();
        var next = filters[(Array.IndexOf(filters, filter) + 1) % filters.Length];
        await LoadPageAsync(async () =>
        {
            await model.SetFilterAsync(next, lifetime.Token);
            if (!lifetime.IsCancellationRequested)
            {
                filter = next;
                Control<Button>("FilterButton").Content = $"Filter: {filter}";
            }
        });
        games.Focus();
    }

    private async void OnDetails(object? sender, RoutedEventArgs args)
    {
        var panel = Control<ScrollViewer>("DetailsPanel");
        if (panel.IsVisible)
        {
            panel.IsVisible = false;
            return;
        }

        if (games.SelectedItem is not LibraryRow row)
        {
            return;
        }

        await StartOperation(async () =>
        {
            await model.SelectAsync(row, lifetime.Token);
            if (!lifetime.IsCancellationRequested && games.SelectedItem is LibraryRow selected && selected.Id == row.Id && model.Selected is { } detail)
            {
                Control<TextBlock>("DetailsText").Text =
                    $"{detail.Name} · {(detail.IsInstalled ? "Installed" : "Not installed")} · {detail.PlayCountDisplay}\n{detail.Description}";
                panel.Offset = default;
                panel.IsVisible = true;
            }
        });
        if (IsVisible)
        {
            Control<Button>("DetailsButton").Focus();
        }
    }

    private void Back()
    {
        if (SearchControls.IsVisible)
        {
            OnCancelSearch(null, new RoutedEventArgs());
        }
        else if (Control<ScrollViewer>("DetailsPanel").IsVisible)
        {
            Control<ScrollViewer>("DetailsPanel").IsVisible = false;
            games.Focus();
        }
        else if (!trackedLaunch)
        {
            Close();
        }
    }

    private void StartController()
    {
        controller = SdlGamepad.TryOpen();
        var status = Control<TextBlock>("ControllerText");
        status.Text = controller is null ? "SDL3 unavailable. Use keyboard and mouse." : "Connect a controller.";
        if (controller is null)
        {
            return;
        }

        controllerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        controllerTimer.Tick += (_, _) =>
        {
            if (controller is not { } input)
            {
                return;
            }

            input.Poll(button =>
            {
                if (IsActive)
                {
                    HandleController(button);
                }
            });
            if (controller is null)
            {
                return;
            }

            status.Text = input.ConnectedCount > 0
                ? "D-pad Navigate · A Select · B Back · LB/RB Page"
                : "Connect a controller.";
        };
        controllerTimer.Start();
    }

    private void StopController()
    {
        controllerTimer?.Stop();
        controller?.Dispose();
        controller = null;
    }

    internal void HandleController(GamepadButton button)
    {
        if (button == GamepadButton.Back)
        {
            Back();
            return;
        }

        if (busy)
        {
            return;
        }

        var focused = FocusManager?.GetFocusedElement();
        if (SearchControls.IsVisible)
        {
            NavigateSearch(button, focused);
            return;
        }

        var details = Control<ScrollViewer>("DetailsPanel");
        if (details.IsVisible && button is GamepadButton.Up or GamepadButton.Down)
        {
            var offset = button == GamepadButton.Up ? -48 : 48;
            details.Offset = details.Offset.WithY(Math.Clamp(details.Offset.Y + offset, 0, Math.Max(0, details.Extent.Height - details.Viewport.Height)));
            return;
        }

        var inGrid = focused is ListBox or ListBoxItem;
        if (button == GamepadButton.Accept)
        {
            ClickControllerButton(inGrid ? Control<Button>("PlayButton") : focused as Button);
        }
        else if (button is GamepadButton.LeftShoulder or GamepadButton.RightShoulder)
        {
            ClickControllerButton(Control<Button>(button == GamepadButton.LeftShoulder ? "PreviousButton" : "NextButton"));
            games.Focus();
        }
        else if (!inGrid || !MoveInGrid(button))
        {
            Control[] order = [Control<Button>("SearchButton"), Control<Button>("FilterButton"),
                Control<Button>("DetailsButton"), Control<Button>("ExitButton"), games,
                Control<Button>("PreviousButton"), Control<Button>("NextButton"), Control<Button>("PlayButton")];
            if (!inGrid || button is GamepadButton.Up or GamepadButton.Down)
            {
                MoveFocus(order, inGrid ? games : focused, button);
            }
        }
    }

    private void NavigateSearch(GamepadButton button, IInputElement? focused)
    {
        if (button == GamepadButton.Accept)
        {
            ClickControllerButton(focused as Button);
            return;
        }

        var keys = Control<UniformGrid>("SearchKeys").Children.ToArray();
        Control[] order = [.. keys, Control<Button>("SpaceButton"), Control<Button>("EraseButton"),
            Control<Button>("ClearButton"), Control<Button>("ApplyButton"), Control<Button>("CancelSearchButton")];
        MoveFocus(order, focused, button, 10);
    }

    private static void ClickControllerButton(Button? button)
    {
        if (button is { IsEnabled: true })
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private static void MoveFocus(Control[] order, IInputElement? focused, GamepadButton button, int columns = 1)
    {
        var offset = button switch
        {
            GamepadButton.Left => -1,
            GamepadButton.Right => 1,
            GamepadButton.Up => -columns,
            GamepadButton.Down => columns,
            _ => 0,
        };
        if (offset == 0)
        {
            return;
        }

        var index = Array.IndexOf(order, focused as Control);
        var next = index < 0 ? 0 : Math.Clamp(index + offset, 0, order.Length - 1);
        while (next >= 0 && next < order.Length && !order[next].IsEnabled)
        {
            next += Math.Sign(offset);
        }
        if (next >= 0 && next < order.Length)
        {
            order[next].Focus(NavigationMethod.Directional);
        }
    }

    private void SetStatus(string status) => this.FindControl<TextBlock>("StatusText")!.Text = status;
}
