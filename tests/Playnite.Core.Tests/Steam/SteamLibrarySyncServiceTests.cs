using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Serialization;
using Playnite.Core.Tests.Database;
using Playnite.Steam;
using Playnite.Steam.Sync;
using Xunit;

namespace Playnite.Core.Tests.Steam;

public sealed class SteamLibrarySyncServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Steam = new(SteamLibrarySyncService.SteamPluginId);
    private const string Library = "/tmp/steam-library";

    [Fact]
    public async Task First_sync_adds_each_app_and_a_repeat_changes_nothing()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var host = new FakeDiscovery(
            Discovery(
                App(10, "Alpha", "Alpha", SteamAppState.Installed, 100),
                App(20, "Beta", "Beta", SteamAppState.UpdatePending, 200)));

        var first = await Service(host).SyncAsync(database, TestContext.Current.CancellationToken);

        Assert.Equal(2, first.Added);
        Assert.Equal(0, first.Updated);
        Assert.Equal(0, first.Removed);
        Assert.Equal(0, first.Unchanged);
        Assert.Equal(0, first.SkippedCount);
        Assert.Equal(2, first.Changes);
        Assert.True(first.Reconciled);

        var added = (await ReadAll(database)).OrderBy(game => uint.Parse(game.Value.GameId!)).ToArray();
        Assert.Equal(["10", "20"], added.Select(game => game.Value.GameId));
        Assert.All(added, game => Assert.Equal(Steam, game.Value.PluginId));
        Assert.All(added, game => Assert.Equal(Now, game.Value.Added));
        Assert.All(added, game => Assert.Equal(Now, game.Value.Modified));
        Assert.All(added, game => Assert.True(game.Value.IsInstalled));
        Assert.Equal([100UL, 200UL], added.Select(game => game.Value.InstallSize!.Value));
        Assert.Equal(
            [InstallPath("Alpha"), InstallPath("Beta")],
            added.Select(game => game.Value.InstallDirectory));
        Assert.Equal(["Alpha", "Beta"], added.Select(game => game.Value.Name));

        var serialized = added.Select(game => JsonSerialization.ToJson(game.Value)).ToArray();
        var second = await Service(host).SyncAsync(database, TestContext.Current.CancellationToken);

        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Removed);
        Assert.Equal(2, second.Unchanged);
        Assert.Equal(0, second.SkippedCount);
        Assert.Equal(0, second.Changes);
        Assert.Equal(serialized, (await ReadAll(database))
            .OrderBy(game => uint.Parse(game.Value.GameId!))
            .Select(game => JsonSerialization.ToJson(game.Value)));
    }

    [Fact]
    public async Task Existing_Steam_game_is_matched_by_app_id_and_user_edits_survive_an_update()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var added = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var existing = new Game(id, "10", "User name")
        {
            PluginId = Steam,
            InstallDirectory = "/old",
            InstallSize = 5,
            IsInstalled = true,
            SortingName = "custom sort",
            Favorite = true,
            Hidden = true,
            Notes = "hand written",
            Description = "<p>description</p>",
            Playtime = 1234,
            PlayCount = 7,
            Added = added,
            Links = [new Link("wiki", "https://example.invalid")],
        };
        await database.WriteAsync(async session => { await session.Games.AddAsync(new StoredGame(existing)); return 0; });

        var result = await Service(new FakeDiscovery(
            Discovery(App(10, "Steam name", "Steam name", SteamAppState.Installed, 99))))
            .SyncAsync(database, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Single(await ReadAll(database));
        var stored = (await ReadAll(database))[0];
        Assert.Equal(id, stored.Value.Id);
        Assert.Equal("Steam name", stored.Value.Name);
        Assert.Equal(InstallPath("Steam name"), stored.Value.InstallDirectory);
        Assert.Equal(99UL, stored.Value.InstallSize);
        Assert.True(stored.Value.IsInstalled);
        Assert.Equal(added, stored.Value.Added);
        Assert.Equal(Now, stored.Value.Modified);

        Assert.Equal("custom sort", stored.Value.SortingName);
        Assert.True(stored.Value.Favorite);
        Assert.True(stored.Value.Hidden);
        Assert.Equal("hand written", stored.Value.Notes);
        Assert.Equal("<p>description</p>", stored.Value.Description);
        Assert.Equal(1234UL, stored.Value.Playtime);
        Assert.Equal(7UL, stored.Value.PlayCount);
        Assert.Equal("https://example.invalid", Assert.Single(stored.Value.Links).Url);
    }

    [Fact]
    public async Task A_removed_app_is_marked_not_installed_and_never_deleted()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var host = new FakeDiscovery(Discovery(
            App(10, "Alpha", "Alpha", SteamAppState.Installed, 100),
            App(20, "Beta", "Beta", SteamAppState.Installed, 200)));
        var service = Service(host);
        await service.SyncAsync(database, TestContext.Current.CancellationToken);

        await database.WriteAsync(async session =>
        {
            var beta = (await session.Games.GetAllAsync()).Single(game => game.Value.GameId == "20");
            beta.Value.Favorite = true;
            beta.Value.Notes = "keep me";
            beta.Value.Playtime = 55;
            await session.Games.UpdateAsync(beta);
            return 0;
        });

        host.Result = Discovery(App(10, "Alpha", "Alpha", SteamAppState.Installed, 100));
        var removed = await service.SyncAsync(database, TestContext.Current.CancellationToken);

        Assert.Equal(0, removed.Added);
        Assert.Equal(0, removed.Updated);
        Assert.Equal(1, removed.Removed);
        Assert.Equal(1, removed.Unchanged);
        Assert.Equal(1, removed.Changes);

        var games = await ReadAll(database);
        Assert.Equal(2, games.Count);
        var beta = games.Single(game => game.Value.GameId == "20");
        Assert.False(beta.Value.IsInstalled);
        Assert.True(beta.Value.Favorite);
        Assert.Equal("keep me", beta.Value.Notes);
        Assert.Equal(55UL, beta.Value.Playtime);
        Assert.Equal(InstallPath("Beta"), beta.Value.InstallDirectory);

        var repeat = await service.SyncAsync(database, TestContext.Current.CancellationToken);
        Assert.Equal(0, repeat.Removed);
        Assert.Equal(0, repeat.Changes);
        Assert.False((await ReadAll(database)).Single(game => game.Value.GameId == "20").Value.IsInstalled);
    }

    [Fact]
    public async Task Reappearing_app_is_installed_again_without_duplicating_the_record()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var host = new FakeDiscovery(Discovery(App(10, "Alpha", "Alpha", SteamAppState.Installed, 100)));
        var service = Service(host);
        await service.SyncAsync(database, TestContext.Current.CancellationToken);
        host.Result = Discovery();
        await service.SyncAsync(database, TestContext.Current.CancellationToken);
        Assert.False((await ReadAll(database))[0].Value.IsInstalled);

        host.Result = Discovery(App(10, "Alpha", "Alpha", SteamAppState.Installed, 100));
        var again = await service.SyncAsync(database, TestContext.Current.CancellationToken);

        Assert.Equal(0, again.Added);
        Assert.Equal(1, again.Updated);
        Assert.Equal(0, again.Removed);
        Assert.True(Assert.Single(await ReadAll(database)).Value.IsInstalled);
    }

    [Fact]
    public async Task Unreconcilable_or_unavailable_discovery_never_marks_a_game_uninstalled()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        await database.WriteAsync(async session =>
        {
            await session.Games.AddAsync(new StoredGame(new Game(id, "10", "Alpha")
            {
                PluginId = Steam,
                IsInstalled = true,
                InstallDirectory = InstallPath("Alpha"),
            }));
            return 0;
        });

        var noRoot = await Service(new FakeDiscovery(SteamSyncDiscovery.Empty(false))).SyncAsync(database);
        Assert.Equal(0, noRoot.Removed);
        Assert.True(Assert.Single(await ReadAll(database)).Value.IsInstalled);

        var unavailable = new SteamSyncDiscovery(
            [],
            [new(10, SteamSyncReasons.LibraryMissing)],
            [],
            true);
        var missingLibrary = await Service(new FakeDiscovery(unavailable)).SyncAsync(database);
        Assert.Equal(0, missingLibrary.Removed);
        Assert.True(Assert.Single(await ReadAll(database)).Value.IsInstalled);
    }

    [Fact]
    public async Task OverrideInstallState_is_respected_for_both_provider_state_and_removal()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        await database.WriteAsync(async session =>
        {
            await session.Games.AddAsync(new StoredGame(new Game(id, "10", "Alpha")
            {
                PluginId = Steam,
                IsInstalled = true,
                OverrideInstallState = true,
                InstallDirectory = InstallPath("Alpha"),
                InstallSize = 100,
            }));
            return 0;
        });

        var broken = await Service(new FakeDiscovery(
            Discovery(App(10, "Alpha", "Alpha", SteamAppState.Broken, 100)))).SyncAsync(database);
        Assert.Equal(0, broken.Updated);
        Assert.Equal(1, broken.Unchanged);
        Assert.True(Assert.Single(await ReadAll(database)).Value.IsInstalled);

        var absent = await Service(new FakeDiscovery(Discovery())).SyncAsync(database);
        Assert.Equal(0, absent.Removed);
        Assert.True(Assert.Single(await ReadAll(database)).Value.IsInstalled);
    }

    [Fact]
    public async Task Missing_install_size_does_not_clear_a_stored_size()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var id = Guid.Parse("44444444-4444-4444-4444-444444444444");
        await database.WriteAsync(async session =>
        {
            await session.Games.AddAsync(new StoredGame(new Game(id, "10", "Alpha")
            {
                PluginId = Steam,
                IsInstalled = true,
                InstallDirectory = InstallPath("Alpha"),
                InstallSize = 100,
            }));
            return 0;
        });

        var result = await Service(new FakeDiscovery(
            Discovery(App(10, "Alpha", "Alpha", SteamAppState.Installed, null)))).SyncAsync(database);

        Assert.Equal(0, result.Updated);
        Assert.Equal(100UL, Assert.Single(await ReadAll(database)).Value.InstallSize);
    }

    [Fact]
    public async Task Every_skipped_app_is_reported_with_its_id_and_reason()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var host = new FakeDiscovery(new SteamSyncDiscovery(
            [App(10, "Alpha", "Alpha", SteamAppState.Installed, 100)],
            [new(20, SteamSyncReasons.ManifestMissing), new(30, SteamSyncReasons.ManifestRejected)],
            [new("library-parse-issue", null, "libraryfolders.vdf")],
            true));

        var result = await Service(host).SyncAsync(database);

        Assert.Equal(1, result.Added);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal([20u, 30u], result.Skipped.Select(skip => skip.AppId));
        Assert.Equal(
            [SteamSyncReasons.ManifestMissing, SteamSyncReasons.ManifestRejected],
            result.Skipped.Select(skip => skip.Reason));
        Assert.Equal("library-parse-issue", Assert.Single(result.Issues).Reason);
        Assert.Equal("10", Assert.Single(await ReadAll(database)).Value.GameId);
    }

    [Fact]
    public async Task Duplicate_library_records_are_reported_and_the_extra_record_is_untouched()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var first = new Game(Guid.Parse("50000000-0000-0000-0000-000000000001"), "10", "First")
        {
            PluginId = Steam,
            IsInstalled = true,
            InstallDirectory = InstallPath("First"),
        };
        var second = new Game(Guid.Parse("50000000-0000-0000-0000-000000000002"), "10", "Second")
        {
            PluginId = Steam,
            IsInstalled = true,
            InstallDirectory = InstallPath("Second"),
        };
        await database.WriteAsync(async session =>
        {
            await session.Games.AddAsync(new StoredGame(first));
            await session.Games.AddAsync(new StoredGame(second));
            return 0;
        });

        var result = await Service(new FakeDiscovery(
            Discovery(App(10, "Steam name", "Steam name", SteamAppState.Installed, 100)))).SyncAsync(database);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(new SteamSyncSkip(10, SteamSyncReasons.DuplicateLibraryRecord), Assert.Single(result.Skipped));
        var games = await ReadAll(database);
        Assert.Equal(2, games.Count);
        Assert.Equal("Steam name", games[0].Value.Name);
        Assert.Equal("Second", games[1].Value.Name);
    }

    [Fact]
    public async Task Deterministic_identity_conflict_is_reported_instead_of_overwriting_another_game()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var foreign = new Game(SteamLibrarySyncService.GameIdFor(10), "custom", "Not Steam");
        await database.WriteAsync(async session => { await session.Games.AddAsync(new StoredGame(foreign)); return 0; });

        var result = await Service(new FakeDiscovery(
            Discovery(App(10, "Alpha", "Alpha", SteamAppState.Installed, 100)))).SyncAsync(database);

        Assert.Equal(0, result.Added);
        Assert.Equal(new SteamSyncSkip(10, SteamSyncReasons.IdentityConflict), Assert.Single(result.Skipped));
        var stored = Assert.Single(await ReadAll(database));
        Assert.Equal(Guid.Empty, stored.Value.PluginId);
        Assert.Equal("Not Steam", stored.Value.Name);
    }

    [Fact]
    public async Task Pre_cancelled_sync_writes_nothing()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var host = new FakeDiscovery(Discovery(App(10, "Alpha", "Alpha", SteamAppState.Installed, 100)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(host).SyncAsync(database, cancellation.Token));

        Assert.Empty(await ReadAll(database));
    }

    [Fact]
    public void Game_identity_is_stable_and_distinct_per_app()
    {
        Assert.Equal(SteamLibrarySyncService.GameIdFor(10), SteamLibrarySyncService.GameIdFor(10));
        Assert.NotEqual(SteamLibrarySyncService.GameIdFor(10), SteamLibrarySyncService.GameIdFor(11));
        var text = SteamLibrarySyncService.GameIdFor(10).ToString("D");
        Assert.Equal('5', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    private static SteamLibrarySyncService Service(ISteamSyncDiscovery discovery) =>
        new(discovery, new FixedTimeProvider());

    private static SteamSyncDiscovery Discovery(params SteamApp[] apps) => new(apps, [], [], true);

    private static SteamApp App(uint appId, string name, string installDir, SteamAppState state, ulong? size) =>
        new(appId, name, installDir, Library, state, false, size, null, 4);

    private static string InstallPath(string installDir) => $"{Library}/steamapps/common/{installDir}";

    private static Task<IReadOnlyList<StoredGame>> ReadAll(LibraryDatabase database) =>
        database.ReadAsync(session => session.Games.GetAllAsync());

    private sealed class FakeDiscovery(SteamSyncDiscovery result) : ISteamSyncDiscovery
    {
        public SteamSyncDiscovery Result { get; set; } = result;

        public SteamSyncDiscovery Discover(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }
}
