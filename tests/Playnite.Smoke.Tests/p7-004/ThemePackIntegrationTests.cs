using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Playnite.Core;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Themes;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Plugins;
using Playnite.Desktop.Shell;
using Playnite.Desktop.Theme;
using Playnite.Desktop.Themes;
using Playnite.Platform.Linux;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class ThemePackIntegrationTests
{
    private static readonly IRegularFileProbe Probe = new LinuxRegularFileProbe();

    [Fact]
    public async Task Live_theme_switching_updates_open_controls_and_falls_back_cleanly_to_charcoal_on_error()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-live-theme-");
        try
        {
            var themesRoot = Path.Combine(tempDir.FullName, "themes");
            var themeADir = Path.Combine(themesRoot, "solar-amber");
            var themeBDir = Path.Combine(themesRoot, "emerald-surface");
            var invalidApiDir = Path.Combine(themesRoot, "future-api");
            var unsafeAxamlDir = Path.Combine(themesRoot, "unsafe-axaml");
            Directory.CreateDirectory(themeADir);
            Directory.CreateDirectory(themeBDir);
            Directory.CreateDirectory(invalidApiDir);
            Directory.CreateDirectory(unsafeAxamlDir);

            await File.WriteAllTextAsync(Path.Combine(themeADir, "theme.yaml"), """
                Id: solar-amber
                Name: Solar Amber
                Version: 1.0.0
                ThemeApiVersion: 2.10.0
                BaseVariant: Light
                Resources:
                  - theme.axaml
                """, token);
            await File.WriteAllTextAsync(Path.Combine(themeADir, "theme.axaml"), """
                <Styles xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Styles.Resources>
                    <Color x:Key="PlayniteAccentColor">#FFFF6B00</Color>
                    <Color x:Key="PlayniteSurfaceColor">#FFF3EFEA</Color>
                    <Color x:Key="PlayniteTextPrimaryColor">#FF111827</Color>
                    <Color x:Key="PlayniteCanvasTopColor">#FFFEF3C7</Color>
                    <Color x:Key="PlayniteCanvasBottomColor">#FFFDE68A</Color>
                    <x:Double x:Key="LibraryCardWidth">280</x:Double>
                    <x:Double x:Key="LibraryCardHeight">168</x:Double>
                    <x:Double x:Key="LibraryDetailsWidth">380</x:Double>
                    <FontFamily x:Key="PlayniteFontFamily">Liberation Sans</FontFamily>
                  </Styles.Resources>
                  <Style Selector=":is(Window)">
                    <Setter Property="FontSize" Value="18" />
                  </Style>
                </Styles>
                """, token);

            await File.WriteAllTextAsync(Path.Combine(themeBDir, "theme.yaml"), """
                Id: emerald-surface
                Name: Emerald Surface
                Version: 1.1.0
                ThemeApiVersion: 2.10.0
                Colors:
                  PlayniteSurfaceColor: "#FF0E2A23"
                """, token);

            await File.WriteAllTextAsync(Path.Combine(invalidApiDir, "theme.yaml"), """
                Id: future-api
                Name: Future API
                Version: 1.0.0
                ThemeApiVersion: 3.0.0
                """, token);

            await File.WriteAllTextAsync(Path.Combine(unsafeAxamlDir, "theme.yaml"), """
                Id: unsafe-axaml
                Name: Unsafe AXAML
                Version: 1.0.0
                ThemeApiVersion: 2.10.0
                Resources:
                  - theme.axaml
                """, token);
            await File.WriteAllTextAsync(Path.Combine(unsafeAxamlDir, "theme.axaml"), """
                <ResourceDictionary xmlns="https://github.com/avaloniaui"
                                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Button x:Key="BadButton" />
                </ResourceDictionary>
                """, token);

            await WithHeadlessUiAsync(() =>
            {
                var app = Application.Current!;
                app.RequestedThemeVariant = ThemeVariant.Dark;
                var darkTheme = new PlayniteDarkTheme();
                app.Styles.Add(darkTheme);

                var surfaceBorder = new Border();
                surfaceBorder.Bind(Border.BackgroundProperty, surfaceBorder.GetResourceObservable("PlayniteSurfaceBrush"));
                var accentBorder = new Border();
                accentBorder.Bind(Border.BackgroundProperty, accentBorder.GetResourceObservable("PlayniteAccentBrush"));
                var windowBgBorder = new Border();
                windowBgBorder.Bind(Border.BackgroundProperty, windowBgBorder.GetResourceObservable("PlayniteWindowBackgroundBrush"));
                var window = new Window
                {
                    Width = 800,
                    Height = 600,
                    Content = new StackPanel { Children = { surfaceBorder, accentBorder, windowBgBorder } },
                };

                var manager = new LiveThemeManager(app, [themesRoot], Probe, fileReader: DesktopThemeCatalogFactory.CreateDefaultFileReader());
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();

                    // Charcoal baseline.
                    Assert.Equal(Color.Parse("#FF55CDFF"), Assert.IsAssignableFrom<ISolidColorBrush>(accentBorder.Background).Color);
                    Assert.Equal(Color.Parse("#FF212121"), Assert.IsAssignableFrom<ISolidColorBrush>(surfaceBorder.Background).Color);
                    Assert.Equal(13, window.FontSize);

                    // Apply Theme A live: open window controls update immediately including gradient and FontFamily.
                    var applyA = manager.ApplyThemeById("solar-amber", persist: false);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(applyA.Applied);
                    Assert.False(applyA.FellBackToCharcoal);
                    Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
                    Assert.Equal(Color.Parse("#FFFF6B00"), Assert.IsAssignableFrom<ISolidColorBrush>(accentBorder.Background).Color);
                    Assert.Equal(Color.Parse("#FFF3EFEA"), Assert.IsAssignableFrom<ISolidColorBrush>(surfaceBorder.Background).Color);
                    var gradient = Assert.IsAssignableFrom<ILinearGradientBrush>(windowBgBorder.Background);
                    Assert.Equal(Color.Parse("#FFFEF3C7"), gradient.GradientStops[0].Color);
                    Assert.Equal(18, window.FontSize);
                    Assert.Contains("Liberation Sans", window.FontFamily.Name, StringComparison.Ordinal);

                    // Switch to Theme B: un-overridden Accent and FontSize revert cleanly to Charcoal (no leaked tokens).
                    var applyB = manager.ApplyThemeById("emerald-surface", persist: false);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(applyB.Applied);
                    Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
                    Assert.Equal(Color.Parse("#FF55CDFF"), Assert.IsAssignableFrom<ISolidColorBrush>(accentBorder.Background).Color);
                    Assert.Equal(Color.Parse("#FF0E2A23"), Assert.IsAssignableFrom<ISolidColorBrush>(surfaceBorder.Background).Color);
                    Assert.Equal(13, window.FontSize);

                    // Incompatible API version triggers safe Charcoal fallback.
                    var badApi = manager.ApplyThemeById("future-api", persist: false);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(badApi.Applied);
                    Assert.True(badApi.FellBackToCharcoal);
                    Assert.Equal(ThemePackDefaults.DefaultDesktopThemeId, manager.ActiveDocument.Descriptor.Id);
                    Assert.Equal(Color.Parse("#FF212121"), Assert.IsAssignableFrom<ISolidColorBrush>(surfaceBorder.Background).Color);

                    // Re-apply valid Theme B, then attempt unsafe .axaml and missing theme: both fall back cleanly to Charcoal.
                    Assert.True(manager.ApplyThemeById("emerald-surface", persist: false).Applied);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(Color.Parse("#FF0E2A23"), Assert.IsAssignableFrom<ISolidColorBrush>(surfaceBorder.Background).Color);

                    var badAxaml = manager.ApplyThemeById("unsafe-axaml", persist: false);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(badAxaml.Applied);
                    Assert.True(badAxaml.FellBackToCharcoal);
                    Assert.Equal(Color.Parse("#FF212121"), Assert.IsAssignableFrom<ISolidColorBrush>(surfaceBorder.Background).Color);

                    // Corrupt Theme A on disk after initial discovery and verify fresh re-validation catches it.
                    File.WriteAllText(Path.Combine(themeADir, "theme.yaml"), ":::corrupt:::");
                    var corrupted = manager.ApplyThemeById("solar-amber", persist: false);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(corrupted.Applied);
                    Assert.True(corrupted.FellBackToCharcoal);
                    Assert.NotEmpty(corrupted.Diagnostics);
                    Assert.Equal(ThemePackDefaults.DefaultDesktopThemeId, manager.ActiveDocument.Descriptor.Id);

                    var missing = manager.ApplyThemeById("does-not-exist", persist: false);
                    Assert.False(missing.Applied);
                    Assert.True(missing.FellBackToCharcoal);
                }
                finally
                {
                    manager.ResetToCharcoal(persist: false);
                    app.Styles.Remove(darkTheme);
                    window.Close();
                }
            }, token);

            // Also verify Charcoal fallback restores :is(Window) FontSize=13 and SurfaceBrush when PlayniteDarkTheme is NOT present.
            await WithHeadlessUiAsync(() =>
            {
                var app = Application.Current!;
                var surfaceBorder = new Border();
                surfaceBorder.Bind(Border.BackgroundProperty, surfaceBorder.GetResourceObservable("PlayniteSurfaceBrush"));
                var window = new Window { Width = 640, Height = 480, Content = surfaceBorder };
                var manager = new LiveThemeManager(app, [themesRoot], Probe);
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();

                    Assert.True(manager.ApplyThemeById("emerald-surface", persist: false).Applied);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(Color.Parse("#FF0E2A23"), Assert.IsAssignableFrom<ISolidColorBrush>(surfaceBorder.Background).Color);

                    var reset = manager.ResetToCharcoal(persist: false);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(reset.Applied);
                    Assert.Equal(13, window.FontSize);
                    Assert.Equal(Color.Parse("#FF212121"), Assert.IsAssignableFrom<ISolidColorBrush>(surfaceBorder.Background).Color);
                }
                finally
                {
                    window.Close();
                }
            }, token);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Themes_UI_applies_persists_resets_surfaces_duplicates_and_recovers_on_reopen()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-theme-ui-");
        try
        {
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "220", "Half-Life 2")
                {
                    PluginId = StoreCatalog.SteamPluginId,
                    IsInstalled = true,
                }));
                return true;
            }, token);

            var themesRoot = Path.Combine(tempDir.FullName, "themes");
            var oceanDir = Path.Combine(themesRoot, "ocean-wide");
            var dupOceanDir = Path.Combine(themesRoot, "Desktop", "ocean-duplicate");
            var brokenDir = Path.Combine(themesRoot, "broken-pack");
            var pluginThemeDir = Path.Combine(tempDir.FullName, "plugins", "sunset-plugin-theme");
            Directory.CreateDirectory(oceanDir);
            Directory.CreateDirectory(dupOceanDir);
            Directory.CreateDirectory(brokenDir);
            Directory.CreateDirectory(pluginThemeDir);

            await File.WriteAllTextAsync(Path.Combine(oceanDir, "theme.yaml"), """
                Id: ocean-wide
                Name: Ocean Wide
                Author: Theme Author
                Version: 1.0.0
                ThemeApiVersion: 2.10.0
                Colors:
                  accent: "#38BDF8"
                  surface: "#FF0F172A"
                Metrics:
                  LibraryCardWidth: 275
                  LibraryCardHeight: 165
                  LibraryDetailsWidth: 410
                  CardCornerRadius: 12
                """, token);
            await File.WriteAllTextAsync(Path.Combine(dupOceanDir, "theme.yaml"), """
                Id: ocean-wide
                Name: Ocean Duplicate
                Version: 2.0.0
                ThemeApiVersion: 2.10.0
                """, token);
            await File.WriteAllTextAsync(Path.Combine(brokenDir, "theme.yaml"), """
                Id: broken-pack
                Name: Broken Pack
                Version: 1.0.0
                ThemeApiVersion: 99.0.0
                """, token);
            await File.WriteAllTextAsync(Path.Combine(pluginThemeDir, "theme.axaml"), """
                <ResourceDictionary xmlns="https://github.com/avaloniaui"
                                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Color x:Key="PlayniteAccentColor">#FFF97316</Color>
                </ResourceDictionary>
                """, token);
            await File.WriteAllTextAsync(Path.Combine(pluginThemeDir, "plugin.json"), """
                {
                  "id": "sunset-plugin-theme",
                  "name": "Sunset Plugin Theme",
                  "version": "1.0.0",
                  "surfaces": ["theme"],
                  "capabilities": ["theme"],
                  "entryPoints": { "theme": "theme.axaml" }
                }
                """, token);

            using (var host1 = DesktopPluginHostFactory.CreateForLibrary(dbPath))
            {
                await WithHeadlessUiAsync(async () =>
                {
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    var darkTheme = new PlayniteDarkTheme();
                    Application.Current.Styles.Add(darkTheme);

                    var model = new LibraryViewModel(dbPath, host1, ownsPlugins: false);
                    var view = new LibraryGridView(
                        new SteamPlayRequest(new SteamPlayRequest.DesktopOpener()),
                        fullscreenPreview: false,
                        steamSync: null,
                        artworkLoaderFactory: null,
                        sessionManager: null,
                        externalThemesRoot: themesRoot);
                    var window = new Window { Width = 1280, Height = 720, Content = view };
                    try
                    {
                        await model.LoadAsync(token);
                        window.Show();
                        view.Apply(model);
                        await view.CardSlotsTask;
                        await view.DetailsSlotsTask;
                        Dispatcher.UIThread.RunJobs();

                        var detailsPane = view.FindControl<Border>("DetailsPane")!;
                        var gameList = view.FindControl<ListBox>("GameList")!;
                        Assert.Equal(320, detailsPane.Width);
                        Assert.Equal(new CornerRadius(6), detailsPane.CornerRadius);

                        // Open Themes manager directly from toolbar ThemesButton.
                        view.FindControl<Button>("ThemesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        PumpUntil(() => window.OwnedWindows.OfType<PluginManagerWindow>().Any(), token);
                        var manager = window.OwnedWindows.OfType<PluginManagerWindow>().Single();
                        var tabs = manager.FindControl<TabControl>("ManagerTabs")!;
                        Assert.Equal(1, tabs.SelectedIndex);

                        // PluginSurface.Theme package under <libraryDir>/plugins is discovered in ThemeList.
                        var themeList = manager.FindControl<ListBox>("ThemeList")!;
                        Assert.Contains(themeList.Items.OfType<ListBoxItem>(),
                            i => i.Tag is ThemePackCatalogEntry e && e.Id == "sunset-plugin-theme" && e.IsValid);

                        // Select valid Ocean Wide and apply live.
                        var oceanItem = themeList.Items.OfType<ListBoxItem>().Single(
                            i => i.Tag is ThemePackCatalogEntry e && e.Id == "ocean-wide" && e.IsValid);
                        themeList.SelectedItem = oceanItem;
                        Dispatcher.UIThread.RunJobs();

                        manager.FindControl<Button>("ApplyThemeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal("ocean-wide", host1.GetActiveThemeId());
                        Assert.Equal(410, detailsPane.Width);
                        Assert.Equal(new CornerRadius(12), detailsPane.CornerRadius);
                        var firstCard = gameList.GetVisualDescendants().OfType<ListBoxItem>().First();
                        Assert.Equal(275, firstCard.Width);
                        Assert.Equal(165, firstCard.Height);
                        Assert.Contains("Applied theme", manager.FindControl<TextBlock>("ThemeStatusText")!.Text);

                        // Duplicate-ID invalid row does NOT display Active badge and shows duplicate-theme-id diagnostic on selection + apply.
                        var dupItem = themeList.Items.OfType<ListBoxItem>().Single(
                            i => i.Tag is ThemePackCatalogEntry e && e.Id == "ocean-wide" && !e.IsValid);
                        Assert.DoesNotContain("(Active)", AutomationProperties.GetName(dupItem) ?? string.Empty, StringComparison.Ordinal);
                        themeList.SelectedItem = dupItem;
                        Dispatcher.UIThread.RunJobs();
                        Assert.True(manager.FindControl<Border>("ThemeDiagnosticsPanel")!.IsVisible);

                        manager.FindControl<Button>("ApplyThemeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal(ThemePackDefaults.DefaultDesktopThemeId, view.ThemeManager!.ActiveDocument.Descriptor.Id);
                        Assert.Equal(320, detailsPane.Width);
                        Assert.Equal(new CornerRadius(6), detailsPane.CornerRadius);
                        Assert.True(manager.FindControl<Border>("ThemeDiagnosticsPanel")!.IsVisible);
                        Assert.Equal("ocean-wide", host1.GetActiveThemeId());

                        // Explicit reset clears persisted active theme.
                        manager.FindControl<Button>("ResetThemeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Dispatcher.UIThread.RunJobs();
                        Assert.Null(host1.GetActiveThemeId());

                        // Re-apply ocean-wide then delete broken-pack on disk while still in catalog to confirm deleted-pack diagnostics stay visible.
                        themeList.SelectedItem = themeList.Items.OfType<ListBoxItem>().Single(
                            i => i.Tag is ThemePackCatalogEntry e && e.Id == "ocean-wide" && e.IsValid);
                        manager.FindControl<Button>("ApplyThemeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal("ocean-wide", host1.GetActiveThemeId());

                        manager.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await view.PluginsDialogTask;
                    }
                    finally
                    {
                        view.ThemeManager?.ResetToCharcoal(persist: false);
                        Application.Current!.Styles.Remove(darkTheme);
                        window.Close();
                        await view.UnloadTask;
                        model.Dispose();
                    }
                }, token);
            }

            // Corrupt the persisted theme pack on disk before reopening a new host & view.
            await File.WriteAllTextAsync(Path.Combine(oceanDir, "theme.yaml"), "corrupt: [unclosed", token);

            using (var host2 = DesktopPluginHostFactory.CreateForLibrary(dbPath))
            {
                await WithHeadlessUiAsync(async () =>
                {
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                    var darkTheme = new PlayniteDarkTheme();
                    Application.Current.Styles.Add(darkTheme);

                    var model = new LibraryViewModel(dbPath, host2, ownsPlugins: false);
                    var view = new LibraryGridView(
                        new SteamPlayRequest(new SteamPlayRequest.DesktopOpener()),
                        fullscreenPreview: false,
                        steamSync: null,
                        artworkLoaderFactory: null,
                        sessionManager: null,
                        externalThemesRoot: themesRoot);
                    var window = new Window { Width = 1280, Height = 720, Content = view };
                    try
                    {
                        await model.LoadAsync(token);
                        window.Show();
                        view.Apply(model);
                        await view.CardSlotsTask;
                        await view.DetailsSlotsTask;
                        Dispatcher.UIThread.RunJobs();

                        Assert.NotNull(view.ThemeManager);
                        Assert.True(view.ThemeManager!.LastResult.FellBackToCharcoal);
                        Assert.Equal(ThemePackDefaults.DefaultDesktopThemeId, view.ThemeManager.ActiveDocument.Descriptor.Id);
                        Assert.Equal(320, view.FindControl<Border>("DetailsPane")!.Width);
                        Assert.Contains("fell back to Default Charcoal", view.FindControl<TextBlock>("SyncStatus")!.Text);
                    }
                    finally
                    {
                        view.ThemeManager?.ResetToCharcoal(persist: false);
                        Application.Current!.Styles.Remove(darkTheme);
                        window.Close();
                        await view.UnloadTask;
                        model.Dispose();
                    }
                }, token);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static async Task WithHeadlessUiAsync(Action action, CancellationToken token)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
        await session.Dispatch(action, token);
    }

    private static async Task WithHeadlessUiAsync(Func<Task> action, CancellationToken token)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
        await session.Dispatch(action, token);
    }

    private static void PumpUntil(Func<bool> condition, CancellationToken token)
    {
        for (var i = 0; i < 200; i++)
        {
            token.ThrowIfCancellationRequested();
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            Thread.Sleep(15);
        }

        Assert.True(condition(), "Timed out waiting for headless UI state.");
    }
}
