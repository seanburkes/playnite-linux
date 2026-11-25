using System.Diagnostics;
using Dapper;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Xunit;

namespace Playnite.Core.Tests.Database;

public sealed class SchemaTests
{
    private static readonly Guid GameId = Guid.Parse("d4226dbf-c79a-45ac-9c0e-710bf45066ac");

    [Fact]
    public async Task Fresh_and_upgraded_databases_have_identical_schema_and_upgrade_preserves_rows()
    {
        using var files = new DatabaseTestDirectory();
        await LibraryDatabase.OpenAsync(files.PathFor("fresh.sqlite"));
        using (var old = DatabaseTestDirectory.Connect(files.PathFor("upgraded.sqlite")))
        {
            await SchemaMigrator.ApplyAsync(old, 1, CancellationToken.None);
            await using var transaction = old.BeginTransaction();
            var session = new LibrarySession(old, transaction, CancellationToken.None);
            await session.Games.AddAsync(new StoredGame(new Game(GameId, "570", "Dota 2") { Playtime = ulong.MaxValue }));
            await transaction.CommitAsync();
        }
        var upgraded = await LibraryDatabase.OpenAsync(files.PathFor("upgraded.sqlite"));
        var item = await upgraded.ReadAsync(s => s.Games.GetAsync(GameId));
        Assert.Equal("Dota 2", item!.Value.Name);
        Assert.Equal(ulong.MaxValue, item.Value.Playtime);
        using var fresh = DatabaseTestDirectory.Connect(files.PathFor("fresh.sqlite"));
        using var update = DatabaseTestDirectory.Connect(files.PathFor("upgraded.sqlite"));
        Assert.Equal(await Shape(fresh), await Shape(update));
        Assert.Equal(new[] { 1, 2, 3, 4 }, (await update.QueryAsync<int>("SELECT version FROM schema_version ORDER BY version;")).ToArray());
        Assert.Equal("ok", await update.QuerySingleAsync<string>("PRAGMA integrity_check;"));
        Assert.Empty(await update.QueryAsync("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task Second_open_is_idempotent_including_version_history_and_user_data()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(GameId, "570"))); return 0; });
        string[] before;
        using (var connection = DatabaseTestDirectory.Connect(files.PathFor()))
            before = (await connection.QueryAsync<string>("SELECT version || ':' || applied_at FROM schema_version ORDER BY version;")).ToArray();
        database = await LibraryDatabase.OpenAsync(files.PathFor());
        using var after = DatabaseTestDirectory.Connect(files.PathFor());
        Assert.Equal(before, (await after.QueryAsync<string>("SELECT version || ':' || applied_at FROM schema_version ORDER BY version;")).ToArray());
        Assert.Single(await database.ReadAsync(s => s.Games.GetAllAsync()));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public async Task Unknown_or_gapped_versions_are_refused_without_changing_history(int version)
    {
        using var files = new DatabaseTestDirectory();
        await LibraryDatabase.OpenAsync(files.PathFor());
        using (var connection = DatabaseTestDirectory.Connect(files.PathFor()))
            await connection.ExecuteAsync("INSERT INTO schema_version VALUES (@Version, 'future');", new { Version = version });
        await Assert.ThrowsAsync<InvalidDataException>(() => LibraryDatabase.OpenAsync(files.PathFor()));
        using var after = DatabaseTestDirectory.Connect(files.PathFor());
        Assert.Equal(5, await after.QuerySingleAsync<int>("SELECT COUNT(*) FROM schema_version;"));
        Assert.Equal("future", await after.QuerySingleAsync<string>("SELECT applied_at FROM schema_version WHERE version=@Version;", new { Version = version }));
    }

    [Theory]
    [InlineData("game")]
    [InlineData("tag")]
    [InlineData("game_action")]
    public async Task Upgrade_refuses_v2_rows_with_non_canonical_identities(string kind)
    {
        using var files = new DatabaseTestDirectory();
        using (var connection = DatabaseTestDirectory.Connect(files.PathFor()))
        {
            await SchemaMigrator.ApplyAsync(connection, 2, CancellationToken.None);
            if (kind == "tag")
            {
                await connection.ExecuteAsync("INSERT INTO tag(Id, LegacyJson) VALUES ('ZZZZZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZZZZZZZZZ', '{}');");
            }
            else
            {
                await connection.ExecuteAsync("""
                    INSERT INTO game(Id, PluginId, Hidden, Favorite, EnableSystemHdr, IncludeLibraryPluginAction, IsInstalled,
                        OverrideInstallState, UseGlobalPreScript, UseGlobalPostScript, UseGlobalGameStartedScript, Playtime, PlayCount,
                        GenreIdsPresent, PlatformIdsPresent, PublisherIdsPresent, DeveloperIdsPresent, CategoryIdsPresent, TagIdsPresent,
                        FeatureIdsPresent, SeriesIdsPresent, AgeRatingIdsPresent, RegionIdsPresent, LinksPresent, GameActionsPresent,
                        RomsPresent, LegacyJson)
                    VALUES (@Id, '00000000-0000-0000-0000-000000000000', 0, 0, 0, 0, 0, 0, 0, 0, 0,
                        '00000000000000000000', '00000000000000000000', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '{}');
                    """, new { Id = kind == "game" ? "zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz" : GameId.ToString("D") });
                if (kind != "game")
                {
                    await connection.ExecuteAsync("""
                        INSERT INTO game_action(GameId, Position, Type, OverrideDefaultArgs, IsPlayAction, EmulatorId, TrackingMode, InitialTrackingDelay, TrackingFrequency)
                        VALUES (@Id, 0, 0, 0, 0, 'ZZZZZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZZZZZZZZZ', 0, 0, 0);
                        """, new { Id = GameId.ToString("D") });
                }
            }
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LibraryDatabase.OpenAsync(files.PathFor()));
        Assert.Contains(kind switch { "tag" => "tag.Id", "game" => "game.Id", _ => "game_action.EmulatorId" }, error.Message, StringComparison.Ordinal);
        Assert.Contains("non-canonical identity", error.Message, StringComparison.Ordinal);
        using var after = DatabaseTestDirectory.Connect(files.PathFor());
        Assert.Equal(2, await after.QuerySingleAsync<int>("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(1, await after.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {(kind == "tag" ? "tag" : kind == "game" ? "game" : "game_action")};"));
        Assert.Equal(0, await after.QuerySingleAsync<int>("SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger';"));
    }

    public static IEnumerable<object[]> CrashCheckpoints()
    {
        foreach (var version in new[] { 1, 2, 3, 4 })
            foreach (var stage in Enum.GetValues<MigrationStage>())
                yield return [version, stage.ToString()];
    }

    [Theory]
    [MemberData(nameof(CrashCheckpoints))]
    public async Task Sigkill_at_every_migration_checkpoint_is_resumable(int version, string stage)
    {
        using var files = new DatabaseTestDirectory();
        if (version > 1)
        {
            using var old = DatabaseTestDirectory.Connect(files.PathFor());
            await SchemaMigrator.ApplyAsync(old, 1, CancellationToken.None);
            await using var transaction = old.BeginTransaction();
            await new LibrarySession(old, transaction, CancellationToken.None).Games.AddAsync(new StoredGame(new Game(GameId, "570", "preserved")));
            await transaction.CommitAsync();
        }
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(SchemaTests).Assembly.Location);
        start.Environment["PLAYNITE_SCHEMA_CRASH_DATABASE"] = files.PathFor();
        start.Environment["PLAYNITE_SCHEMA_CRASH_VERSION"] = version.ToString();
        start.Environment["PLAYNITE_SCHEMA_CRASH_STAGE"] = stage;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        Assert.NotEqual(0, process.ExitCode);
        Assert.True(File.Exists(files.PathFor() + ".checkpoint"), (await stdout) + (await stderr));
        Assert.Equal($"{version}:{stage}", await File.ReadAllTextAsync(files.PathFor() + ".checkpoint"));
        var resumed = await LibraryDatabase.OpenAsync(files.PathFor());
        await LibraryDatabase.OpenAsync(files.PathFor());
        using var after = DatabaseTestDirectory.Connect(files.PathFor());
        Assert.Equal(LibraryDatabase.SchemaVersion, await after.QuerySingleAsync<int>("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal("ok", await after.QuerySingleAsync<string>("PRAGMA integrity_check;"));
        Assert.Empty(await after.QueryAsync("PRAGMA foreign_key_check;"));
        if (version > 1)
            Assert.Equal("preserved", (await resumed.ReadAsync(s => s.Games.GetAsync(GameId)))!.Value.Name);
        await LibraryDatabase.OpenAsync(files.PathFor("fresh.sqlite"));
        using var fresh = DatabaseTestDirectory.Connect(files.PathFor("fresh.sqlite"));
        Assert.Equal(await Shape(fresh), await Shape(after));
    }

    [Fact]
    public async Task Cancelled_migration_rolls_back_its_ddl_and_version_and_can_resume()
    {
        using var files = new DatabaseTestDirectory();
        using var connection = DatabaseTestDirectory.Connect(files.PathFor());
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SchemaMigrator.ApplyAsync(connection, 2, cancel.Token,
            (_, stage) => { if (stage == MigrationStage.AfterSql) cancel.Cancel(); }));
        Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM sqlite_schema WHERE name='game';"));
        await SchemaMigrator.ApplyAsync(connection, 2, CancellationToken.None);
        Assert.Equal(2, await connection.QuerySingleAsync<int>("SELECT MAX(version) FROM schema_version;"));
    }

    private static async Task<string[]> Shape(Microsoft.Data.Sqlite.SqliteConnection connection)
        => (await connection.QueryAsync<string>("SELECT type || ':' || name || ':' || sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY type, name;")).ToArray();
}
