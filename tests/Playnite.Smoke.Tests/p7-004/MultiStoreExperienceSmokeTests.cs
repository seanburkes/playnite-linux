using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using Playnite.Core;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Shell;
using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Sessions;
using Playnite.Steam;
using Playnite.Steam.Sync;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class MultiStoreExperienceSmokeTests
{
    private static readonly byte[] ValidOnePixelPng = BuildDeterministicPng(32, 32);

    [Fact]
    public async Task StoreCatalog_and_HeroicPlayRequest_validate_ids_build_uris_and_handle_opener_outcomes()
    {
        var token = TestContext.Current.CancellationToken;

        Assert.Equal("Steam", StoreCatalog.GetDisplayName(StoreCatalog.SteamPluginId));
        Assert.Equal("Epic", StoreCatalog.GetDisplayName(StoreCatalog.EpicPluginId));
        Assert.Equal("GOG", StoreCatalog.GetDisplayName(StoreCatalog.GogPluginId));
        Assert.Equal("Amazon", StoreCatalog.GetDisplayName(StoreCatalog.AmazonPluginId));
        Assert.Equal("Library", StoreCatalog.GetDisplayName(Guid.Empty));

        Assert.True(StoreCatalog.TryBuildHeroicLaunchUri(StoreCatalog.EpicPluginId, "Celeste", out var epicUri));
        Assert.Equal("heroic://launch/legendary/Celeste", epicUri);
        Assert.True(StoreCatalog.TryBuildHeroicLaunchUri(StoreCatalog.GogPluginId, "1207658924", out var gogUri));
        Assert.Equal("heroic://launch/gog/1207658924", gogUri);
        Assert.True(StoreCatalog.TryBuildHeroicLaunchUri(StoreCatalog.AmazonPluginId, "amzn1.adg.product.abc-123", out var amazonUri));
        Assert.Equal("heroic://launch/nile/amzn1.adg.product.abc-123", amazonUri);

        // Rejects non-Heroic stores and malformed/injectable identifiers.
        Assert.False(StoreCatalog.TryBuildHeroicLaunchUri(StoreCatalog.SteamPluginId, "440", out _));
        Assert.False(StoreCatalog.TryBuildHeroicLaunchUri(Guid.Empty, "Celeste", out _));
        Assert.False(StoreCatalog.TryBuildHeroicLaunchUri(StoreCatalog.EpicPluginId, string.Empty, out _));
        Assert.False(StoreCatalog.TryBuildHeroicLaunchUri(StoreCatalog.EpicPluginId, "..", out _));
        Assert.False(StoreCatalog.TryBuildHeroicLaunchUri(StoreCatalog.EpicPluginId, "../etc", out _));
        Assert.False(StoreCatalog.TryBuildHeroicLaunchUri(StoreCatalog.EpicPluginId, "game?arg=1", out _));
        Assert.False(StoreCatalog.TryBuildHeroicLaunchUri(StoreCatalog.EpicPluginId, "bad id", out _));
        Assert.False(StoreCatalog.TryBuildHeroicLaunchUri(StoreCatalog.EpicPluginId, new string('a', 129), out _));

        Assert.True(StoreCatalog.TryGetFilterPluginId(LibrarySourceFilter.Custom, out var customId));
        Assert.Equal(Guid.Empty, customId);
        Assert.False(StoreCatalog.TryGetFilterPluginId(LibrarySourceFilter.All, out _));

        var okOpener = new RecordingOpener((_, _) => Task.FromResult(0));
        var play = new HeroicPlayRequest(okOpener);
        var okMsg = await play.SendAsync(StoreCatalog.EpicPluginId, "Celeste", "Celeste", token);
        Assert.Contains("Launch requested for Celeste via Heroic", okMsg, StringComparison.Ordinal);
        Assert.Single(okOpener.Requests);
        Assert.Equal("/usr/bin/xdg-open", okOpener.Requests[0].Executable);
        Assert.Equal(["heroic://launch/legendary/Celeste"], okOpener.Requests[0].Arguments);

        var badExitPlay = new HeroicPlayRequest(new RecordingOpener((_, _) => Task.FromResult(2)));
        var badExitMsg = await badExitPlay.SendAsync(StoreCatalog.GogPluginId, "1207658924", "Witcher", token);
        Assert.Contains("did not accept the Heroic launch request", badExitMsg, StringComparison.Ordinal);

        var ioFailPlay = new HeroicPlayRequest(new RecordingOpener((_, _) => throw new IOException("missing xdg-open")));
        var ioFailMsg = await ioFailPlay.SendAsync(StoreCatalog.AmazonPluginId, "amzn-1", "Amazon Game", token);
        Assert.Contains("Could not open Heroic", ioFailMsg, StringComparison.Ordinal);

        var invalidMsg = await play.SendAsync(StoreCatalog.EpicPluginId, "../bad", "Bad", token);
        Assert.Contains("invalid or unsupported Heroic store identifier", invalidMsg, StringComparison.Ordinal);
        Assert.Single(okOpener.Requests);

        var timeoutPlay = new HeroicPlayRequest(
            new RecordingOpener(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return 0;
            }),
            TimeSpan.FromMilliseconds(20));
        var timeoutMsg = await timeoutPlay.SendAsync(StoreCatalog.EpicPluginId, "Celeste", "Celeste", token);
        Assert.Contains("was not confirmed", timeoutMsg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeroicLibrarySyncRunner_discovers_all_stores_skips_dlc_and_symlink_escapes_and_preserves_user_data()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-heroic-sync-");
        try
        {
            var xdgConfig = Path.Combine(temp.FullName, "xdg");
            var heroicRoot = Path.Combine(xdgConfig, "heroic");
            var epicImages = Path.Combine(heroicRoot, "Images");
            var outsideDir = Path.Combine(temp.FullName, "outside");
            Directory.CreateDirectory(Path.Combine(heroicRoot, "legendaryConfig", "legendary"));
            Directory.CreateDirectory(Path.Combine(heroicRoot, "store_cache"));
            Directory.CreateDirectory(Path.Combine(heroicRoot, "images-cache"));
            Directory.CreateDirectory(Path.Combine(heroicRoot, "gog_store"));
            Directory.CreateDirectory(Path.Combine(heroicRoot, "nile_config", "nile"));
            Directory.CreateDirectory(epicImages);
            Directory.CreateDirectory(outsideDir);

            var validCover = Path.Combine(epicImages, "celeste.png");
            var gogSquareCover = Path.Combine(epicImages, "hollow-square.png");
            var gogCachedCoverUrl = "https://images.example.invalid/stronghold-cover.png";
            var gogCachedCover = Path.Combine(heroicRoot, "images-cache", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gogCachedCoverUrl))).ToLowerInvariant());
            var outsideSecret = Path.Combine(outsideDir, "secret.png");
            var symlinkEscape = Path.Combine(epicImages, "escape.png");
            await File.WriteAllBytesAsync(validCover, CreateMinimalPng(), token);
            await File.WriteAllBytesAsync(gogSquareCover, CreateMinimalPng(), token);
            await File.WriteAllBytesAsync(gogCachedCover, CreateMinimalPng(), token);
            await File.WriteAllBytesAsync(outsideSecret, CreateMinimalPng(), token);
            File.CreateSymbolicLink(symlinkEscape, outsideSecret);

            // Two-hop symlinked parent tree: realAlias -> temp.FullName, and xdg_sym/heroic -> realAlias/xdg/heroic.
            var realAlias = Path.Combine(temp.FullName, "real_alias");
            var symlinkedXdg = Path.Combine(temp.FullName, "xdg_sym");
            Directory.CreateDirectory(symlinkedXdg);
            File.CreateSymbolicLink(realAlias, temp.FullName);
            File.CreateSymbolicLink(Path.Combine(symlinkedXdg, "heroic"), Path.Combine(realAlias, "xdg", "heroic"));

            var epicInstallDir = Path.Combine(temp.FullName, "Games", "Celeste");
            var gogInstallDir = Path.Combine(temp.FullName, "Games", "HollowGog");
            var strongholdInstallDir = Path.Combine(temp.FullName, "Games", "Stronghold Kingdom Simulator");
            var amazonInstallDir = Path.Combine(temp.FullName, "Games", "PrimeGame");
            Directory.CreateDirectory(epicInstallDir);
            Directory.CreateDirectory(gogInstallDir);
            Directory.CreateDirectory(strongholdInstallDir);
            Directory.CreateDirectory(amazonInstallDir);

            // Seed pre-existing Epic game, OverrideInstallState game, and imported DLC entry.
            var dbDir = Path.Combine(temp.FullName, "db");
            Directory.CreateDirectory(dbDir);
            var dbPath = Path.Combine(dbDir, "library.sqlite");
            var customAction = new GameAction
            {
                Name = "Custom Binary",
                Type = GameActionType.File,
                IsPlayAction = true,
                Path = "/usr/bin/true",
            };
            var existingEpicId = Guid.NewGuid();
            var overriddenId = Guid.NewGuid();
            var dlcId = Guid.NewGuid();
            var db = await LibraryDatabase.OpenAsync(dbPath, token);
            await db.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(existingEpicId, "Celeste", "Celeste Old")
                {
                    PluginId = StoreCatalog.EpicPluginId,
                    IsInstalled = true,
                    Favorite = true,
                    Playtime = 3600,
                    PlayCount = 4,
                    UserScore = 95,
                    GameActions = [customAction],
                }));
                await session.Games.AddAsync(new StoredGame(new Game(overriddenId, "OverrideEpic", "Manual Epic Game")
                {
                    PluginId = StoreCatalog.EpicPluginId,
                    IsInstalled = true,
                    OverrideInstallState = true,
                    InstallDirectory = "/custom/manual/path",
                }));
                await session.Games.AddAsync(new StoredGame(new Game(dlcId, "CelesteDlc", "Celeste Soundtrack")
                {
                    PluginId = StoreCatalog.EpicPluginId,
                    IsInstalled = true,
                }));
                return 0;
            }, token);

            // Write Heroic manifests for Epic, GOG, Amazon (including DLC, symlink cover escape, and invalid ID).
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "legendaryConfig", "legendary", "installed.json"),
                $$"""
                {
                  "Celeste": { "app_name": "Celeste", "title": "Celeste", "install_path": "{{EscapeJson(epicInstallDir)}}", "version": "1.4.0.0", "is_dlc": false },
                  "CelesteDlc": { "app_name": "CelesteDlc", "title": "Celeste Soundtrack", "install_path": "{{EscapeJson(epicInstallDir)}}", "is_dlc": true },
                  "bad/id": { "app_name": "bad/id", "title": "Invalid ID" }
                }
                """,
                token);
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "store_cache", "legendary_library.json"),
                $$"""
                {
                  "library": [
                    { "app_name": "Celeste", "title": "Celeste", "art_cover": "{{EscapeJson(validCover)}}", "metadata": { "description": "Climb Celeste Mountain." } },
                    { "app_name": "AlanWakeUninstalled", "title": "Alan Wake", "art_cover": "{{EscapeJson(symlinkEscape)}}" }
                  ]
                }
                """,
                token);
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "gog_store", "installed.json"),
                $$"""
                {
                  "installed": [
                    { "appName": "1450723045", "title": "Hollow Knight GOG", "install_path": "{{EscapeJson(gogInstallDir)}}", "version": "1.5.78", "is_dlc": false },
                    { "appName": "1441096928", "install_path": "{{EscapeJson(strongholdInstallDir)}}", "version": "1.0", "is_dlc": false }
                  ]
                }
                """,
                token);
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "store_cache", "gog_library.json"),
                $$"""
                {
                  "games": [
                    { "app_name": "1441096928", "art_square": "{{gogCachedCoverUrl}}" }
                  ]
                }
                """,
                token);
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "gog_store", "library.json"),
                $$"""
                {
                  "games": [
                    { "app_name": "1450723045", "title": "Hollow Knight GOG", "art_cover": "https://images.example.invalid/ignore.png", "art_square": "{{EscapeJson(gogSquareCover)}}", "extra": { "about": { "shortDescription": "Explore Hallownest." } } },
                    { "app_name": "1207658924", "title": "The Witcher GOG" }
                  ]
                }
                """,
                token);
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "nile_config", "nile", "installed.json"),
                $$"""
                [
                  { "id": "amzn1.adg.product.installed", "title": "Prime Tactics", "path": "{{EscapeJson(amazonInstallDir)}}", "version": "2.0" }
                ]
                """,
                token);
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "nile_config", "nile", "library.json"),
                """
                [
                  { "id": "amzn1.adg.product.uninstalled", "product": { "id": "amzn1.adg.product.uninstalled", "title": "Prime Vault", "productDetail": { "details": { "shortDescription": "Uninstalled Prime title." } } } }
                ]
                """,
                token);

            using var metadataClient = CreateGogNotFoundClient();
            var runner = new HeroicLibrarySyncRunner(new FakeEnvironment(temp.FullName, symlinkedXdg), gogMetadataClient: metadataClient);
            var firstSync = await runner.SyncAsync(dbPath, token);
            Assert.True(firstSync.RootFound);
            Assert.True(firstSync.Reconciled);
            Assert.Equal(6, firstSync.Added);
            Assert.Equal(1, firstSync.Updated);
            Assert.Equal(0, firstSync.Uninstalled);
            Assert.Equal(2, firstSync.Skipped);
            Assert.Empty(firstSync.Issues);

            var allAfterFirst = (await db.ReadAsync(s => s.Games.GetAllAsync(), token)).Select(s => s.Value).ToList();
            Assert.Equal(9, allAfterFirst.Count);

            var celeste = allAfterFirst.Single(g => g.GameId == "Celeste");
            Assert.Equal(existingEpicId, celeste.Id);
            Assert.Equal("Celeste", celeste.Name);
            Assert.Equal(3600UL, celeste.Playtime);
            Assert.Equal(4UL, celeste.PlayCount);
            Assert.True(celeste.Favorite);
            Assert.Equal(95, celeste.UserScore);
            Assert.Equal(Path.GetFullPath(validCover), celeste.CoverImage);
            Assert.Equal("Climb Celeste Mountain.", celeste.Description);
            Assert.NotNull(celeste.Modified);
            Assert.Single(celeste.GameActions!);
            Assert.Equal("/usr/bin/true", celeste.GameActions![0].Path);

            // OverrideInstallState game and skipped installed DLC remain installed.
            var overridden = allAfterFirst.Single(g => g.Id == overriddenId);
            Assert.True(overridden.IsInstalled);
            Assert.Equal("/custom/manual/path", overridden.InstallDirectory);
            Assert.True(allAfterFirst.Single(g => g.Id == dlcId).IsInstalled);

            var alanWake = allAfterFirst.Single(g => g.GameId == "AlanWakeUninstalled");
            Assert.False(alanWake.IsInstalled);
            Assert.Null(alanWake.CoverImage); // symlink escape outside Heroic root rejected

            var hollowGog = allAfterFirst.Single(g => g.GameId == "1450723045");
            Assert.Equal(StoreCatalog.GogPluginId, hollowGog.PluginId);
            Assert.True(hollowGog.IsInstalled);
            // Remote art_cover fell back to validated local art_square under canonical heroic root!
            Assert.Equal(gogSquareCover, hollowGog.CoverImage);
            Assert.NotNull(hollowGog.Added);

            var stronghold = allAfterFirst.Single(g => g.GameId == "1441096928");
            Assert.Equal("Stronghold Kingdom Simulator", stronghold.Name);
            Assert.Equal(Path.GetFullPath(gogCachedCover), stronghold.CoverImage);

            // Deterministic GUID assignment in a fresh DB.
            Assert.Equal(HeroicLibrarySyncRunner.GameIdFor(StoreCatalog.GogPluginId, "1450723045"), hollowGog.Id);

            // Second sync is idempotent.
            var secondSync = await runner.SyncAsync(dbPath, token);
            Assert.Equal(0, secondSync.Added);
            Assert.Equal(0, secondSync.Updated);
            Assert.Equal(7, secondSync.Unchanged);

            // Uninstall Celeste from Epic installed.json (keeping skipped DLC) -> marked uninstalled, clears InstallDirectory+Version, preserves user data.
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "legendaryConfig", "legendary", "installed.json"),
                $$"""
                {
                  "CelesteDlc": { "app_name": "CelesteDlc", "title": "Celeste Soundtrack", "install_path": "{{EscapeJson(epicInstallDir)}}", "is_dlc": true }
                }
                """,
                token);
            var thirdSync = await runner.SyncAsync(dbPath, token);
            Assert.Equal(1, thirdSync.Uninstalled);
            var celesteAfterUninstall = (await db.ReadAsync(s => s.Games.GetAsync(existingEpicId), token))!.Value;
            Assert.False(celesteAfterUninstall.IsInstalled);
            Assert.Null(celesteAfterUninstall.InstallDirectory);
            Assert.Null(celesteAfterUninstall.Version);
            Assert.Equal(3600UL, celesteAfterUninstall.Playtime);
            Assert.Single(celesteAfterUninstall.GameActions!);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HeroicLibrarySyncRunner_handles_missing_root_and_partial_manifest_failures_without_wiping_stores()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-heroic-partial-");
        try
        {
            var dbDir = Path.Combine(temp.FullName, "db");
            Directory.CreateDirectory(dbDir);
            var dbPath = Path.Combine(dbDir, "library.sqlite");
            var db = await LibraryDatabase.OpenAsync(dbPath, token);
            var preExistingGogId = Guid.NewGuid();
            await db.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(preExistingGogId, "999", "Existing GOG")
                {
                    PluginId = StoreCatalog.GogPluginId,
                    IsInstalled = true,
                }));
                return 0;
            }, token);

            // Missing config roots returns actionable non-throwing result without touching DB.
            var missingRunner = new HeroicLibrarySyncRunner(new FakeEnvironment(
                Path.Combine(temp.FullName, "no-home"),
                Path.Combine(temp.FullName, "no-xdg")));
            var missingResult = await missingRunner.SyncAsync(dbPath, token);
            Assert.False(missingResult.RootFound);
            Assert.False(missingResult.Reconciled);
            Assert.Contains(HeroicSyncIssues.RootMissing, missingResult.Issues);
            Assert.Contains("not found", HeroicLibrarySyncRunner.FormatSummary(missingResult), StringComparison.OrdinalIgnoreCase);

            // Partial failure: corrupt gog_store/installed.json must NOT mark pre-existing GOG game uninstalled,
            // while still importing valid Epic games.
            var xdgConfig = Path.Combine(temp.FullName, "xdg");
            var heroicRoot = Path.Combine(xdgConfig, "heroic");
            Directory.CreateDirectory(Path.Combine(heroicRoot, "legendaryConfig", "legendary"));
            Directory.CreateDirectory(Path.Combine(heroicRoot, "gog_store"));
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "legendaryConfig", "legendary", "installed.json"),
                """
                {
                  "EpicOne": { "app_name": "EpicOne", "title": "Epic One" }
                }
                """,
                token);
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "gog_store", "installed.json"),
                "{ not-valid-json",
                token);
            await File.WriteAllTextAsync(
                Path.Combine(heroicRoot, "gog_store", "library.json"),
                """
                {
                  "games": [
                    { "app_name": "999", "title": "Existing GOG" }
                  ]
                }
                """,
                token);

            using var partialMetadataClient = CreateGogNotFoundClient();
            var partialRunner = new HeroicLibrarySyncRunner(new FakeEnvironment(temp.FullName, xdgConfig), gogMetadataClient: partialMetadataClient);
            var partialResult = await partialRunner.SyncAsync(dbPath, token);
            Assert.True(partialResult.RootFound);
            Assert.True(partialResult.Reconciled);
            Assert.Equal(1, partialResult.Added);
            Assert.Equal(0, partialResult.Uninstalled);
            Assert.Contains(HeroicSyncIssues.GogParseError, partialResult.Issues);

            var gogStillInstalled = (await db.ReadAsync(s => s.Games.GetAsync(preExistingGogId), token))!.Value;
            Assert.True(gogStillInstalled.IsInstalled);

            // Dangling symlink installed.json + wrong-shape library.json across all stores -> Reconciled == false.
            var badXdg = Path.Combine(temp.FullName, "bad-xdg");
            var badHeroic = Path.Combine(badXdg, "heroic");
            Directory.CreateDirectory(Path.Combine(badHeroic, "legendaryConfig", "legendary"));
            Directory.CreateDirectory(Path.Combine(badHeroic, "store_cache"));
            File.CreateSymbolicLink(
                Path.Combine(badHeroic, "legendaryConfig", "legendary", "installed.json"),
                Path.Combine(temp.FullName, "nonexistent-target.json"));
            await File.WriteAllTextAsync(
                Path.Combine(badHeroic, "store_cache", "legendary_library.json"),
                "42",
                token);

            var brokenRunner = new HeroicLibrarySyncRunner(new FakeEnvironment(temp.FullName, badXdg));
            var brokenResult = await brokenRunner.SyncAsync(dbPath, token);
            Assert.True(brokenResult.RootFound);
            Assert.False(brokenResult.Reconciled);
            Assert.Contains(HeroicSyncIssues.EpicParseError, brokenResult.Issues);
            Assert.Contains("could not reconcile", HeroicLibrarySyncRunner.FormatSummary(brokenResult), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LibraryGridView_filters_by_store_displays_store_badges_syncs_heroic_and_clears_filters()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-multistore-ui-");
        try
        {
            var dbDir = Path.Combine(temp.FullName, "db");
            Directory.CreateDirectory(dbDir);
            var dbPath = Path.Combine(dbDir, "library.sqlite");
            var db = await LibraryDatabase.OpenAsync(dbPath, token);
            await db.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "custom-1", "Custom Indie")
                {
                    PluginId = Guid.Empty,
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "440", "Steam Fortress")
                {
                    PluginId = StoreCatalog.SteamPluginId,
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "EpicSlug", "Epic Odyssey")
                {
                    PluginId = StoreCatalog.EpicPluginId,
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "1207658924", "Gog Adventure")
                {
                    PluginId = StoreCatalog.GogPluginId,
                    IsInstalled = false,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "amzn1", "Amazon Prime Title")
                {
                    PluginId = StoreCatalog.AmazonPluginId,
                    IsInstalled = true,
                }));
                return 0;
            }, token);

            var fakeHeroicSync = new FakeHeroicSync(async path =>
            {
                var database = await LibraryDatabase.OpenAsync(path, token);
                await database.WriteAsync(async session =>
                {
                    await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "SyncedEpic", "Synced From Heroic")
                    {
                        PluginId = StoreCatalog.EpicPluginId,
                        IsInstalled = true,
                    }));
                    return 0;
                }, token);
                return new HeroicSyncResult(1, 0, 0, 5, 0, [], Reconciled: true, RootFound: true);
            });

            await WithHeadlessUiAsync(async () =>
            {
                var vm = new LibraryViewModel(dbPath);
                await vm.LoadAsync(token);

                var view = new LibraryGridView(
                    new SteamPlayRequest(new RecordingOpener((_, _) => Task.FromResult(0))),
                    fullscreenPreview: false,
                    new NoopSteamSync(),
                    artworkLoaderFactory: null,
                    sessionManager: null,
                    toolResolver: null,
                    heroicSync: fakeHeroicSync,
                    heroicPlay: new HeroicPlayRequest(new RecordingOpener((_, _) => Task.FromResult(0))));
                var window = new Window { Content = view, Width = 1200, Height = 800 };
                window.Show();
                try
                {
                    view.Apply(vm);
                    PumpUntil(() => view.FindControl<ListBox>("GameList")!.Items.Count == 5, token);

                    // Default SourceFilter.All preserves the exact pre-existing QueryStatus contract.
                    Assert.Equal("Search:  | Filter: All | Sort: Name | Direction: asc", vm.QueryStatus);
                    Assert.False(vm.HasActiveFilters);

                    var gameList = view.FindControl<ListBox>("GameList")!;
                    var cardsByName = gameList.Items.OfType<ListBoxItem>()
                        .Select(i => (LibraryCardItem)i.DataContext!)
                        .ToDictionary(c => c.Row.Name, c => c, StringComparer.Ordinal);
                    Assert.False(cardsByName["Custom Indie"].HasSourceBadge);
                    Assert.Equal("Library", cardsByName["Custom Indie"].SourceDisplay);
                    Assert.True(cardsByName["Steam Fortress"].HasSourceBadge);
                    Assert.Equal("Steam", cardsByName["Steam Fortress"].SourceDisplay);
                    Assert.True(cardsByName["Epic Odyssey"].HasSourceBadge);
                    Assert.Equal("Epic", cardsByName["Epic Odyssey"].SourceDisplay);
                    Assert.True(cardsByName["Gog Adventure"].HasSourceBadge);
                    Assert.Equal("GOG", cardsByName["Gog Adventure"].SourceDisplay);
                    Assert.True(cardsByName["Amazon Prime Title"].HasSourceBadge);
                    Assert.Equal("Amazon", cardsByName["Amazon Prime Title"].SourceDisplay);

                    var sourceCombo = view.FindControl<ComboBox>("SourceCombo")!;
                    // Epic (index 2)
                    sourceCombo.SelectedIndex = 2;
                    await view.QueryTask;
                    PumpUntil(() => RowNames(gameList).SequenceEqual(["Epic Odyssey"]), token);
                    Assert.True(vm.HasActiveFilters);
                    Assert.True(view.FindControl<Button>("ClearFiltersButton")!.IsVisible);
                    Assert.Contains("Source: Epic", vm.QueryStatus, StringComparison.Ordinal);

                    // GOG (index 3) + Installed filter composes FilterSettings.Library + IsInstalled.
                    sourceCombo.SelectedIndex = 3;
                    await view.QueryTask;
                    PumpUntil(() => RowNames(gameList).SequenceEqual(["Gog Adventure"]), token);
                    view.FindControl<Button>("InstalledFilterButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.QueryTask;
                    PumpUntil(() => gameList.Items.Count == 0, token);
                    Assert.True(view.FindControl<Border>("EmptyState")!.IsVisible);

                    // Clear filters restores both quick filter and SourceCombo back to All.
                    view.FindControl<Button>("ClearFiltersButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.QueryTask;
                    PumpUntil(() => gameList.Items.Count == 5, token);
                    Assert.Equal(0, sourceCombo.SelectedIndex);
                    Assert.Equal(LibrarySourceFilter.All, vm.SourceFilter);

                    // Custom (index 5) filters strictly to Guid.Empty games.
                    sourceCombo.SelectedIndex = 5;
                    await view.QueryTask;
                    PumpUntil(() => RowNames(gameList).SequenceEqual(["Custom Indie"]), token);

                    // Switch to List View with GroupBy active to exercise list-row 6-column + grid-card group/badge layout.
                    view.FindControl<Button>("ListViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(view.IsListViewMode);
                    view.FindControl<ComboBox>("GroupCombo")!.SelectedIndex = 1; // InstallationStatus
                    await view.QueryTask;
                    PumpUntil(() => ((LibraryCardItem)gameList.Items.OfType<ListBoxItem>().First().DataContext!).HasGroupLabel, token);

                    // EmptySyncHeroicButton from EmptyState runs Heroic sync, updates SyncStatus, and brings new Epic game into view.
                    view.FindControl<ComboBox>("GroupCombo")!.SelectedIndex = 0;
                    await view.QueryTask;
                    sourceCombo.SelectedIndex = 3; // GOG (uninstalled only)
                    await view.QueryTask;
                    view.FindControl<Button>("InstalledFilterButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.QueryTask;
                    PumpUntil(() => view.FindControl<Border>("EmptyState")!.IsVisible, token);

                    view.FindControl<Button>("ClearFiltersButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.QueryTask;
                    sourceCombo.SelectedIndex = 3;
                    await view.QueryTask;
                    view.FindControl<Button>("InstalledFilterButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.QueryTask;
                    view.FindControl<Button>("EmptySyncHeroicButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.SyncTask;
                    PumpUntil(() => view.FindControl<TextBlock>("SyncStatus")!.Text?.Contains("1 added", StringComparison.Ordinal) == true, token);
                    view.FindControl<Button>("ClearFiltersButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.QueryTask;
                    Assert.Equal(6, vm.TotalCount);
                }
                finally
                {
                    window.Close();
                    vm.Dispose();
                }
            }, token);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Heroic_game_launches_via_uri_and_respects_custom_file_action_override_and_artwork_containment()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-heroic-launch-");
        try
        {
            var heroicRoot = Path.Combine(temp.FullName, "heroic-root");
            var coverDir = Path.Combine(heroicRoot, "Images");
            var outsideDir = Path.Combine(temp.FullName, "outside");
            Directory.CreateDirectory(coverDir);
            Directory.CreateDirectory(outsideDir);
            var safeCover = Path.Combine(coverDir, "epic-cover.png");
            var outsideCover = Path.Combine(outsideDir, "outside.png");
            var linkCover = Path.Combine(coverDir, "link-escape.png");
            await File.WriteAllBytesAsync(safeCover, CreateMinimalPng(), token);
            await File.WriteAllBytesAsync(outsideCover, CreateMinimalPng(), token);
            File.CreateSymbolicLink(linkCover, outsideCover);

            var dbDir = Path.Combine(temp.FullName, "db");
            Directory.CreateDirectory(dbDir);
            var dbPath = Path.Combine(dbDir, "library.sqlite");
            var db = await LibraryDatabase.OpenAsync(dbPath, token);
            var epicRowId = Guid.NewGuid();
            var gogOverrideRowId = Guid.NewGuid();
            var launchCount = 0;
            await db.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(epicRowId, "AlphaEpic", "Alpha Heroic Game")
                {
                    PluginId = StoreCatalog.EpicPluginId,
                    IsInstalled = true,
                    CoverImage = safeCover,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(gogOverrideRowId, "1999", "Beta Custom GOG Game")
                {
                    PluginId = StoreCatalog.GogPluginId,
                    IsInstalled = true,
                    GameActions =
                    [
                        new GameAction
                        {
                            Name = "Native Wrapper",
                            Type = GameActionType.File,
                            IsPlayAction = true,
                            Path = "/usr/bin/true",
                        },
                    ],
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "UninstalledEpic", "Gamma Uninstalled Epic")
                {
                    PluginId = StoreCatalog.EpicPluginId,
                    IsInstalled = false,
                }));
                return 0;
            }, token);

            // Artwork loader containment against Heroic root override.
            using var artworkLoader = new LibraryArtworkLoader(dbPath, heroicRootsOverride: [heroicRoot]);
            Assert.Equal(safeCover, artworkLoader.ResolveArtworkPath(
                new LibraryRow(epicRowId, "Alpha Heroic Game", "AlphaEpic") { CoverImage = safeCover }, ArtworkSlot.Cover));
            Assert.Null(artworkLoader.ResolveArtworkPath(
                new LibraryRow(epicRowId, "Alpha Heroic Game", "AlphaEpic") { CoverImage = outsideCover }, ArtworkSlot.Cover));
            Assert.Null(artworkLoader.ResolveArtworkPath(
                new LibraryRow(epicRowId, "Alpha Heroic Game", "AlphaEpic") { CoverImage = linkCover }, ArtworkSlot.Cover));
            Assert.Null(artworkLoader.ResolveArtworkPath(
                new LibraryRow(epicRowId, "Alpha Heroic Game", "AlphaEpic") { CoverImage = "https://cdn.example/cover.png" }, ArtworkSlot.Cover));

            var opener = new RecordingOpener((_, _) =>
            {
                Interlocked.Increment(ref launchCount);
                return Task.FromResult(0);
            });
            var identity = new ProcessIdentity(88123, 99999, 88123);
            await using var sleepInhibition = new SessionSleepInhibition(new FakeSleepInhibitionService());
            await using var sessionManager = new DesktopGameSessionManager(
                new FakeCompletedProcessLauncher(identity),
                new FakeExitedProcessObserver(identity),
                sessionDelay: null,
                TimeProvider.System,
                sleepInhibition,
                new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(10), ExitConfirmationSamples = 1 });

            await WithHeadlessUiAsync(async () =>
            {
                var vm = new LibraryViewModel(dbPath);
                await vm.LoadAsync(token);

                var view = new LibraryGridView(
                    new SteamPlayRequest(opener),
                    fullscreenPreview: false,
                    new NoopSteamSync(),
                    path => new LibraryArtworkLoader(path, heroicRootsOverride: [heroicRoot]),
                    sessionManager,
                    toolResolver: null,
                    heroicSync: null,
                    heroicPlay: new HeroicPlayRequest(opener));
                var window = new Window { Content = view, Width = 1200, Height = 800 };
                window.Show();
                try
                {
                    view.Apply(vm);
                    PumpUntil(() => view.FindControl<ListBox>("GameList")!.Items.Count == 3, token);

                    // Row 0 is Alpha Heroic Game (Heroic Epic URI launch) with real Heroic cover loaded on UI card & hero pane.
                    await view.ArtworkTask;
                    await view.HeroArtworkTask;
                    var alphaCard = (LibraryCardItem)((ListBoxItem)view.FindControl<ListBox>("GameList")!.Items[0]!).DataContext!;
                    Assert.True(alphaCard.HasCoverBitmap);
                    Assert.NotNull(view.FindControl<Image>("DetailsHeroImage")!.Source);
                    Assert.Equal("Heroic (Epic)", view.FindControl<TextBlock>("DetailsLaunchTarget")!.Text);
                    Assert.True(view.FindControl<Button>("PlayButton")!.IsEnabled);

                    view.FindControl<Button>("PlayButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.PlayTask;
                    Assert.Equal(1, Volatile.Read(ref launchCount));
                    Assert.Equal(["heroic://launch/legendary/AlphaEpic"], opener.Requests[0].Arguments);
                    Assert.Contains("Heroic", view.FindControl<TextBlock>("PlayStatus")!.Text, StringComparison.Ordinal);

                    // Select Row 1 (GOG game with custom File PlayAction -> must use tracked session, NOT Heroic URI).
                    view.SelectRowForSmoke(1);
                    await view.SelectionTask;
                    PumpUntil(() => view.FindControl<TextBlock>("DetailsName")!.Text == "Beta Custom GOG Game", token);
                    Assert.Equal("/usr/bin/true", view.FindControl<TextBlock>("DetailsLaunchTarget")!.Text);

                    view.FindControl<Button>("PlayButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.PlayTask;
                    PumpUntil(() => vm.Rows.Single(r => r.Id == gogOverrideRowId).PlayCount == 1, token);
                    Assert.Equal(1, Volatile.Read(ref launchCount)); // URI opener NOT called again

                    // Row 2 is uninstalled Epic game -> cannot launch, target shows Not configured.
                    view.SelectRowForSmoke(2);
                    await view.SelectionTask;
                    PumpUntil(() => view.FindControl<TextBlock>("DetailsName")!.Text == "Gamma Uninstalled Epic", token);
                    Assert.False(view.FindControl<Button>("PlayButton")!.IsEnabled);
                    Assert.Equal("Not configured", view.FindControl<TextBlock>("DetailsLaunchTarget")!.Text);
                }
                finally
                {
                    window.Close();
                    vm.Dispose();
                }
            }, token);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HeroicGog_sync_uses_public_metadata_when_heroic_catalog_and_art_cache_are_missing()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-gog-public-fallback-");
        try
        {
            var xdgConfig = Path.Combine(temp.FullName, "xdg");
            var heroicRoot = Path.Combine(xdgConfig, "heroic");
            var gogStore = Path.Combine(heroicRoot, "gog_store");
            var installDirectory = Path.Combine(temp.FullName, "Games", "1441096928");
            var databasePath = Path.Combine(temp.FullName, "db", "library.sqlite");
            Directory.CreateDirectory(gogStore);
            Directory.CreateDirectory(installDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(gogStore, "installed.json"),
                $$"""
                {
                  "installed": [
                    { "appName": "1441096928", "install_path": "{{EscapeJson(installDirectory)}}", "version": "1.0" }
                  ]
                }
                """,
                token);

            var apiCalls = 0;
            var imageCalls = 0;
            using var metadataClient = new HttpClient(new RecordingHttpMessageHandler((request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.RequestUri?.Host == "api.gog.com")
                {
                    Interlocked.Increment(ref apiCalls);
                    var content = new StringContent(
                        "{\"id\":1441096928,\"title\":\"D&D Stronghold: Kingdom Simulator\",\"images\":{\"background\":\"//images-2.gog-statics.com/stronghold.png\"}}",
                        Encoding.UTF8,
                        "application/json");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                }

                Interlocked.Increment(ref imageCalls);
                var imageContent = new ByteArrayContent(CreateMinimalPng());
                imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = imageContent });
            }));

            var runner = new HeroicLibrarySyncRunner(new FakeEnvironment(temp.FullName, xdgConfig), gogMetadataClient: metadataClient);
            var first = await runner.SyncAsync(databasePath, token);
            Assert.True(first.Reconciled);
            Assert.Equal(1, first.Added);
            Assert.Empty(first.Issues);
            Assert.Equal(1, apiCalls);
            Assert.Equal(1, imageCalls);

            var database = await LibraryDatabase.OpenAsync(databasePath, token);
            var game = (await database.ReadAsync(session => session.Games.GetAllAsync()).ConfigureAwait(false))
                .Select(stored => stored.Value)
                .Single();
            Assert.Equal("D&D Stronghold: Kingdom Simulator", game.Name);
            Assert.True(game.IsInstalled);
            Assert.NotNull(game.CoverImage);
            Assert.True(File.Exists(game.CoverImage));
            Assert.StartsWith(Path.Combine(Path.GetDirectoryName(databasePath)!, "files", "gog-artwork"), game.CoverImage, StringComparison.Ordinal);

            var second = await runner.SyncAsync(databasePath, token);
            Assert.Equal(0, second.Updated);
            Assert.Equal(1, second.Unchanged);
            Assert.Equal(1, apiCalls);
            Assert.Equal(1, imageCalls);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HeroicGog_public_metadata_fallback_observes_cancellation()
    {
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-gog-cancel-");
        try
        {
            var xdgConfig = Path.Combine(temp.FullName, "xdg");
            var gogStore = Path.Combine(xdgConfig, "heroic", "gog_store");
            Directory.CreateDirectory(gogStore);
            await File.WriteAllTextAsync(
                Path.Combine(gogStore, "installed.json"),
                "{\"installed\":[{\"appName\":\"1441096928\",\"install_path\":\"/games/Stronghold\"}]}");
            using var metadataClient = new HttpClient(new RecordingHttpMessageHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            var runner = new HeroicLibrarySyncRunner(new FakeEnvironment(temp.FullName, xdgConfig), gogMetadataClient: metadataClient);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await runner.SyncAsync(Path.Combine(temp.FullName, "db", "library.sqlite"), cancellation.Token));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HeroicGog_public_title_survives_artwork_failure()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-gog-art-failure-");
        try
        {
            var xdgConfig = Path.Combine(temp.FullName, "xdg");
            var gogStore = Path.Combine(xdgConfig, "heroic", "gog_store");
            Directory.CreateDirectory(gogStore);
            await File.WriteAllTextAsync(
                Path.Combine(gogStore, "installed.json"),
                "{\"installed\":[{\"appName\":\"1441096928\",\"install_path\":\"/games/Stronghold\"}]}",
                token);
            using var metadataClient = new HttpClient(new RecordingHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri?.Host == "api.gog.com")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"id\":1441096928,\"title\":\"D&D Stronghold: Kingdom Simulator\",\"images\":{\"background\":\"//images-2.gog-statics.com/stronghold.png\"}}",
                            Encoding.UTF8,
                            "application/json"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }));
            var runner = new HeroicLibrarySyncRunner(new FakeEnvironment(temp.FullName, xdgConfig), gogMetadataClient: metadataClient);

            var result = await runner.SyncAsync(Path.Combine(temp.FullName, "db", "library.sqlite"), token);
            Assert.True(result.Reconciled);
            Assert.Contains(HeroicSyncIssues.GogMetadataFallbackError, result.Issues);
            var database = await LibraryDatabase.OpenAsync(Path.Combine(temp.FullName, "db", "library.sqlite"), token);
            var game = (await database.ReadAsync(session => session.Games.GetAllAsync()).ConfigureAwait(false))
                .Select(stored => stored.Value)
                .Single();
            Assert.Equal("D&D Stronghold: Kingdom Simulator", game.Name);
            Assert.Null(game.CoverImage);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HeroicGog_artwork_cache_rejects_external_directory_symlinks()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-gog-symlink-");
        try
        {
            var xdgConfig = Path.Combine(temp.FullName, "xdg");
            var gogStore = Path.Combine(xdgConfig, "heroic", "gog_store");
            var databaseDirectory = Path.Combine(temp.FullName, "db");
            var outsideDirectory = Path.Combine(temp.FullName, "outside");
            Directory.CreateDirectory(gogStore);
            Directory.CreateDirectory(databaseDirectory);
            Directory.CreateDirectory(outsideDirectory);
            Directory.CreateSymbolicLink(Path.Combine(databaseDirectory, "files"), outsideDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(gogStore, "installed.json"),
                "{\"installed\":[{\"appName\":\"1441096928\",\"install_path\":\"/games/Stronghold\"}]}",
                token);
            using var metadataClient = new HttpClient(new RecordingHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri?.Host == "api.gog.com")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"id\":1441096928,\"title\":\"D&D Stronghold: Kingdom Simulator\",\"images\":{\"background\":\"//images-2.gog-statics.com/stronghold.png\"}}",
                            Encoding.UTF8,
                            "application/json"),
                    });
                }

                var imageContent = new ByteArrayContent(CreateMinimalPng());
                imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = imageContent });
            }));
            var runner = new HeroicLibrarySyncRunner(new FakeEnvironment(temp.FullName, xdgConfig), gogMetadataClient: metadataClient);

            var result = await runner.SyncAsync(Path.Combine(databaseDirectory, "library.sqlite"), token);
            Assert.True(result.Reconciled);
            Assert.False(Directory.Exists(Path.Combine(outsideDirectory, "gog-artwork")));
            var database = await LibraryDatabase.OpenAsync(Path.Combine(databaseDirectory, "library.sqlite"), token);
            var game = (await database.ReadAsync(session => session.Games.GetAllAsync()).ConfigureAwait(false))
                .Select(stored => stored.Value)
                .Single();
            Assert.Equal("D&D Stronghold: Kingdom Simulator", game.Name);
            Assert.Null(game.CoverImage);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static HttpClient CreateGogNotFoundClient() => new(new RecordingHttpMessageHandler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private static async Task WithHeadlessUiAsync(Func<Task> action, CancellationToken token)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
        await session.Dispatch(() =>
        {
            var frame = new DispatcherFrame();
            var task = action();
            if (!task.IsCompleted)
            {
                using var registration = token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
                _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.UIThread.PushFrame(frame);
            }

            task.GetAwaiter().GetResult();
        }, token);
    }

    private static string[] RowNames(ListBox list) =>
        list.Items.OfType<ListBoxItem>()
            .Select(item => ((item.Content as TextBlock)?.Text) ?? string.Empty)
            .ToArray();

    private static void PumpUntil(Func<bool> condition, CancellationToken token, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Timed out waiting for UI condition in headless test.");
            }

            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private static string EscapeJson(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static byte[] CreateMinimalPng() => ValidOnePixelPng;

    private static byte[] BuildDeterministicPng(int width, int height)
    {
        var rowStride = 1 + (width * 3);
        var raw = new byte[rowStride * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowStride;
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                var p = rowStart + 1 + (x * 3);
                raw[p] = (byte)(x * 7 + y * 3);
                raw[p + 1] = (byte)(x * 5 + y * 11);
                raw[p + 2] = (byte)(x * 13 + y * 2);
            }
        }

        byte[] compressed;
        using (var zlibBuf = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(zlibBuf, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = zlibBuf.ToArray();
        }

        Span<byte> ihdr = stackalloc byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;
        ihdr[9] = 2;

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(output, "IHDR", ihdr);
        WritePngChunk(output, "IDAT", compressed);
        WritePngChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[..4], payload.Length);
        System.Text.Encoding.ASCII.GetBytes(type, header[4..8]);
        output.Write(header);
        output.Write(payload);

        var crc = UpdateCrc32(0xFFFFFFFFu, header[4..8]);
        crc = UpdateCrc32(crc, payload) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc;
    }

    private sealed class FakeEnvironment(string home, string xdgConfigHome) : IEnvironment
    {
        public string Home => home;
        public string CurrentDirectory => home;
        public string ProgramDirectory => home;
        public string? GetVariable(string name) => name switch
        {
            "HOME" => home,
            "XDG_CONFIG_HOME" => xdgConfigHome,
            _ => null,
        };
    }

    private sealed class RecordingOpener(Func<LaunchRequest, CancellationToken, Task<int>> handler) : ISteamUriOpener
    {
        public List<LaunchRequest> Requests { get; } = [];

        public Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return handler(request, cancellationToken);
        }
    }

    private sealed class FakeHeroicSync(Func<string, Task<HeroicSyncResult>> handler) : ILibraryHeroicSync
    {
        public Task<HeroicSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken) =>
            handler(databasePath);
    }

    private sealed class NoopSteamSync : ILibrarySteamSync
    {
        public Task<SteamSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken) =>
            Task.FromResult(new SteamSyncResult(0, 0, 0, 0, [], [], true));
    }

    private sealed class FakeCompletedProcessLauncher(ProcessIdentity identity) : IProcessLauncher
    {
        public Task<IGameProcess> StartAsync(LaunchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IGameProcess>(new FakeProcess(identity, DateTimeOffset.UtcNow));

        public Task<bool> IsRunningAsync(IGameProcess process, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task StopAsync(IGameProcess process, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(IGameProcess process, IReadOnlyCollection<ProcessIdentity> trackedProcesses, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class FakeProcess(ProcessIdentity identity, DateTimeOffset startedAt) : IGameProcess
        {
            public ProcessIdentity Identity { get; } = identity;
            public DateTimeOffset StartedAt { get; } = startedAt;
        }
    }

    private sealed class FakeExitedProcessObserver(ProcessIdentity identity) : IProcessObserver
    {
        private int calls;
        public Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
        {
            // First poll sees running process; subsequent poll sees natural exit so session completes cleanly.
            var count = Interlocked.Increment(ref calls);
            if (count == 1)
            {
                return Task.FromResult(new ProcessSnapshot([new ProcessEntry(identity, 1, true)], IsComplete: true));
            }

            return Task.FromResult(new ProcessSnapshot([], IsComplete: true));
        }
    }

    private sealed class FakeSleepInhibitionService : ISleepInhibitionService
    {
        public Task<SleepInhibitionLease> InhibitAsync(SleepInhibitionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(SleepInhibitionLease.Held(
                DesktopCapability.Available(DesktopFacility.SleepInhibition, "fake-inhibit", "test lease"),
                new FakeHandle()));

        private sealed class FakeHandle : ISleepInhibitorHandle
        {
            private bool released;
            public string Mechanism => "fake-inhibit";
            public bool IsHeld => !released;
            public ValueTask DisposeAsync()
            {
                released = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
