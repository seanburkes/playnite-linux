using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xunit;

namespace HeadlessSpike;

public sealed class SpikeTests
{
    private static readonly string[] KnownArtifacts =
        ["render-1.png", "render-2.png", "render-3.png", "render.json", "automation-tree.json"];

    private static readonly Lazy<string> EvidencePath = new(() =>
    {
        var path = Environment.GetEnvironmentVariable("SPIKE_EVIDENCE_DIR")
            ?? throw new InvalidOperationException("Set SPIKE_EVIDENCE_DIR to an external evidence directory.");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException(
                $"SPIKE_EVIDENCE_DIR '{path}' must be new or empty so stale artifacts cannot be attributed to this run.");
        Directory.CreateDirectory(path);
        return path;
    });

    private static string EvidenceDirectory => EvidencePath.Value;

    [Fact]
    public Task Skia_renders_nonblank_repeatable_pixels_and_a_decodable_png() => Run(() =>
    {
        var captures = new List<byte[]>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var window = new SyntheticWindow();
            try
            {
                window.Show();
                Assert.Equal(1, window.RenderScaling);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Assert.Equal(new PixelSize(640, 480), frame.PixelSize);
                var pixels = Pixels(frame);
                Assert.True(pixels.Chunk(4).Select(p => BitConverter.ToUInt32(p)).Distinct().Count() > 32,
                    "A blank/test renderer frame is not a screenshot.");
                captures.Add(pixels);
                var path = Path.Combine(EvidenceDirectory, $"render-{attempt + 1}.png");
                frame.Save(path, PngBitmapEncoderOptions.Default);
                using var decoded = new Bitmap(path);
                Assert.Equal(frame.PixelSize, decoded.PixelSize);
                Assert.Equal(pixels, Pixels(decoded));
            }
            finally { window.Close(); }
        }

        Assert.Equal(captures[0], captures[1]);
        Assert.Equal(captures[0], captures[2]);
        File.WriteAllText(Path.Combine(EvidenceDirectory, "render.json"), JsonSerializer.Serialize(new
        {
            width = 640, height = 480, scaling = 1, repetitions = 3,
            pixelFormat = "RGBA8888", sha256 = Convert.ToHexString(SHA256.HashData(captures[0])),
            distinctColors = captures[0].Chunk(4).Select(p => BitConverter.ToUInt32(p)).Distinct().Count()
        }, new JsonSerializerOptions { WriteIndented = true }));

        AssertEvidence("render-1.png", "render-2.png", "render-3.png", "render.json");
        Assert.Equal(new[] { "render-1.png", "render-2.png", "render-3.png" },
            Directory.EnumerateFiles(EvidenceDirectory, "render-*.png")
                .Select(path => Path.GetFileName(path)!).Order().ToArray());
    });

    [Fact]
    public Task Automation_peer_tree_exposes_names_roles_states_and_working_providers() => Run(() =>
    {
        var window = new SyntheticWindow();
        try
        {
            window.Show();
            using var frame = window.CaptureRenderedFrame();
            var root = ControlAutomationPeer.CreatePeerForElement(window);
            Assert.NotNull(root);
            var peers = Walk(root).ToArray();
            AutomationPeer Named(string id) => Assert.Single(peers, p => p.GetAutomationId() == id);
            void Check(string id, AutomationControlType role, string name, bool enabled)
            {
                var peer = Named(id);
                Assert.Equal(role, peer.GetAutomationControlType());
                Assert.Equal(name, peer.GetName());
                Assert.Equal(enabled, peer.IsEnabled());
                Assert.False(peer.IsOffscreen());
                Assert.True(peer.GetBoundingRectangle().Width > 0);
            }

            Check("library", AutomationControlType.Window, "Synthetic library spike", true);
            Check("search", AutomationControlType.Edit, "Search games", true);
            Check("installed", AutomationControlType.CheckBox, "Installed only", true);
            Check("games", AutomationControlType.List, "Games", true);
            Check("play", AutomationControlType.Button, "Play Orbit", true);
            Check("details", AutomationControlType.Button, "Details unavailable", false);
            Assert.Contains(peers, p => p.GetAutomationControlType() == AutomationControlType.ListItem
                && p.GetName() == "Orbit — synthetic game");
            Assert.Contains(peers, p => p.GetAutomationControlType() == AutomationControlType.ListItem
                && p.GetName() == "Meadow — synthetic game");
            File.WriteAllText(Path.Combine(EvidenceDirectory, "automation-tree.json"),
                JsonSerializer.Serialize(Snapshot(root), new JsonSerializerOptions { WriteIndented = true }));
            AssertEvidence("automation-tree.json");

            var value = Named("search").GetProvider<IValueProvider>();
            Assert.NotNull(value);
            Assert.Equal("Orbit", value.Value);
            Assert.False(value.IsReadOnly);
            value.SetValue("Meadow");
            Assert.Equal("Meadow", window.FindControl<TextBox>("Search")!.Text);
            var toggle = Named("installed").GetProvider<IToggleProvider>();
            Assert.NotNull(toggle);
            Assert.Equal(ToggleState.On, toggle.ToggleState);
            toggle.Toggle();
            Assert.Equal(ToggleState.Off, toggle.ToggleState);
            Assert.False(window.FindControl<CheckBox>("Installed")!.IsChecked);
            var invoke = Named("play").GetProvider<IInvokeProvider>();
            Assert.NotNull(invoke);
            invoke.Invoke();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Invoked: Orbit (no process launched)", window.FindControl<TextBlock>("Status")!.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Keyboard_tab_navigation_and_space_activation_reach_the_synthetic_controls() => Run(() =>
    {
        var window = new SyntheticWindow();
        try
        {
            window.Show();
            using var frame = window.CaptureRenderedFrame();
            var search = window.FindControl<TextBox>("Search")!;
            var installed = window.FindControl<CheckBox>("Installed")!;
            var games = window.FindControl<ListBox>("Games")!;
            var play = window.FindControl<Button>("Play")!;
            Assert.True(search.Focus());
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            Assert.True(installed.IsFocused);
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.False(installed.IsChecked);
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            Assert.True(games.IsKeyboardFocusWithin);
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            Assert.True(play.IsFocused);
            Assert.True(ControlAutomationPeer.CreatePeerForElement(play)!.HasKeyboardFocus());
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.Equal("Invoked: Orbit (no process launched)", window.FindControl<TextBlock>("Status")!.Text);
        }
        finally { window.Close(); }
    });

    private static async Task Run(Action test)
    {
        _ = EvidenceDirectory; // fail before any work if the evidence directory is not fresh
        await using var session = HeadlessUnitTestSession.StartNew(typeof(SpikeApplication));
        await session.Dispatch(test, CancellationToken.None);
    }

    private static void AssertEvidence(params string[] expected)
    {
        foreach (var name in expected)
        {
            var path = Path.Combine(EvidenceDirectory, name);
            Assert.True(File.Exists(path), $"Expected evidence artifact '{name}' was not written.");
            Assert.True(new FileInfo(path).Length > 0, $"Expected evidence artifact '{name}' is empty.");
        }

        Assert.DoesNotContain(Directory.EnumerateFiles(EvidenceDirectory)
            .Select(path => Path.GetFileName(path)!), name => !KnownArtifacts.Contains(name));
    }

    private static IEnumerable<AutomationPeer> Walk(AutomationPeer peer)
    {
        yield return peer;
        foreach (var child in peer.GetChildren())
            foreach (var descendant in Walk(child))
                yield return descendant;
    }

    private static object Snapshot(AutomationPeer peer) => new
    {
        id = peer.GetAutomationId(), name = peer.GetName(), role = peer.GetAutomationControlType().ToString(),
        enabled = peer.IsEnabled(), focusable = peer.IsKeyboardFocusable(), offscreen = peer.IsOffscreen(),
        value = peer.GetProvider<IValueProvider>()?.Value,
        toggle = peer.GetProvider<IToggleProvider>()?.ToggleState.ToString(),
        invoke = peer.GetProvider<IInvokeProvider>() is not null,
        children = peer.GetChildren().Select(Snapshot).ToArray()
    };

    private static byte[] Pixels(Bitmap bitmap)
    {
        using var target = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Premul);
        using var buffer = target.Lock();
        bitmap.CopyPixels(buffer);
        var bytes = new byte[buffer.RowBytes * bitmap.PixelSize.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return bytes;
    }
}
