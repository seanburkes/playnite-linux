using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Desktop.Bootstrap;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;
using Playnite.Settings;

namespace Playnite.Desktop.Shell;

public sealed partial class ShellView : UserControl
{
    private readonly SettingsCatalogs catalogs;
    private readonly DesktopPreferencesStore store;
    private BootstrapLocalization catalog = new();
    private CancellationTokenSource? lifetime;
    private Task acknowledging = Task.CompletedTask;
    private Task editing = Task.CompletedTask;
    private TaskCompletionSource<SettingsWindow?>? smokeDialog;
    private Button OpenSettingsAction => this.FindControl<Button>("SettingsButton")!;
    private Button AcknowledgeAction => this.FindControl<Button>("AcknowledgeButton")!;
    private Border SidebarRegion => this.FindControl<Border>("Sidebar")!;
    private Border WelcomeScreen => this.FindControl<Border>("Welcome")!;
    private DockPanel ShellWorkspace => this.FindControl<DockPanel>("Workspace")!;
    private TextBlock WelcomeHeading => this.FindControl<TextBlock>("WelcomeTitle")!;
    private TextBlock WelcomeErrorText => this.FindControl<TextBlock>("WelcomeError")!;

    public ShellView()
    {
        AvaloniaXamlLoader.Load(this);
        var library = this.FindControl<ContentHostView>("LibraryContent")!.LibraryGridControl;
        var all = this.FindControl<Button>("AllButton")!;
        var installed = this.FindControl<Button>("InstalledButton")!;
        var favorites = this.FindControl<Button>("FavoritesButton")!;
        all.Click += (_, _) => library.ApplyQuickFilter(LibraryFilterMode.All);
        installed.Click += (_, _) => library.ApplyQuickFilter(LibraryFilterMode.Installed);
        favorites.Click += (_, _) => library.ApplyQuickFilter(LibraryFilterMode.Favorite);
        library.FilterChanged += (_, mode) =>
        {
            all.Classes.Set("active", mode == LibraryFilterMode.All);
            installed.Classes.Set("active", mode == LibraryFilterMode.Installed);
            favorites.Classes.Set("active", mode == LibraryFilterMode.Favorite);
        };
        var config = XdgDirectories.Resolve(new LinuxEnvironment()).Config;
        catalogs = new(config);
        store = new(Path.Combine(config, "settings.json"), Path.Combine(config, "welcome-acknowledged"));
        Loaded += OnLoaded;
        Unloaded += (_, _) => lifetime?.Cancel();
    }

    private async void OnLoaded(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        using var source = new CancellationTokenSource();
        lifetime = source;
        try
        {
            await InitializeAsync(owner, source.Token);
            await RunSettingsSmokeAsync(owner, source.Token);
            await WaitForCloseAsync(owner);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            source.Cancel();
            lifetime = null;
        }
    }

    private static async Task WaitForCloseAsync(Window owner)
    {
        if (!owner.IsVisible)
        {
            return;
        }

        var close = new TaskCompletionSource();
        void Closed(object? sender, EventArgs args) => close.SetResult();
        owner.Closed += Closed;
        try
        {
            await close.Task;
        }
        finally
        {
            owner.Closed -= Closed;
        }
    }

    private async Task InitializeAsync(Window owner, CancellationToken token)
    {
        try
        {
            var preferences = await store.LoadAsync(token);
            token.ThrowIfCancellationRequested();
            SidebarRegion.IsVisible = preferences.ShowSidebar;
            catalog = catalogs.Load(preferences.Language);
            DataContext = new ShellViewModel(catalog, new AvaloniaDialogService(owner));
            OpenSettingsAction.IsEnabled = true;
        }
        catch (SettingsException exception)
        {
            ShowStartupError($"Could not load settings ({exception.Failure}).");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Text.Json.JsonException or ArgumentException)
        {
            ShowStartupError("Could not load the selected language. Using the default language.");
            OpenSettingsAction.IsEnabled = true;
        }

        OpenSettingsAction.Content = catalog.Resolve(LOC.SettingsLabel);
        WelcomeHeading.Text = catalog.Resolve(LOC.WelcomeLabel);
        AcknowledgeAction.Content = catalog.Resolve(LOC.OKLabel);
        try
        {
            var acknowledged = await store.IsWelcomeAcknowledgedAsync(token);
            token.ThrowIfCancellationRequested();
            if (!acknowledged)
            {
                ShellWorkspace.IsVisible = false;
                WelcomeScreen.IsVisible = true;
                AcknowledgeAction.Focus();
            }
        }
        catch (SettingsException exception)
        {
            ShowStartupError($"Could not read welcome acknowledgment ({exception.Failure}).");
        }
    }

    private void OnSettings(object? sender, RoutedEventArgs args)
    {
        if (OpenSettingsAction.IsEnabled && lifetime is { } source && TopLevel.GetTopLevel(this) is Window owner)
        {
            editing = EditAsync(owner, source.Token);
        }
    }

    private async Task EditAsync(Window owner, CancellationToken token)
    {
        OpenSettingsAction.IsEnabled = false;
        try
        {
            var current = await store.LoadAsync(token);
            token.ThrowIfCancellationRequested();
            var window = new SettingsWindow(store, current, catalogs.List(), catalog, token);
            var result = window.ShowDialog<DesktopPreferences?>(owner);
            smokeDialog?.TrySetResult(window);
            var saved = await result;
            if (saved is not null && !token.IsCancellationRequested)
            {
                SidebarRegion.IsVisible = saved.ShowSidebar;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is SettingsException or IOException or UnauthorizedAccessException)
        {
            ShowStartupError("Could not open Settings.");
        }
        finally
        {
            smokeDialog?.TrySetResult(null);
            OpenSettingsAction.IsEnabled = true;
        }
    }

    private void OnAcknowledge(object? sender, RoutedEventArgs args)
    {
        if (AcknowledgeAction.IsEnabled && lifetime is { } source)
        {
            acknowledging = AcknowledgeAsync(source.Token);
        }
    }

    private async Task AcknowledgeAsync(CancellationToken token)
    {
        AcknowledgeAction.IsEnabled = false;
        WelcomeErrorText.IsVisible = false;
        try
        {
            await store.AcknowledgeWelcomeAsync(token);
            token.ThrowIfCancellationRequested();
            WelcomeScreen.IsVisible = false;
            ShellWorkspace.IsVisible = true;
            OpenSettingsAction.Focus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (SettingsException exception)
        {
            Console.Error.WriteLine($"Welcome acknowledgment failed: {exception.Failure}");
            WelcomeErrorText.Text = "Could not remember your acknowledgment. Try again.";
            WelcomeErrorText.IsVisible = true;
        }
        finally
        {
            AcknowledgeAction.IsEnabled = true;
        }
    }

    private void ShowStartupError(string message)
    {
        Console.Error.WriteLine(message);
        var error = this.FindControl<TextBlock>("StartupError")!;
        error.Text = message;
        error.IsVisible = true;
    }
}
