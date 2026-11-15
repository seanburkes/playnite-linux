using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace UiShellSpike;

public sealed class ShellTests
{
    private static readonly Lazy<string> Evidence = new(() =>
    {
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")));
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));
        var path = Environment.GetEnvironmentVariable("SPIKE_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Set SPIKE_EVIDENCE_DIR to a new or empty external directory.");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("SPIKE_EVIDENCE_DIR must be new or empty.");
        Directory.CreateDirectory(path);
        return path;
    });

    [Fact]
    public Task Grid_binds_every_fake_cover_title_playtime_and_last_played() => Run(window =>
    {
        var grid = window.FindControl<ListBox>("Games")!;
        Assert.Equal("6 invented games", window.FindControl<TextBlock>("Count")!.Text);
        Assert.Equal(LibraryModel.Catalog, grid.Items.Cast<Game>());
        Assert.Equal(6, grid.GetVisualDescendants().OfType<ListBoxItem>().Count());
        foreach (var game in LibraryModel.Catalog)
        {
            var cell = Assert.Single(grid.GetVisualDescendants().OfType<ListBoxItem>(), c => Equals(c.DataContext, game));
            var text = cell.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains(game.Title, text);
            Assert.Contains(game.Playtime, text);
            Assert.Contains(game.LastPlayed, text);
            Assert.Equal(game.Motif, Assert.Single(cell.GetVisualDescendants().OfType<CoverArt>()).Motif);
        }
    });

    [Fact]
    public Task Search_and_installed_filter_narrow_the_bound_grid_and_clear_empty_details() => Run(window =>
    {
        var search = window.FindControl<TextBox>("Search")!;
        var grid = window.FindControl<ListBox>("Games")!;
        search.Text = "cLoUd";
        Refresh(window);
        Assert.Equal("cLoUd", window.Model.Search);
        Assert.Equal("Cloud Orchard", Assert.Single(grid.Items.Cast<Game>()).Title);
        Assert.Equal("Cloud Orchard", window.FindControl<TextBlock>("DetailsTitle")!.Text);
        window.FindControl<CheckBox>("Installed")!.IsChecked = true;
        Refresh(window);
        Assert.True(window.Model.InstalledOnly);
        Assert.Empty(grid.Items);
        Assert.Equal("0 invented games", window.FindControl<TextBlock>("Count")!.Text);
        Assert.True(string.IsNullOrEmpty(window.FindControl<TextBlock>("DetailsTitle")!.Text));
        search.Text = "";
        Refresh(window);
        Assert.Equal(4, grid.ItemCount);
        Assert.All(grid.Items.Cast<Game>(), game => Assert.True(game.Installed));
        Assert.Equal("4 invented games", window.FindControl<TextBlock>("Count")!.Text);
    });

    [Fact]
    public Task Selecting_a_cell_updates_all_bound_details() => Run(window =>
    {
        var selected = LibraryModel.Catalog[2];
        window.FindControl<ListBox>("Games")!.SelectedItem = selected;
        Refresh(window);
        Assert.Same(selected, window.Model.SelectedGame);
        Assert.Equal(selected.Title, window.FindControl<TextBlock>("DetailsTitle")!.Text);
        Assert.Equal(selected.Playtime, window.FindControl<TextBlock>("DetailsPlaytime")!.Text);
        Assert.Equal(selected.LastPlayed, window.FindControl<TextBlock>("DetailsLastPlayed")!.Text);
    });

    [Fact]
    public Task Narrow_and_wide_layouts_keep_cells_search_and_details_inside_the_frame() => Run(window =>
    {
        foreach (var width in new[] { 640, 1200 })
        {
            window.Width = width;
            Refresh(window);
            var details = window.FindControl<Border>("Details")!;
            var games = window.FindControl<ListBox>("Games")!;
            Assert.Equal(width == 640 ? 1 : 0, Grid.GetRow(details));
            foreach (var control in new Control[] { details, games, window.FindControl<TextBox>("Search")! })
                Inside(window, control);
            var cells = games.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            Assert.Equal(6, cells.Length);
            foreach (var cell in cells)
            {
                Inside(window, cell);
                var cover = Assert.Single(cell.GetVisualDescendants().OfType<CoverArt>());
                Assert.Equal(width == 640 ? 100 : 138, cover.Height);
                Assert.Equal(cover.Height, cover.Bounds.Height);
                foreach (var text in cell.GetVisualDescendants().OfType<TextBlock>())
                {
                    var point = text.TranslatePoint(default, games)!.Value;
                    Assert.True(point.Y >= 0 && point.Y + text.Bounds.Height <= games.Bounds.Height,
                        "Every cell's metadata must fit in the library viewport at these tested sizes.");
                }
            }
            var first = cells[0].TranslatePoint(default, window)!.Value;
            var second = cells[1].TranslatePoint(default, window)!.Value;
            Assert.True(second.X > first.X, "The library must be a grid, not a vertical list.");
            if (width == 640)
                Assert.True(details.TranslatePoint(default, window)!.Value.Y >= games.TranslatePoint(default, window)!.Value.Y + games.Bounds.Height);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(new PixelSize(width, 800), frame.PixelSize);
            frame.Save(Path.Combine(Evidence.Value, $"layout-{width}.png"), PngBitmapEncoderOptions.Default);
        }
    });

    [Fact]
    public Task Replacing_a_theme_dictionary_updates_resource_driven_controls() => Run(window =>
    {
        var dictionaries = Application.Current!.Resources.MergedDictionaries;
        var original = dictionaries[0];
        var spacingOverride = new ResourceDictionary { ["CoverHeight"] = 110d };
        var replacement = new ResourceDictionary();
        replacement["CanvasColor"] = Color.Parse("#362144");
        replacement["SurfaceColor"] = Color.Parse("#51335E");
        replacement["TextColor"] = Colors.White;
        replacement["MutedColor"] = Colors.LightGray;
        replacement["AccentColor"] = Colors.Gold;
        replacement["CoverColor"] = Colors.DarkSlateBlue;
        try
        {
            var before = ((ISolidColorBrush)window.Background!).Color;
            var cover = window.GetVisualDescendants().OfType<CoverArt>().First();
            var card = window.FindControl<ListBox>("Games")!.GetVisualDescendants().OfType<Border>()
                .First(border => border.Classes.Contains("surface"));
            Assert.Equal(138, cover.Height);
            dictionaries[0] = replacement;
            dictionaries.Add(spacingOverride);
            Refresh(window);
            Assert.NotEqual(before, ((ISolidColorBrush)window.Background!).Color);
            Assert.Equal(Color.Parse("#362144"), ((ISolidColorBrush)window.Background!).Color);
            Assert.Equal(Colors.Gold, ((ISolidColorBrush)window.FindResource("AccentBrush")!).Color);
            Assert.Equal(Colors.DarkSlateBlue, ((ISolidColorBrush)cover.Fill).Color);
            Assert.Equal(Colors.Gold, ((ISolidColorBrush)cover.Accent).Color);
            Assert.Equal(Color.Parse("#51335E"), ((ISolidColorBrush)card.Background!).Color);
            Assert.Equal(110, cover.Height);
            Assert.Equal(110, cover.Bounds.Height);
        }
        finally
        {
            dictionaries.Remove(spacingOverride);
            dictionaries[0] = original;
        }
    });

    [Fact]
    public Task Skia_captures_are_nonblank_decodable_and_byte_identical() => Run(window =>
    {
        var files = new List<byte[]>();
        var pixels = new List<byte[]>();
        for (var i = 1; i <= 3; i++)
        {
            // A new window each time checks more than repeated reads of the same render target.
            var fresh = new LibraryWindow();
            try
            {
                fresh.Show();
                Refresh(fresh);
                Assert.Equal(1, fresh.RenderScaling);
                using var frame = fresh.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Assert.Equal(new PixelSize(1200, 800), frame.PixelSize);
                var path = Path.Combine(Evidence.Value, $"render-{i}.png");
                frame.Save(path, PngBitmapEncoderOptions.Default);
                files.Add(File.ReadAllBytes(path));
                pixels.Add(Pixels(frame));
                using var decoded = new Bitmap(path);
                Assert.Equal(frame.PixelSize, decoded.PixelSize);
                Assert.Equal(pixels[^1], Pixels(decoded));
            }
            finally { fresh.Close(); }
        }
        Assert.Equal(files[0], files[1]);
        Assert.Equal(files[0], files[2]);
        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[0], pixels[2]);
        var distinct = pixels[0].Chunk(4).Select(p => BitConverter.ToUInt32(p)).Distinct().Count();
        Assert.True(distinct > 32, "Loose whole-frame nonblank guard, not a pixel parity gate.");
        File.WriteAllText(Path.Combine(Evidence.Value, "render.json"), JsonSerializer.Serialize(new
        {
            width = 1200, height = 800, scaling = 1, repetitions = 3,
            pngBytes = files[0].Length, sha256 = Convert.ToHexString(SHA256.HashData(files[0])),
            pixelSha256 = Convert.ToHexString(SHA256.HashData(pixels[0])),
            distinctColors = distinct, byteIdentical = true, pixelFormat = "RGBA8888",
            avalonia = "12.1.6", font = "bundled Inter", renderer = "Skia / Avalonia.Headless"
        }, new JsonSerializerOptions { WriteIndented = true }));
    });

    private static async Task Run(Action<LibraryWindow> test)
    {
        _ = Evidence.Value;
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(() =>
        {
            var window = new LibraryWindow();
            try { window.Show(); Refresh(window); test(window); }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static void Refresh(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
    }

    private static void Inside(Window window, Control control)
    {
        var point = control.TranslatePoint(default, window);
        Assert.NotNull(point);
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        Assert.True(point.Value.X >= 0 && point.Value.Y >= 0);
        Assert.True(point.Value.X + control.Bounds.Width <= window.ClientSize.Width + 1);
        Assert.True(point.Value.Y + control.Bounds.Height <= window.ClientSize.Height + 1);
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        using var target = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
        using var buffer = target.Lock();
        bitmap.CopyPixels(buffer);
        var bytes = new byte[buffer.RowBytes * bitmap.PixelSize.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return bytes;
    }
}
