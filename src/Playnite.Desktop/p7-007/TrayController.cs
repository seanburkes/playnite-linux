using Avalonia;
using Avalonia.Controls;
using Playnite.Desktop.Bootstrap;
using Playnite.Platform.DesktopIntegration;
using Playnite.Settings;

namespace Playnite.Desktop.Integration;

/// <summary>Tray preferences read from the native settings store.</summary>
public sealed record TrayPreferences(
    bool EnableTray,
    bool MinimizeToTray,
    bool CloseToTray,
    bool StartMinimized)
{
    public static TrayPreferences Default { get; } = new(
        EnableTray: true,
        MinimizeToTray: false,
        CloseToTray: true,
        StartMinimized: false);

    public static async Task<TrayPreferences> LoadAsync(
        string settingsPath,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        try
        {
            using var settings = new PlayniteSettingsService(settingsPath);
            var snapshot = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            return new TrayPreferences(
                EnableTray: snapshot.Get(PlayniteSettingsKeys.EnableTray),
                MinimizeToTray: snapshot.Get(PlayniteSettingsKeys.MinimizeToTray),
                CloseToTray: snapshot.Get(PlayniteSettingsKeys.CloseToTray),
                StartMinimized: snapshot.Get(PlayniteSettingsKeys.StartMinimized));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SettingsException or IOException or UnauthorizedAccessException)
        {
            logger?.Info($"tray: could not read settings ({exception.Message}); using defaults.");
            return Default;
        }
    }
}

/// <summary>
/// Coordinates the p5-007a <see cref="ITrayService"/> with the desktop shell window lifecycle
/// (StartMinimized, MinimizeToTray, CloseToTray, and tray menu restore/exit).
/// </summary>
public sealed class TrayController : IAsyncDisposable
{
    public const string OpenMenuItemId = "open";
    public const string ExitMenuItemId = "exit";

    private readonly ITrayService trayService;
    private readonly TrayPreferences preferences;
    private readonly IAppLogger? logger;
    private readonly Func<Window?>? windowProvider;
    private readonly Action? shutdownAction;

    private TrayInstallation? installation;
    private Window? attachedWindow;
    private bool exitRequested;
    private bool startMinimizedApplied;

    public TrayController(
        ITrayService trayService,
        TrayPreferences preferences,
        IAppLogger? logger = null,
        Func<Window?>? windowProvider = null,
        Action? shutdownAction = null)
    {
        this.trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        this.logger = logger;
        this.windowProvider = windowProvider;
        this.shutdownAction = shutdownAction;
    }

    public bool IsHosted => installation?.IsHosted == true;

    public TrayPreferences Preferences => preferences;

    public async Task InitializeAsync(
        TrayModel? model = null,
        BootstrapLocalization? localization = null,
        CancellationToken cancellationToken = default)
    {
        if (!preferences.EnableTray)
        {
            logger?.Info("tray: disabled by EnableTray=false.");
            return;
        }

        var trayModel = model ?? CreateDefaultModel(localization);
        installation = await trayService.InstallAsync(trayModel, cancellationToken).ConfigureAwait(false);
        logger?.Info($"tray: hosted={installation.IsHosted.ToString().ToLowerInvariant()} ({installation.Capability.Detail})");
    }

    public static TrayModel CreateDefaultModel(BootstrapLocalization? localization = null)
    {
        var openLabel = localization?.Resolve(LOC.OpenPlaynite) ?? "Open Playnite";
        var exitLabel = localization?.Resolve(LOC.ExitAppLabel) ?? "Exit";
        return new TrayModel(
            Id: "playnite",
            Title: "Playnite",
            Menu:
            [
                new TrayMenuItem(OpenMenuItemId, openLabel, Enabled: true, IsDefault: true),
                new TrayMenuItem(ExitMenuItemId, exitLabel, Enabled: true),
            ],
            IconName: "playnite");
    }

    public void AttachWindow(Window window, bool alreadyOpened = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        DetachWindow();
        attachedWindow = window;
        window.Opened += OnWindowOpened;
        window.Closing += OnWindowClosing;
        window.PropertyChanged += OnWindowPropertyChanged;
        if (alreadyOpened)
        {
            ApplyStartMinimized(window);
        }
    }

    public void RestoreWindow()
    {
        var window = windowProvider?.Invoke() ?? attachedWindow;
        if (window is null)
        {
            return;
        }

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

    public void RequestExit()
    {
        exitRequested = true;
        if (shutdownAction is not null)
        {
            shutdownAction();
            return;
        }

        var window = windowProvider?.Invoke() ?? attachedWindow;
        window?.Close();
    }

    public void HandleMenuItem(string id)
    {
        if (string.Equals(id, OpenMenuItemId, StringComparison.Ordinal))
        {
            RestoreWindow();
        }
        else if (string.Equals(id, ExitMenuItemId, StringComparison.Ordinal))
        {
            RequestExit();
        }
    }

    public void HandleTrayClick() => RestoreWindow();

    public async ValueTask DisposeAsync()
    {
        DetachWindow();
        var current = Interlocked.Exchange(ref installation, null);
        if (current is not null)
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void DetachWindow()
    {
        if (attachedWindow is null)
        {
            return;
        }

        attachedWindow.Opened -= OnWindowOpened;
        attachedWindow.Closing -= OnWindowClosing;
        attachedWindow.PropertyChanged -= OnWindowPropertyChanged;
        attachedWindow = null;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            ApplyStartMinimized(window);
        }
    }

    private void ApplyStartMinimized(Window window)
    {
        if (startMinimizedApplied || !preferences.StartMinimized)
        {
            return;
        }

        startMinimizedApplied = true;
        window.WindowState = WindowState.Minimized;
        if (preferences.EnableTray && preferences.MinimizeToTray && IsHosted)
        {
            window.Hide();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not Window window
            || e.Property != Window.WindowStateProperty
            || e.NewValue is not WindowState.Minimized)
        {
            return;
        }

        if (preferences.EnableTray && preferences.MinimizeToTray && IsHosted)
        {
            window.Hide();
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not Window window
            || exitRequested
            || e.IsProgrammatic)
        {
            return;
        }

        if (preferences.EnableTray && preferences.CloseToTray && IsHosted)
        {
            e.Cancel = true;
            window.Hide();
        }
    }
}
