using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Playnite.Core;
using Playnite.Core.Database;
using Playnite.Core.Library;
using Playnite.Core.Models;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Shell;
using Playnite.Platform.Sessions;
using Playnite.Steam;
using Playnite.Steam.Sync;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class LibraryExperienceSmokeTests
{
    private static readonly Guid SteamPluginId = new(SteamLibrarySyncService.SteamPluginId);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task Grid_and_list_view_toggle_switches_panels_and_preserves_selection()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-view-");
        try
        {
            var dbPath = Path.Combine(temp.FullName, "library.sqlite");
            await SeedGamesAsync(
                dbPath,
                [
                    new Game(Guid.NewGuid(), "220", "Alpha Quest") { PluginId = SteamPluginId, IsInstalled = true },
                    new Game(Guid.NewGuid(), "400", "Beta Runner") { PluginId = SteamPluginId, Favorite = true },
                    new Game(Guid.NewGuid(), "570", "Gamma Craft"),
                ],
                token);

            await WithViewAsync(dbPath, null, null, async (_, view) =>
            {
                var list = view.FindControl<ListBox>("GameList")!;
                var gridButton = view.FindControl<Button>("GridViewButton")!;
                var listButton = view.FindControl<Button>("ListViewButton")!;
                var gridPanel = view.Resources["LibraryGridPanel"];
                var listPanel = view.Resources["LibraryListPanel"];

                Assert.False(view.IsListViewMode);
                Assert.Same(gridPanel, list.ItemsPanel);
                Assert.Contains("grid-card", ((ListBoxItem)list.Items[0]!).Classes);

                list.SelectedIndex = 1;
                await view.SelectionTask;
                Assert.Equal("Beta Runner", view.FindControl<TextBlock>("DetailsName")!.Text);

                listButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(view.IsListViewMode);
                Assert.Same(listPanel, list.ItemsPanel);
                Assert.Equal(1, list.SelectedIndex);
                Assert.Equal("Beta Runner", view.FindControl<TextBlock>("DetailsName")!.Text);
                Assert.Contains("list-row", ((ListBoxItem)list.Items[1]!).Classes);
                Assert.Contains("active", listButton.Classes);
                Assert.DoesNotContain("active", gridButton.Classes);

                gridButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(view.IsListViewMode);
                Assert.Same(gridPanel, list.ItemsPanel);
                Assert.Equal(1, list.SelectedIndex);
                Assert.Contains("grid-card", ((ListBoxItem)list.Items[1]!).Classes);
                Assert.Contains("active", gridButton.Classes);
            }, token);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Details_pane_displays_rich_metadata_and_updates_when_selection_changes()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-details-");
        try
        {
            var dbPath = Path.Combine(temp.FullName, "library.sqlite");
            await SeedGamesAsync(
                dbPath,
                [
                    new Game(Guid.NewGuid(), "220", "Half-Life 2")
                    {
                        PluginId = SteamPluginId,
                        IsInstalled = true,
                        Playtime = 7500,
                        ReleaseDate = new ReleaseDate(2004, 11, 16),
                        UserScore = 96,
                        LastActivity = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc),
                        Version = "20th-anniv",
                        InstallDirectory = "/games/hl2",
                        Description = "Classic sci-fi FPS.",
                    },
                    new Game(Guid.NewGuid(), "custom-1", "Local Indie")
                    {
                        IsInstalled = false,
                        Playtime = 0,
                        Description = "Backlog title.",
                    },
                ],
                token);

            await WithViewAsync(dbPath, null, null, async (_, view) =>
            {
                var list = view.FindControl<ListBox>("GameList")!;
                Assert.Equal("Half-Life 2", view.FindControl<TextBlock>("DetailsName")!.Text);
                Assert.Equal("Installed", view.FindControl<TextBlock>("DetailsStatus")!.Text);
                Assert.Equal("Steam", view.FindControl<TextBlock>("DetailsSource")!.Text);
                Assert.Equal("2h 5m", view.FindControl<TextBlock>("DetailsPlaytime")!.Text);
                Assert.Equal("2026-10-14", view.FindControl<TextBlock>("DetailsLastPlayed")!.Text);
                Assert.Equal("2004", view.FindControl<TextBlock>("DetailsReleaseYear")!.Text);
                Assert.Equal("96/100", view.FindControl<TextBlock>("DetailsUserScore")!.Text);
                Assert.Equal("20th-anniv", view.FindControl<TextBlock>("DetailsVersion")!.Text);
                Assert.Equal("/games/hl2", view.FindControl<TextBlock>("DetailsInstallDir")!.Text);
                Assert.Equal("Classic sci-fi FPS.", view.FindControl<TextBlock>("DetailsDescription")!.Text);

                list.SelectedIndex = 1;
                await view.SelectionTask;
                Assert.Equal("Local Indie", view.FindControl<TextBlock>("DetailsName")!.Text);
                Assert.Equal("Not installed", view.FindControl<TextBlock>("DetailsStatus")!.Text);
                Assert.Equal("Library", view.FindControl<TextBlock>("DetailsSource")!.Text);
                Assert.Equal("Never played", view.FindControl<TextBlock>("DetailsPlaytime")!.Text);
                Assert.Equal("Never", view.FindControl<TextBlock>("DetailsLastPlayed")!.Text);
                Assert.Equal("Unknown", view.FindControl<TextBlock>("DetailsReleaseYear")!.Text);
                Assert.Equal("Unrated", view.FindControl<TextBlock>("DetailsUserScore")!.Text);
                Assert.Equal("—", view.FindControl<TextBlock>("DetailsVersion")!.Text);
                Assert.Equal("—", view.FindControl<TextBlock>("DetailsInstallDir")!.Text);
                Assert.Equal("Backlog title.", view.FindControl<TextBlock>("DetailsDescription")!.Text);
            }, token);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Group_by_selector_groups_games_and_clear_filters_recovers_empty_state()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-group-");
        try
        {
            var emptyDbPath = Path.Combine(temp.FullName, "empty.sqlite");
            await WithViewAsync(emptyDbPath, null, null, (model, view) =>
            {
                Assert.Equal(0, model.TotalCount);
                Assert.False(model.HasActiveFilters);
                Assert.True(view.FindControl<Border>("EmptyState")!.IsVisible);
                Assert.False(view.FindControl<Button>("ClearFiltersButton")!.IsVisible);
                return Task.CompletedTask;
            }, token);

            var dbPath = Path.Combine(temp.FullName, "library.sqlite");
            await SeedGamesAsync(
                dbPath,
                [
                    new Game(Guid.NewGuid(), "10", "Alpha")
                    {
                        IsInstalled = true,
                        ReleaseDate = new ReleaseDate(2020),
                        Playtime = 120,
                        UserScore = 92,
                    },
                    new Game(Guid.NewGuid(), "20", "Bravo")
                    {
                        IsInstalled = false,
                        ReleaseDate = new ReleaseDate(2022),
                        Playtime = 0,
                        Favorite = true,
                    },
                    new Game(Guid.NewGuid(), "30", "Charlie")
                    {
                        IsInstalled = true,
                        ReleaseDate = new ReleaseDate(2022),
                        Playtime = 7200,
                        UserScore = 75,
                    },
                ],
                token);

            await WithViewAsync(dbPath, null, null, async (model, view) =>
            {
                var groupCombo = view.FindControl<ComboBox>("GroupCombo")!;
                var searchBox = view.FindControl<TextBox>("SearchBox")!;
                var favoriteButton = view.FindControl<Button>("FavoriteFilterButton")!;
                var allButton = view.FindControl<Button>("AllFilterButton")!;
                var list = view.FindControl<ListBox>("GameList")!;
                var emptyState = view.FindControl<Border>("EmptyState")!;
                var clearFilters = view.FindControl<Button>("ClearFiltersButton")!;

                groupCombo.SelectedIndex = 1; // InstallationStatus
                await view.QueryTask;
                Assert.Equal(LibraryGroupBy.InstallationStatus, model.GroupBy);
                Assert.Equal("Installation Status", groupCombo.SelectedItem);
                var statusCards = list.Items.OfType<ListBoxItem>()
                    .Select(item => Assert.IsType<LibraryCardItem>(item.DataContext))
                    .ToArray();
                Assert.Contains(statusCards, c => c.GroupLabel == "Installed");
                Assert.Contains(statusCards, c => c.GroupLabel == "Not installed");

                groupCombo.SelectedIndex = 2; // ReleaseYear
                await view.QueryTask;
                Assert.Contains(list.Items.OfType<ListBoxItem>().Select(i => ((LibraryCardItem)i.DataContext!).GroupLabel), label => label == "2020");
                Assert.Contains(list.Items.OfType<ListBoxItem>().Select(i => ((LibraryCardItem)i.DataContext!).GroupLabel), label => label == "2022");

                groupCombo.SelectedIndex = 3; // PlayTime
                await view.QueryTask;
                Assert.Contains(list.Items.OfType<ListBoxItem>().Select(i => ((LibraryCardItem)i.DataContext!).GroupLabel), label => label == "Never played");

                groupCombo.SelectedIndex = 4; // UserScore
                await view.QueryTask;
                Assert.Contains(list.Items.OfType<ListBoxItem>().Select(i => ((LibraryCardItem)i.DataContext!).GroupLabel), label => label == "Unrated");

                groupCombo.SelectedIndex = 5; // Name
                await view.QueryTask;
                Assert.Contains(list.Items.OfType<ListBoxItem>().Select(i => ((LibraryCardItem)i.DataContext!).GroupLabel), label => label == "A");

                favoriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.QueryTask;
                Assert.Equal(LibraryFilterMode.Favorite, model.FilterMode);
                Assert.Contains("active", favoriteButton.Classes);

                searchBox.Text = "ZZZ_NO_MATCH";
                searchBox.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent, searchBox));
                await view.QueryTask;
                Assert.True(emptyState.IsVisible);
                Assert.True(clearFilters.IsVisible);

                clearFilters.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.QueryTask;
                Assert.False(emptyState.IsVisible);
                Assert.Equal(3, model.TotalCount);
                Assert.Equal(LibraryFilterMode.All, model.FilterMode);
                Assert.Equal(LibraryGroupBy.None, model.GroupBy);
                Assert.Equal(0, groupCombo.SelectedIndex);
                Assert.Equal(string.Empty, searchBox.Text);
                Assert.Contains("active", allButton.Classes);
                Assert.DoesNotContain("active", favoriteButton.Classes);
            }, token);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Artwork_loader_resolves_steam_and_explicit_covers_rejects_bad_paths_and_enforces_bounded_cache()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-art-");
        try
        {
            var dbDir = Path.Combine(temp.FullName, "profile");
            var dbPath = Path.Combine(dbDir, "library.sqlite");
            var steamRoot = Path.Combine(temp.FullName, "steam");
            var steamCoverDir = Path.Combine(steamRoot, "appcache", "librarycache", "220");
            var hashedDir = Path.Combine(steamRoot, "appcache", "librarycache", "400", "0123456789abcdef0123456789abcdef01234567");
            var outsideSubDir = Path.Combine(temp.FullName, "outside-dir", "sub");
            Directory.CreateDirectory(Path.Combine(dbDir, "files"));
            Directory.CreateDirectory(steamCoverDir);
            Directory.CreateDirectory(hashedDir);
            Directory.CreateDirectory(outsideSubDir);

            var validCoverPng = CreateDeterministicPng(120, 60, 11);
            var validHashedCoverPng = CreateDeterministicPng(90, 135, 17);
            var validHeroPng = CreateDeterministicPng(240, 100, 22);
            var explicitPng = CreateDeterministicPng(100, 100, 33);
            var outsidePng = Path.Combine(temp.FullName, "outside.png");
            var localCoverPng = Path.Combine(dbDir, "files", "local-cover.png");
            var zeroBytePng = Path.Combine(dbDir, "files", "empty.png");
            var oversizedPng = Path.Combine(dbDir, "files", "oversized.png");
            var bombPng = Path.Combine(dbDir, "files", "pixel-bomb.png");

            await File.WriteAllBytesAsync(Path.Combine(steamCoverDir, "header.jpg"), validCoverPng, token);
            await File.WriteAllBytesAsync(Path.Combine(hashedDir, "library_600x900.jpg"), validHashedCoverPng, token);
            await File.WriteAllBytesAsync(Path.Combine(hashedDir, "library_hero.jpg"), validHeroPng, token);
            await File.WriteAllBytesAsync(localCoverPng, explicitPng, token);
            await File.WriteAllBytesAsync(outsidePng, explicitPng, token);
            await File.WriteAllBytesAsync(Path.Combine(outsideSubDir, "secret.png"), explicitPng, token);
            await File.WriteAllBytesAsync(Path.Combine(dbDir, "files", "corrupt.png"), "not-an-image"u8.ToArray(), token);
            await File.WriteAllBytesAsync(zeroBytePng, [], token);
            await File.WriteAllBytesAsync(bombPng, CreateOversizedHeaderPng(100_000, 100_000), token);
            await using (var fs = new FileStream(oversizedPng, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(LibraryArtworkLoader.MaxImageFileBytes + 1);
            }

            File.CreateSymbolicLink(Path.Combine(dbDir, "files", "escape-link.png"), outsidePng);
            Directory.CreateSymbolicLink(Path.Combine(dbDir, "files", "escape-dir"), Path.Combine(temp.FullName, "outside-dir"));

            using var boundedLoader = new LibraryArtworkLoader(dbPath, [steamRoot], maxCacheEntries: 2, maxCacheBytes: 1024 * 1024);

            Assert.Null(boundedLoader.ResolveArtworkPath(
                new LibraryRow(Guid.NewGuid(), "Traversal", "1") { CoverImage = "../outside.png" }, ArtworkSlot.Cover));
            Assert.Null(boundedLoader.ResolveArtworkPath(
                new LibraryRow(Guid.NewGuid(), "Remote", "2") { CoverImage = "https://example.invalid/a.png" }, ArtworkSlot.Cover));
            Assert.Null(boundedLoader.ResolveArtworkPath(
                new LibraryRow(Guid.NewGuid(), "SymlinkEscape", "3") { CoverImage = "escape-link.png" }, ArtworkSlot.Cover));
            Assert.Null(boundedLoader.ResolveArtworkPath(
                new LibraryRow(Guid.NewGuid(), "IntermediateSymlink", "4") { CoverImage = "escape-dir/sub/secret.png" }, ArtworkSlot.Cover));
            Assert.Null(boundedLoader.ResolveArtworkPath(
                new LibraryRow(Guid.NewGuid(), "RootedOutside", "5") { CoverImage = outsidePng }, ArtworkSlot.Cover));
            Assert.Equal(localCoverPng, boundedLoader.ResolveArtworkPath(
                new LibraryRow(Guid.NewGuid(), "RootedInside", "6") { CoverImage = localCoverPng }, ArtworkSlot.Cover));
            Assert.Null(boundedLoader.ResolveArtworkPath(
                new LibraryRow(Guid.NewGuid(), "ZeroByte", "7") { CoverImage = "empty.png" }, ArtworkSlot.Cover));
            Assert.Null(boundedLoader.ResolveArtworkPath(
                new LibraryRow(Guid.NewGuid(), "Oversized", "8") { CoverImage = "oversized.png" }, ArtworkSlot.Cover));

            await SeedGamesAsync(
                dbPath,
                [
                    new Game(Guid.NewGuid(), "220", "Steam Cover Game") { PluginId = SteamPluginId, IsInstalled = true },
                    new Game(Guid.NewGuid(), "400", "Hashed Hero Game") { PluginId = SteamPluginId, IsInstalled = true },
                    new Game(Guid.NewGuid(), "local-1", "Explicit Local Game") { CoverImage = "local-cover.png" },
                ],
                token);

            LibraryCardItem[]? capturedCards = null;
            Image? capturedHero = null;
            await WithViewAsync(
                dbPath,
                sync: null,
                artworkFactory: path => new LibraryArtworkLoader(path, [steamRoot], maxCacheEntries: 2, maxCacheBytes: 1024 * 1024),
                async (_, view) =>
                {
                    await view.ArtworkTask;
                    await view.HeroArtworkTask;

                    var list = view.FindControl<ListBox>("GameList")!;
                    var items = list.Items.OfType<ListBoxItem>().ToArray();
                    Assert.Equal(3, items.Length);

                    var explicitCard = Assert.IsType<LibraryCardItem>(items[0].DataContext);
                    var hashedCard = Assert.IsType<LibraryCardItem>(items[1].DataContext);
                    var steamCard = Assert.IsType<LibraryCardItem>(items[2].DataContext);
                    capturedCards = [explicitCard, hashedCard, steamCard];
                    Assert.True(explicitCard.HasCoverBitmap);
                    Assert.True(hashedCard.HasCoverBitmap);
                    Assert.True(steamCard.HasCoverBitmap);

                    list.SelectedIndex = 1; // Hashed Hero Game
                    await view.SelectionTask;
                    await view.HeroArtworkTask;
                    capturedHero = view.FindControl<Image>("DetailsHeroImage")!;
                    Assert.True(capturedHero.IsVisible);
                    Assert.NotNull(capturedHero.Source);

                    // Verify LRU count eviction, byte-budget eviction, corrupt image, and pixel-bomb rejection.
                    Assert.NotNull(await boundedLoader.GetOrReadBytesAsync(Path.Combine(steamCoverDir, "header.jpg"), token));
                    Assert.NotNull(await boundedLoader.GetOrReadBytesAsync(Path.Combine(hashedDir, "library_hero.jpg"), token));
                    Assert.NotNull(await boundedLoader.GetOrReadBytesAsync(localCoverPng, token));
                    Assert.Equal(2, boundedLoader.CachedEntryCount);

                    using var byteBudgetLoader = new LibraryArtworkLoader(
                        dbPath,
                        [steamRoot],
                        maxCacheEntries: 10,
                        maxCacheBytes: validCoverPng.Length + explicitPng.Length - 1);
                    Assert.NotNull(await byteBudgetLoader.GetOrReadBytesAsync(Path.Combine(steamCoverDir, "header.jpg"), token));
                    Assert.NotNull(await byteBudgetLoader.GetOrReadBytesAsync(localCoverPng, token));
                    Assert.Equal(1, byteBudgetLoader.CachedEntryCount);
                    Assert.True(byteBudgetLoader.CachedBytes <= validCoverPng.Length + explicitPng.Length - 1);

                    using var corruptResult = await boundedLoader.LoadBitmapAsync(
                        new LibraryRow(Guid.NewGuid(), "Corrupt", "9") { CoverImage = "corrupt.png" },
                        ArtworkSlot.Cover,
                        200,
                        token);
                    Assert.Null(corruptResult);

                    using var bombResult = await boundedLoader.LoadBitmapAsync(
                        new LibraryRow(Guid.NewGuid(), "Bomb", "10") { CoverImage = "pixel-bomb.png" },
                        ArtworkSlot.Cover,
                        200,
                        token);
                    Assert.Null(bombResult);

                    // Unloaded/window-close must release active bitmaps deterministically on the UI thread.
                    (TopLevel.GetTopLevel(view) as Window)?.Close();
                    Dispatcher.UIThread.RunJobs();
                    Assert.All(capturedCards, card => Assert.False(card.HasCoverBitmap));
                    Assert.Null(capturedHero.Source);
                },
                token);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Sync_steam_button_reconciles_library_reloads_grid_and_handles_missing_steam_or_errors()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-sync-");
        try
        {
            var emptyDbPath = Path.Combine(temp.FullName, "empty-sync.sqlite");
            var realSteamRoot = Path.Combine(temp.FullName, "home", ".local", "share", "Steam");
            var realSteamApps = Path.Combine(realSteamRoot, "steamapps");
            Directory.CreateDirectory(realSteamApps);
            await File.WriteAllTextAsync(
                Path.Combine(realSteamApps, "libraryfolders.vdf"),
                $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{realSteamRoot}\"\n\t}}\n}}\n",
                token);
            await File.WriteAllTextAsync(
                Path.Combine(realSteamApps, "appmanifest_400.acf"),
                "\"AppState\"\n{\n\t\"appid\"\t\t\"400\"\n\t\"name\"\t\t\"Portal Real Sync\"\n\t\"installdir\"\t\t\"Portal\"\n\t\"StateFlags\"\t\t\"4\"\n}\n",
                token);

            var realRunner = new LibrarySteamSyncRunner(new TestEnvironment(Path.Combine(temp.FullName, "home")));
            await WithViewAsync(emptyDbPath, realRunner, null, async (_, view) =>
            {
                Assert.True(view.FindControl<Border>("EmptyState")!.IsVisible);
                var emptySync = view.FindControl<Button>("EmptySyncButton")!;
                emptySync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.SyncTask;

                Assert.False(view.FindControl<Border>("EmptyState")!.IsVisible);
                Assert.Single(view.FindControl<ListBox>("GameList")!.Items);
                Assert.Equal("Portal Real Sync", view.FindControl<TextBlock>("DetailsName")!.Text);
                Assert.Equal("Steam sync complete: 1 added, 0 updated, 0 unchanged.", view.FindControl<TextBlock>("SyncStatus")!.Text);
            }, token);

            var dbPath = Path.Combine(temp.FullName, "library.sqlite");
            await SeedGamesAsync(
                dbPath,
                [
                    new Game(Guid.NewGuid(), "220", "Existing Game 1") { PluginId = SteamPluginId, IsInstalled = true },
                    new Game(Guid.NewGuid(), "570", "Existing Game 2") { PluginId = SteamPluginId, IsInstalled = true },
                ],
                token);

            var pendingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sync = new FakeSteamSync(pendingGate);
            await WithViewAsync(dbPath, sync, null, async (model, view) =>
            {
                var syncButton = view.FindControl<Button>("SyncSteamButton")!;
                var emptySyncButton = view.FindControl<Button>("EmptySyncButton")!;
                var playButton = view.FindControl<Button>("PlayButton")!;
                var status = view.FindControl<TextBlock>("SyncStatus")!;
                var list = view.FindControl<ListBox>("GameList")!;
                Assert.Equal(2, list.Items.Count);
                Assert.True(playButton.IsEnabled);

                syncButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(syncButton.IsEnabled);
                Assert.False(emptySyncButton.IsEnabled);
                Assert.False(playButton.IsEnabled);
                pendingGate.SetResult();
                await view.SyncTask;

                Assert.Equal(3, list.Items.Count);
                Assert.Equal("(3)", view.FindControl<TextBlock>("LibraryCountText")!.Text);
                Assert.Equal("Steam sync complete: 1 added, 0 updated, 0 unchanged.", status.Text);
                Assert.True(syncButton.IsEnabled);
                Assert.True(playButton.IsEnabled);

                syncButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.SyncTask;
                Assert.Equal("Steam library not found on this machine.", status.Text);
                Assert.True(syncButton.IsEnabled);

                syncButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.SyncTask;
                Assert.StartsWith("Steam sync failed:", status.Text, StringComparison.Ordinal);
                Assert.True(syncButton.IsEnabled);

                // Cancelling model lifetime must cancel an in-flight sync without throwing or mutating UI.
                var cancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                sync.OverrideAsync = async ct =>
                {
                    cancelStarted.TrySetResult();
                    await neverCompletes.Task.WaitAsync(ct);
                    return new SteamSyncResult(0, 0, 0, 0, [], [], true);
                };
                syncButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await cancelStarted.Task;
                model.Dispose();
                await view.SyncTask;
                Assert.False(syncButton.IsEnabled);
            }, token);

            Assert.Equal(
                "Steam sync complete: 1 added, 2 updated, 3 unchanged, 4 removed, 1 skipped.",
                LibrarySteamSyncRunner.FormatSummary(new SteamSyncResult(1, 2, 4, 3, [new SteamSyncSkip(1, "bad")], [], true)));
            Assert.Equal(
                $"Steam sync could not reconcile library ({SteamSyncReasons.LibraryFileRejected}).",
                LibrarySteamSyncRunner.FormatSummary(new SteamSyncResult(0, 0, 0, 0, [], [new SteamSyncIssue(SteamSyncReasons.LibraryFileRejected)], false)));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static async Task WithViewAsync(
        string dbPath,
        ILibrarySteamSync? sync,
        Func<string, LibraryArtworkLoader>? artworkFactory,
        Func<LibraryViewModel, LibraryGridView, Task> check,
        CancellationToken token)
    {
        var completed = false;
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
        await session.Dispatch(() =>
        {
            var frame = new DispatcherFrame();
            var task = RunAsync();
            if (!task.IsCompleted)
            {
                using var registration = token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
                _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.UIThread.PushFrame(frame);
            }

            task.GetAwaiter().GetResult();

            async Task RunAsync()
            {
                var model = new LibraryViewModel(dbPath);
                var view = new LibraryGridView(
                    new SteamPlayRequest(new NoopOpener()),
                    fullscreenPreview: false,
                    sync ?? new FakeSteamSync(),
                    artworkFactory);
                var window = new Window { Width = 1280, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);
                    await check(model, view);
                    completed = true;
                }
                finally
                {
                    window.Close();
                    model.Dispose();
                }
            }
        }, token);
        Assert.True(completed);
    }

    private static async Task SeedGamesAsync(string dbPath, IReadOnlyList<Game> games, CancellationToken token)
    {
        var db = await LibraryDatabase.OpenAsync(dbPath, token);
        await db.WriteAsync(async session =>
        {
            foreach (var game in games)
            {
                await session.Games.AddAsync(new StoredGame(game));
            }

            return games.Count;
        }, token);
    }

    private static byte[] CreateOversizedHeaderPng(int width, int height)
    {
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;
        ihdr[9] = 2;

        using var output = new MemoryStream();
        output.Write(PngSignature);
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static byte[] CreateDeterministicPng(int width, int height, uint seed)
    {
        var rowStride = 1 + (width * 3);
        var raw = new byte[rowStride * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowStride;
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                var pixel = rowStart + 1 + (x * 3);
                raw[pixel] = (byte)((x * 7 + y * 3 + seed) & 0xFF);
                raw[pixel + 1] = (byte)((x * 5 + y * 11 + (seed >> 8)) & 0xFF);
                raw[pixel + 2] = (byte)((x * 13 + y * 2 + (seed >> 16)) & 0xFF);
            }
        }

        byte[] compressed;
        using (var zlibBuffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(zlibBuffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = zlibBuffer.ToArray();
        }

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;
        ihdr[9] = 2;

        using var output = new MemoryStream();
        output.Write(PngSignature);
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], payload.Length);
        Encoding.ASCII.GetBytes(type, header[4..8]);
        output.Write(header);
        output.Write(payload);

        var crc = Crc32.Append(0, header[4..8]);
        crc = Crc32.Append(crc, payload);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private sealed class FakeSteamSync(TaskCompletionSource? firstCallGate = null) : ILibrarySteamSync
    {
        private int calls;

        public Func<CancellationToken, Task<SteamSyncResult>>? OverrideAsync { get; set; }

        public async Task<SteamSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken)
        {
            if (OverrideAsync is not null)
            {
                return await OverrideAsync(cancellationToken);
            }

            calls++;
            if (calls == 1)
            {
                if (firstCallGate is not null)
                {
                    await firstCallGate.Task.WaitAsync(cancellationToken);
                }

                var db = await LibraryDatabase.OpenAsync(databasePath, cancellationToken);
                await db.WriteAsync(async session =>
                {
                    await session.Games.AddAsync(new StoredGame(
                        new Game(Guid.NewGuid(), "400", "Synced Portal")
                        {
                            PluginId = SteamPluginId,
                            IsInstalled = true,
                        }));
                    return true;
                }, cancellationToken);
                return new SteamSyncResult(1, 0, 0, 0, [], [], true);
            }

            if (calls == 2)
            {
                return new SteamSyncResult(0, 0, 0, 0, [], [new SteamSyncIssue(SteamSyncReasons.SteamRootMissing)], false);
            }

            throw new IOException("Simulated discovery I/O failure.");
        }
    }

    private sealed class TestEnvironment(string home) : IEnvironment
    {
        public string Home => home;
        public string CurrentDirectory => home;
        public string ProgramDirectory => home;
        public string? GetVariable(string name) => name == "HOME" ? home : null;
    }

    private sealed class NoopOpener : ISteamUriOpener
    {
        public Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
