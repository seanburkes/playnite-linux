using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.UriHandling;
using Playnite.Desktop;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Shell;
using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Integration;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;
using Playnite.SDK;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class TrayProtocolKeyboardTests
{
    private static readonly Guid SampleGameId = Guid.Parse("22222222-3333-4444-8555-666666666661");

    [Fact]
    public void DesktopShortcutHandler_maps_v10_in_app_gestures_and_ignores_unmapped_keys()
    {
        Assert.Equal(DesktopShortcutAction.OpenSettings, DesktopShortcutHandler.Resolve(Key.F4, KeyModifiers.None));
        Assert.Equal(DesktopShortcutAction.OpenAbout, DesktopShortcutHandler.Resolve(Key.F1, KeyModifiers.None));
        Assert.Equal(DesktopShortcutAction.FocusSearch, DesktopShortcutHandler.Resolve(Key.F, KeyModifiers.Control));
        Assert.Equal(DesktopShortcutAction.Shutdown, DesktopShortcutHandler.Resolve(Key.Q, KeyModifiers.Alt));

        Assert.Equal(DesktopShortcutAction.None, DesktopShortcutHandler.Resolve(Key.F, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(DesktopShortcutAction.None, DesktopShortcutHandler.Resolve(Key.F4, KeyModifiers.Alt));
        Assert.Equal(DesktopShortcutAction.None, DesktopShortcutHandler.Resolve(Key.A, KeyModifiers.None));
    }

    [Fact]
    public void DesktopActivationBridge_parses_uridata_and_positional_playnite_arguments()
    {
        Assert.True(DesktopActivationBridge.TryExtractUriArgument(["--uridata", "playnite://playnite/search/Hades"], out var uri1));
        Assert.Equal("playnite://playnite/search/Hades", uri1);

        Assert.True(DesktopActivationBridge.TryExtractUriArgument(["--uridata=playnite://playnite/view/desktop"], out var uri2));
        Assert.Equal("playnite://playnite/view/desktop", uri2);

        Assert.True(DesktopActivationBridge.TryExtractUriArgument(["playnite://playnite/filter/favorite/true"], out var uri3));
        Assert.Equal("playnite://playnite/filter/favorite/true", uri3);

        Assert.False(DesktopActivationBridge.TryExtractUriArgument(["--headless", "--smoke"], out _));
        Assert.False(DesktopActivationBridge.TryExtractUriArgument(["--uridata"], out _));
        Assert.False(DesktopActivationBridge.TryExtractUriArgument(["--uridata="], out _));
    }

    [Fact]
    public async Task TrayController_and_AvaloniaTrayIconPresenter_manage_lifecycle_and_degrade_when_unhosted()
    {
        await RunHeadlessAsync(() =>
        {
            var app = Application.Current!;

            var disabledService = new FakeTrayService(hosted: true, app);
            var disabledController = new TrayController(
                disabledService,
                new TrayPreferences(EnableTray: false, MinimizeToTray: true, CloseToTray: true, StartMinimized: false));
            disabledController.InitializeAsync().GetAwaiter().GetResult();
            Assert.False(disabledController.IsHosted);
            Assert.Equal(0, disabledService.InstallCount);

            var unhostedService = new FakeTrayService(hosted: false, app);
            var unhostedController = new TrayController(
                unhostedService,
                new TrayPreferences(EnableTray: true, MinimizeToTray: true, CloseToTray: true, StartMinimized: false));
            unhostedController.InitializeAsync().GetAwaiter().GetResult();
            Assert.False(unhostedController.IsHosted);

            var unhostedWindow = new Window { Width = 320, Height = 200 };
            unhostedController.AttachWindow(unhostedWindow);
            unhostedWindow.Show();
            PumpFrames();
            unhostedWindow.WindowState = WindowState.Minimized;
            PumpFrames();
            Assert.True(unhostedWindow.IsVisible);
            unhostedWindow.Close();
            unhostedController.DisposeAsync().AsTask().GetAwaiter().GetResult();

            var shutdownCalled = false;
            var hostedService = new FakeTrayService(hosted: true, app);
            var hostedController = new TrayController(
                hostedService,
                new TrayPreferences(EnableTray: true, MinimizeToTray: true, CloseToTray: true, StartMinimized: true),
                shutdownAction: () => shutdownCalled = true);
            hostedController.InitializeAsync().GetAwaiter().GetResult();
            Assert.True(hostedController.IsHosted);
            Assert.NotEmpty(TrayIcon.GetIcons(app)!);

            var hostedWindow = new Window { Width = 320, Height = 200 };
            hostedController.AttachWindow(hostedWindow);
            hostedWindow.Show();
            PumpFrames();
            Assert.Equal(WindowState.Minimized, hostedWindow.WindowState);
            Assert.False(hostedWindow.IsVisible);

            hostedController.HandleMenuItem(TrayController.OpenMenuItemId);
            PumpFrames();
            Assert.True(hostedWindow.IsVisible);
            Assert.Equal(WindowState.Normal, hostedWindow.WindowState);

            hostedController.HandleMenuItem(TrayController.ExitMenuItemId);
            Assert.True(shutdownCalled);

            hostedWindow.Close();
            hostedController.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Empty(TrayIcon.GetIcons(app)!);
        });
    }

    [Fact]
    public async Task MainWindow_shortcuts_focus_search_and_request_exit()
    {
        using var profile = new ScopedXdgProfile();
        await profile.WriteWelcomeAcknowledgedAsync();

        await RunHeadlessAsync(() =>
        {
            var shutdownRequested = false;
            var trayService = new FakeTrayService(hosted: true, Application.Current!);
            var tray = new TrayController(
                trayService,
                TrayPreferences.Default,
                shutdownAction: () => shutdownRequested = true);
            tray.InitializeAsync().GetAwaiter().GetResult();

            var window = new MainWindow { Width = 1024, Height = 640 };
            window.AttachTray(tray);
            try
            {
                window.Show();
                PumpUntil(() =>
                {
                    var grid = FindDescendant<LibraryGridView>(window);
                    return grid?.FindControl<TextBox>("SearchBox") is { IsVisible: true };
                });

                window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
                PumpFrames();
                var searchBox = FindDescendant<LibraryGridView>(window)!.FindControl<TextBox>("SearchBox")!;
                Assert.True(searchBox.IsFocused);

                window.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Alt);
                PumpFrames();
                Assert.True(shutdownRequested);
            }
            finally
            {
                window.Close();
                PumpFrames();
                tray.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task SingleInstance_activation_routes_playnite_uri_to_MainWindow_command_host()
    {
        using var profile = new ScopedXdgProfile();
        await profile.WriteWelcomeAcknowledgedAsync();
        await profile.SeedGameAsync();

        var directories = XdgDirectories.Resolve(new LinuxEnvironment());
        await using var owner = SingleInstanceService.Start("playnite.test.p7007", directories);
        Assert.Equal(SingleInstanceStatus.Owner, owner.Result.Status);

        await RunHeadlessAsync(() =>
        {
            var window = new MainWindow { Width = 1024, Height = 640 };
            var host = new DesktopUriCommandHost(() => window);
            var api = new UriHandlerApi(host);
            using var bridge = new DesktopActivationBridge(owner, api);
            bridge.Start();

            try
            {
                window.Show();
                PumpUntil(() =>
                {
                    var grid = FindDescendant<LibraryGridView>(window);
                    return grid?.FindControl<ListBox>("GameList") is { ItemCount: 1 };
                });

                window.Hide();
                PumpFrames();
                Assert.False(window.IsVisible);

                var forwardTask = Task.Run(async () =>
                {
                    await using var second = SingleInstanceService.Start("playnite.test.p7007", directories);
                    Assert.Equal(SingleInstanceStatus.AlreadyRunning, second.Result.Status);
                    var forwarded = await second.ActivateAsync("playnite://playnite/search/Alpha");
                    Assert.Equal(SingleInstanceStatus.Forwarded, forwarded.Status);
                });

                var searchBox = FindDescendant<LibraryGridView>(window)!.FindControl<TextBox>("SearchBox")!;
                PumpUntil(() => forwardTask.IsCompleted && window.IsVisible && searchBox.Text == "Alpha");
                forwardTask.GetAwaiter().GetResult();
                Assert.True(window.IsVisible);
                Assert.Equal("Alpha", searchBox.Text);

                var showGame = bridge.DispatchUriAsync($"playnite://playnite/showgame/{SampleGameId:D}")
                    .GetAwaiter().GetResult();
                Assert.Equal(UriDispatchStatus.Completed, showGame.Status);

                var filter = bridge.DispatchUriAsync("playnite://playnite/filter/favorite/true")
                    .GetAwaiter().GetResult();
                Assert.Equal(UriDispatchStatus.Completed, filter.Status);

                var desktopView = bridge.DispatchUriAsync("playnite://playnite/view/desktop")
                    .GetAwaiter().GetResult();
                Assert.Equal(UriDispatchStatus.Completed, desktopView.Status);

                var fullscreenView = bridge.DispatchUriAsync("playnite://playnite/view/fullscreen")
                    .GetAwaiter().GetResult();
                Assert.Equal(UriDispatchStatus.Unavailable, fullscreenView.Status);

                var startGame = bridge.DispatchUriAsync($"playnite://playnite/start/{SampleGameId:D}")
                    .GetAwaiter().GetResult();
                Assert.Equal(UriDispatchStatus.Unavailable, startGame.Status);
            }
            finally
            {
                window.Close();
                PumpFrames();
            }
        });
    }

    [Fact]
    public async Task ProtocolHandler_registers_desktop_entry_and_mimeapps_in_isolated_xdg()
    {
        using var profile = new ScopedXdgProfile();
        var env = new LinuxEnvironment();
        var directories = XdgDirectories.Resolve(env);
        var handler = new PlayniteProtocolHandler(
            directories,
            "/opt/playnite/Playnite.Desktop",
            env,
            new SystemProtocolFileSystem());

        var result = await handler.RegisterAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProtocolRegistrationStatus.Registered, result.Status);
        Assert.True(File.Exists(handler.DesktopEntryPath));
        Assert.True(File.Exists(handler.MimeAppsPath));

        var desktopContent = await File.ReadAllTextAsync(handler.DesktopEntryPath, TestContext.Current.CancellationToken);
        Assert.Contains("--uridata %u", desktopContent);
        Assert.Contains("x-scheme-handler/playnite", desktopContent);
    }

    private static async Task RunHeadlessAsync(Action work)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
        await session.Dispatch(work, CancellationToken.None);
    }

    private static void PumpFrames(int count = 4)
    {
        for (var i = 0; i < count; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void PumpUntil(Func<bool> predicate, int maxIterations = 200)
    {
        for (var i = 0; i < maxIterations; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (predicate())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail("Timed out waiting for headless UI state.");
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

    private sealed class FakeTrayService(bool hosted, Application application) : ITrayService
    {
        private readonly AvaloniaTrayIconPresenter presenter = new(application);

        public int InstallCount { get; private set; }

        public async Task<TrayInstallation> InstallAsync(TrayModel model, CancellationToken cancellationToken = default)
        {
            InstallCount++;
            if (!hosted)
            {
                return new TrayInstallation(model, DesktopCapability.Unavailable(DesktopFacility.Tray, "StatusNotifierWatcher absent"));
            }

            var handle = await presenter.PresentAsync(model, cancellationToken).ConfigureAwait(false);
            return new TrayInstallation(model, DesktopCapability.Available(DesktopFacility.Tray, "fake-sni", "fake-sni"), handle);
        }
    }

    private sealed class ScopedXdgProfile : IDisposable
    {
        private static readonly string[] EnvKeys =
            ["XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_RUNTIME_DIR", "HOME"];

        private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);
        public string Root { get; }
        public string ConfigPlaynite { get; }
        public string DataPlaynite { get; }

        public ScopedXdgProfile()
        {
            Root = Path.Combine(Path.GetTempPath(), "playnite-p7-007-" + Path.GetRandomFileName());
            ConfigPlaynite = Path.Combine(Root, "config", "playnite");
            DataPlaynite = Path.Combine(Root, "data", "playnite");

            var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["XDG_CONFIG_HOME"] = Path.Combine(Root, "config"),
                ["XDG_STATE_HOME"] = Path.Combine(Root, "state"),
                ["XDG_DATA_HOME"] = Path.Combine(Root, "data"),
                ["XDG_CACHE_HOME"] = Path.Combine(Root, "cache"),
                ["XDG_RUNTIME_DIR"] = Path.Combine(Root, "runtime"),
                ["HOME"] = Path.Combine(Root, "home"),
            };

            foreach (var key in EnvKeys)
            {
                previous[key] = Environment.GetEnvironmentVariable(key);
                Directory.CreateDirectory(mapping[key]);
                Environment.SetEnvironmentVariable(key, mapping[key]);
            }

            Directory.CreateDirectory(ConfigPlaynite);
            Directory.CreateDirectory(DataPlaynite);
        }

        public Task WriteWelcomeAcknowledgedAsync()
            => File.WriteAllTextAsync(Path.Combine(ConfigPlaynite, "welcome-acknowledged"), "acknowledged\n");

        public async Task SeedGameAsync()
        {
            var libraryPath = Path.Combine(DataPlaynite, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(libraryPath);
            var game = new Game(SampleGameId, "game-001", "Alpha Game")
            {
                IsInstalled = true,
                Favorite = true,
            };
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(game));
                return true;
            });
        }

        public void Dispose()
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
