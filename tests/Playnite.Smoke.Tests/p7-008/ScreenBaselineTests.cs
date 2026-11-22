using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Headless Skia screenshot and semantic-tree baseline verification for p7-008.
/// AT-SPI / screen-reader accessibility is deferred for v1 per docs/port/00-DECISIONS.md (D1);
/// semantic-tree assertions and keyboard Tab checks verify UI structure and focusability only.
/// </summary>
[Collection("HeadlessScreens")]
public sealed class ScreenBaselineTests
{
    private const int ChannelTolerance = 2;
    private const double MaxDifferingPixelRatio = 0.001;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly Guid GameOneId = Guid.Parse("11111111-2222-4333-8444-555555555551");
    private static readonly Guid GameTwoId = Guid.Parse("11111111-2222-4333-8444-555555555552");

    [Fact]
    public async Task Main_shell_matches_screenshot_and_semantic_tree_baselines()
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
                // Allow a second layout/render pass after async library load settles.
                PumpFrames(window, 5);

                AssertScreenBaseline(window, "main-shell", 1280, 720);

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
    public async Task Welcome_overlay_matches_screenshot_and_semantic_tree_baselines_and_tab_focus()
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
                PumpFrames(window, 3);

                AssertScreenBaseline(window, "welcome", 1280, 720);

                var shell = window.FindControl<ShellView>("ShellRoot")!;
                var ack = shell.FindControl<Button>("AcknowledgeButton")!;
                Assert.True(ack.Focus());
                Assert.True(ack.IsFocused);
                var peer = ControlAutomationPeer.CreatePeerForElement(ack);
                Assert.NotNull(peer);
                Assert.True(peer.HasKeyboardFocus());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task About_and_messagebox_views_match_screenshot_and_semantic_tree_baselines()
    {
        await RunHeadlessAsync(() =>
        {
            var aboutWindow = new Window
            {
                Title = "About Playnite",
                Width = 480,
                Height = 240,
                Content = new AboutView("About Playnite", "Playnite Linux"),
            };
            try
            {
                aboutWindow.Show();
                PumpFrames(aboutWindow, 2);
                AssertScreenBaseline(aboutWindow, "about", 480, 240);
            }
            finally
            {
                aboutWindow.Close();
            }

            var messageWindow = new Window
            {
                Title = "Notice",
                Width = 480,
                Height = 240,
                Content = new MessageBoxView("Notice", "Baseline message"),
            };
            try
            {
                messageWindow.Show();
                PumpFrames(messageWindow, 2);
                AssertScreenBaseline(messageWindow, "messagebox", 480, 240);
            }
            finally
            {
                messageWindow.Close();
            }
        });
    }

    [Fact]
    public void Pixel_comparator_detects_layout_or_color_regressions()
    {
        var baseline = new byte[400];
        var identical = new byte[400];
        var shifted = new byte[400];
        shifted[0] = 255;
        shifted[1] = 255;
        shifted[2] = 255;
        shifted[3] = 255;

        Assert.True(ComparePixels(baseline, identical).Matched);
        Assert.False(ComparePixels(baseline, shifted).Matched);
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

    private static void PumpFrames(Window window, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Dispatcher.UIThread.RunJobs();
            using var _ = window.CaptureRenderedFrame();
        }
    }

    private static void AssertScreenBaseline(Window window, string screenName, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(1.0, window.RenderScaling);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), frame.PixelSize);

        var actualPixels = ExtractPixels(frame);
        var distinctColors = actualPixels.Chunk(4).Select(p => BitConverter.ToUInt32(p)).Distinct().Count();
        Assert.True(distinctColors > 4, $"Screen '{screenName}' rendered a blank or flat frame ({distinctColors} distinct colors).");

        var root = ControlAutomationPeer.CreatePeerForElement(window);
        Assert.NotNull(root);
        var actualTreeJson = JsonSerializer.Serialize(SnapshotPeer(root), JsonOptions) + "\n";

        var screensDir = LocateScreensDirectory();
        var pngPath = Path.Combine(screensDir, $"{screenName}.png");
        var treePath = Path.Combine(screensDir, $"{screenName}.tree.json");

        if (string.Equals(Environment.GetEnvironmentVariable("PLAYNITE_UPDATE_SCREENS"), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(screensDir);
            frame.Save(pngPath, PngBitmapEncoderOptions.Default);
            File.WriteAllText(treePath, actualTreeJson);
        }

        Assert.True(File.Exists(pngPath), $"Missing screenshot baseline '{pngPath}'.");
        Assert.True(File.Exists(treePath), $"Missing semantic-tree baseline '{treePath}'.");

        using var baselineBitmap = new Bitmap(pngPath);
        Assert.Equal(frame.PixelSize, baselineBitmap.PixelSize);
        var baselinePixels = ExtractPixels(baselineBitmap);
        var diff = ComparePixels(baselinePixels, actualPixels);
        Assert.True(
            diff.Matched,
            $"Screen '{screenName}' pixel mismatch: {diff.DifferingPixels}/{diff.TotalPixels} pixels differed " +
            $"(ratio {diff.Ratio:P4} > {MaxDifferingPixelRatio:P4}, max channel delta {diff.MaxDelta}).");

        var expectedTreeJson = File.ReadAllText(treePath);
        Assert.Equal(expectedTreeJson.Replace("\r\n", "\n"), actualTreeJson.Replace("\r\n", "\n"));
    }

    private static (bool Matched, int DifferingPixels, int TotalPixels, double Ratio, int MaxDelta) ComparePixels(
        byte[] expected,
        byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        var totalPixels = expected.Length / 4;
        var differing = 0;
        var maxDelta = 0;

        for (var i = 0; i < expected.Length; i += 4)
        {
            var dr = Math.Abs(expected[i] - actual[i]);
            var dg = Math.Abs(expected[i + 1] - actual[i + 1]);
            var db = Math.Abs(expected[i + 2] - actual[i + 2]);
            var da = Math.Abs(expected[i + 3] - actual[i + 3]);
            var delta = Math.Max(Math.Max(dr, dg), Math.Max(db, da));
            if (delta > maxDelta)
            {
                maxDelta = delta;
            }
            if (delta > ChannelTolerance)
            {
                differing++;
            }
        }

        var ratio = totalPixels == 0 ? 0.0 : (double)differing / totalPixels;
        return (ratio <= MaxDifferingPixelRatio, differing, totalPixels, ratio, maxDelta);
    }

    private static byte[] ExtractPixels(Bitmap bitmap)
    {
        using var target = new WriteableBitmap(
            bitmap.PixelSize,
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Premul);
        using var buffer = target.Lock();
        bitmap.CopyPixels(buffer);
        var bytes = new byte[buffer.RowBytes * bitmap.PixelSize.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    private static object SnapshotPeer(AutomationPeer peer) => new
    {
        id = peer.GetAutomationId(),
        name = peer.GetName(),
        role = peer.GetAutomationControlType().ToString(),
        enabled = peer.IsEnabled(),
        focusable = peer.IsKeyboardFocusable(),
        offscreen = peer.IsOffscreen(),
        children = peer.GetChildren().Select(SnapshotPeer).ToArray(),
    };

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

    private static string LocateScreensDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Playnite.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "screens");
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root containing Playnite.slnx.");
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
