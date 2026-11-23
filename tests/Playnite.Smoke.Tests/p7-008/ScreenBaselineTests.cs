using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Desktop;
using Playnite.Desktop.Shell;
using Xunit;

namespace Playnite.Smoke.Tests;

[CollectionDefinition("HeadlessScreens", DisableParallelization = true)]
public sealed class HeadlessScreensCollection;

public sealed class HeadlessScreenApplication : Application
{
    public override void Initialize()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessScreenApplication>()
        .WithInterFont()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Headless usability checks for p7-008.
/// AT-SPI / screen-reader accessibility is deferred for v1 per docs/port/00-DECISIONS.md (D1);
/// semantic-tree assertions and keyboard Tab checks verify UI structure and focusability only.
/// </summary>
[Collection("HeadlessScreens")]
public sealed class ScreenBaselineTests
{
    private static readonly Guid GameOneId = Guid.Parse("11111111-2222-4333-8444-555555555551");
    private static readonly Guid GameTwoId = Guid.Parse("11111111-2222-4333-8444-555555555552");

    [Fact]
    public async Task Main_shell_exposes_key_controls_and_supports_tab_navigation()
    {
        using var profile = new ScopedXdgProfile();
        await profile.WriteWelcomeAcknowledgedAsync();
        await profile.SeedLibraryAsync();

        await RunHeadlessAsync(() =>
        {
            var window = new MainWindow
            {
                Width = 1280,
                Height = 720,
            };
            try
            {
                window.Show();
                PumpUntil(() =>
                {
                    var settingsButton = window.FindControl<ShellView>("ShellRoot")
                        ?.FindControl<Button>("SettingsButton");
                    var gameList = FindDescendant<LibraryGridView>(window)
                        ?.FindControl<ListBox>("GameList");
                    return settingsButton is { IsEnabled: true } && gameList is { ItemCount: 2 };
                });
                Dispatcher.UIThread.RunJobs();

                var root = ControlAutomationPeer.CreatePeerForElement(window);
                Assert.NotNull(root);
                var peers = Walk(root).ToArray();
                Assert.Contains(peers, p => p.GetAutomationId() == "TopPanel" && p.GetName() == "Top panel" && !p.IsOffscreen());
                Assert.Contains(peers, p => p.GetAutomationId() == "Sidebar" && p.GetName() == "Sidebar" && !p.IsOffscreen());
                Assert.Contains(peers, p => p.GetAutomationId() == "ContentHost" && p.GetName() == "Content host" && !p.IsOffscreen());
                Assert.Contains(peers, p => p.GetAutomationId() == "Notifications" && p.GetName() == "Notifications" && !p.IsOffscreen());
                Assert.Contains(peers, p => p.GetAutomationId() == "SettingsButton" && p.IsEnabled());
                Assert.Contains(peers, p => p.GetAutomationId() == "AboutButton" && p.GetName() == "About" && p.IsEnabled());
                Assert.Contains(peers, p => p.GetAutomationId() == "MessageBoxButton" && p.GetName() == "Show message" && p.IsEnabled());

                var topPanel = FindDescendant<TopPanelView>(window);
                Assert.NotNull(topPanel);
                var about = topPanel.FindControl<Button>("AboutButton")!;
                var msg = topPanel.FindControl<Button>("MessageBoxButton")!;
                Assert.True(about.Focus());
                Assert.True(about.IsFocused);
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
                window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
                Assert.True(msg.IsFocused);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Welcome_overlay_exposes_a_keyboard_focusable_acknowledgment()
    {
        using var profile = new ScopedXdgProfile();

        await RunHeadlessAsync(() =>
        {
            var window = new MainWindow
            {
                Width = 1280,
                Height = 720,
            };
            try
            {
                window.Show();
                PumpUntil(() =>
                {
                    var shell = window.FindControl<ShellView>("ShellRoot");
                    var welcome = shell?.FindControl<Border>("Welcome");
                    var ack = shell?.FindControl<Button>("AcknowledgeButton");
                    return welcome is { IsVisible: true } && ack is { IsEffectivelyVisible: true };
                });
                var shell = window.FindControl<ShellView>("ShellRoot")!;
                var ack = shell.FindControl<Button>("AcknowledgeButton")!;
                Assert.True(ack.Focus());
                Assert.True(ack.IsFocused);
                var peer = ControlAutomationPeer.CreatePeerForElement(ack);
                Assert.NotNull(peer);
                Assert.False(peer.IsOffscreen());
                Assert.True(peer.IsEnabled());
                Assert.True(peer.HasKeyboardFocus());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task About_view_displays_title_and_author()
    {
        await RunHeadlessAsync(() =>
        {
            var view = new AboutView("About Playnite", "Playnite Linux");
            AssertDialogText(view, "About Playnite", "Playnite Linux");
        });
    }

    [Fact]
    public async Task Messagebox_view_displays_title_and_message()
    {
        await RunHeadlessAsync(() =>
        {
            var view = new MessageBoxView("Notice", "Game could not be launched.");
            AssertDialogText(view, "Notice", "Game could not be launched.");
        });
    }

    private static async Task RunHeadlessAsync(Action action)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
        await session.Dispatch(action, CancellationToken.None);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }
            Thread.Sleep(15);
        }

        Assert.True(condition(), "Timed out waiting for headless UI initialization to settle.");
    }

    private static void AssertDialogText(UserControl view, string title, string message)
    {
        var window = new Window { Width = 480, Height = 240, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var text = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
                .OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible && block.Bounds.Width > 0 && block.Bounds.Height > 0)
                .Select(block => block.Text)
                .ToArray();
            Assert.Contains(title, text);
            Assert.Contains(message, text);
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<AutomationPeer> Walk(AutomationPeer peer)
    {
        yield return peer;
        foreach (var child in peer.GetChildren())
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    private static T? FindDescendant<T>(Control root) where T : Control
    {
        foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(root))
        {
            if (child is T match)
            {
                return match;
            }
            if (child is Control childControl)
            {
                var found = FindDescendant<T>(childControl);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        return null;
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
            Root = Path.Combine(Path.GetTempPath(), "playnite-p7-008-" + Path.GetRandomFileName());
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

        public async Task WriteWelcomeAcknowledgedAsync()
        {
            await File.WriteAllTextAsync(Path.Combine(ConfigPlaynite, "welcome-acknowledged"), "acknowledged\n");
        }

        public async Task SeedLibraryAsync()
        {
            var libraryPath = Path.Combine(DataPlaynite, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(libraryPath);
            var gameOne = new Game(GameOneId, "game-001", "Alpha Game")
            {
                IsInstalled = true,
                UserScore = 85,
                ReleaseDate = new ReleaseDate(2020),
            };
            var gameTwo = new Game(GameTwoId, "game-002", "Beta Game")
            {
                IsInstalled = false,
                UserScore = 72,
                ReleaseDate = new ReleaseDate(2022),
            };
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(gameOne));
                await session.Games.AddAsync(new StoredGame(gameTwo));
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
