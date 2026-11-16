using System.Diagnostics;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Xunit;

namespace Playnite.Core.Tests.Database;

/// <summary>The contract the importer host is allowed to depend on.</summary>
public sealed class DatabaseContractTests
{
    private static readonly Guid Id = Guid.Parse("87654321-abcd-4abc-9def-123456789abc");
    private static readonly Guid MetadataId = Guid.Parse("12345678-abcd-4abc-9def-123456789abc");
    private static readonly Guid EmulatorId = Guid.Parse("0f0f0f0f-1111-4222-8333-444455556666");

    [Fact]
    public void Raw_sqlite_connection_is_not_reachable_through_the_public_surface()
    {
        foreach (var type in new[] { typeof(LibraryDatabase), typeof(LibrarySession), typeof(GameRepository), typeof(MetadataRepository<Tag>) })
        {
            Assert.DoesNotContain(type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static), ExposesConnection);
        }

        // The repositories are in this assembly; make sure no non-public property or method hands
        // them a connection they could use to skip the transaction either.
        foreach (var type in new[] { typeof(LibrarySession), typeof(GameRepository), typeof(MetadataRepository<Tag>) })
        {
            Assert.DoesNotContain(
                type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(member => member is PropertyInfo or MethodInfo),
                ExposesConnection);
        }
    }

    private static bool ExposesConnection(MemberInfo member)
    {
        var type = member switch
        {
            PropertyInfo property => property.PropertyType,
            MethodInfo method => method.ReturnType,
            _ => null,
        };
        return type != null && (type == typeof(SqliteConnection)
            || (type.IsGenericType && type.GetGenericArguments().Contains(typeof(SqliteConnection))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nested_write_fails_fast_instead_of_waiting_on_its_own_writer(bool sameInstance)
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var other = sameInstance ? database : await LibraryDatabase.OpenAsync(files.PathFor());
        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => database.WriteAsync(async s =>
        {
            await other.WriteAsync(async inner =>
            {
                await inner.Games.AddAsync(new StoredGame(new Game(Id, "570")));
                return 0;
            });
            return 0;
        }));
        stopwatch.Stop();
        Assert.Contains("WriteAsync", error.Message, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"nesting took {stopwatch.Elapsed}; it must not wait for the 5s busy timeout");
        Assert.Empty(await database.ReadAsync(s => s.Games.GetAllAsync()));
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Id, "570"))); return 0; });
        Assert.Single(await database.ReadAsync(s => s.Games.GetAllAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nested_write_is_refused_for_a_different_spelling_of_the_same_file(bool symbolicLink)
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var aliasPath = symbolicLink
            ? files.PathFor("alias.sqlite")
            : Path.Combine(files.DirectoryPath, ".", "library.sqlite");
        if (symbolicLink) File.CreateSymbolicLink(aliasPath, files.PathFor());
        var alias = await LibraryDatabase.OpenAsync(aliasPath);
        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => database.WriteAsync(async s =>
        {
            await alias.WriteAsync(async inner =>
            {
                await inner.Games.AddAsync(new StoredGame(new Game(Id, "570")));
                return 0;
            });
            return 0;
        }));
        stopwatch.Stop();
        Assert.Contains("WriteAsync", error.Message, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"nesting through {aliasPath} took {stopwatch.Elapsed}");
        Assert.Empty(await database.ReadAsync(s => s.Games.GetAllAsync()));
    }

    [Fact]
    public async Task Background_write_started_inside_a_write_callback_fails_fast()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await database.WriteAsync(async s =>
        {
            var background = Task.Run(() => database.WriteAsync(async inner =>
            {
                await inner.Games.AddAsync(new StoredGame(new Game(Id, "570")));
                return 0;
            }));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => background);
            Assert.Contains("WriteAsync", error.Message, StringComparison.Ordinal);
            return 0;
        });
        Assert.Empty(await database.ReadAsync(s => s.Games.GetAllAsync()));
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Id, "570"))); return 0; });
    }

    [Fact]
    public async Task Write_inside_read_is_allowed_and_the_outer_snapshot_stays_stable()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Id, "570", "before"))); return 0; });
        await database.ReadAsync(async snapshot =>
        {
            Assert.Equal("before", (await snapshot.Games.GetAsync(Id))!.Value.Name);
            await database.WriteAsync(async writer =>
            {
                var item = (await writer.Games.GetAsync(Id))!;
                item.Value.Name = "after";
                await writer.Games.UpdateAsync(item);
                return 0;
            });
            Assert.Equal("before", (await snapshot.Games.GetAsync(Id))!.Value.Name);
            return 0;
        });
        Assert.Equal("after", (await database.ReadAsync(s => s.Games.GetAsync(Id)))!.Value.Name);
    }

    [Fact]
    public async Task Read_inside_write_is_allowed_and_sees_only_committed_state()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await database.WriteAsync(async s =>
        {
            await s.Games.AddAsync(new StoredGame(new Game(Id, "570")));
            Assert.Null(await database.ReadAsync(r => r.Games.GetAsync(Id)));
            return 0;
        });
        Assert.NotNull(await database.ReadAsync(s => s.Games.GetAsync(Id)));
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("genre")]
    [InlineData("company")]
    [InlineData("feature")]
    [InlineData("series")]
    [InlineData("category")]
    [InlineData("source")]
    [InlineData("age_rating")]
    [InlineData("region")]
    [InlineData("completion_status")]
    [InlineData("platform")]
    public async Task Metadata_identities_must_be_canonical_lowercase_identities(string table)
    {
        using var files = new DatabaseTestDirectory();
        await LibraryDatabase.OpenAsync(files.PathFor());
        using var connection = DatabaseTestDirectory.Connect(files.PathFor());
        var canonical = MetadataId.ToString("D");
        await connection.ExecuteAsync($"INSERT INTO {table}(Id, LegacyJson) VALUES (@Id, '{{}}');", new { Id = canonical });
        foreach (var invalid in new[] { canonical.ToUpperInvariant(), "not-a-guid", "zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz" })
        {
            var insert = await Assert.ThrowsAsync<SqliteException>(() =>
                connection.ExecuteAsync($"INSERT INTO {table}(Id, LegacyJson) VALUES (@Id, '{{}}');", new { Id = invalid }));
            Assert.Contains("canonical lowercase 36-character identity", insert.Message, StringComparison.Ordinal);
            var update = await Assert.ThrowsAsync<SqliteException>(() =>
                connection.ExecuteAsync($"UPDATE {table} SET Id=@Id WHERE Id=@Canonical;", new { Id = invalid, Canonical = canonical }));
            Assert.Contains("canonical lowercase 36-character identity", update.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Emulator_identities_must_be_canonical_and_dangling_ones_are_reported()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var unknown = Guid.Parse("99999999-8888-4777-8666-555544443333");
        var game = new Game(Id, "570")
        {
            GameActions =
            [
                new GameAction { Name = "known", EmulatorId = EmulatorId },
                new GameAction { Name = "unknown", EmulatorId = unknown },
                new GameAction { Name = "none" },
            ],
        };
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(game)); return 0; });
        var dangling = await database.ReadAsync(s => s.Games.FindDanglingEmulatorReferencesAsync([EmulatorId]));
        var reference = Assert.Single(dangling);
        Assert.Equal(Id, reference.GameId);
        Assert.Equal(1, reference.Position);
        Assert.Equal(unknown, reference.EmulatorId);
        Assert.Empty(await database.ReadAsync(s => s.Games.FindDanglingEmulatorReferencesAsync([EmulatorId, unknown])));

        using var connection = DatabaseTestDirectory.Connect(files.PathFor());
        foreach (var invalid in new[] { EmulatorId.ToString("D").ToUpperInvariant(), "not-a-guid", "zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz" })
        {
            var update = await Assert.ThrowsAsync<SqliteException>(() =>
                connection.ExecuteAsync("UPDATE game_action SET EmulatorId=@Id WHERE Position=0;", new { Id = invalid }));
            Assert.Contains("game_action.EmulatorId must be a canonical lowercase 36-character identity", update.Message, StringComparison.Ordinal);
            var insert = await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
                INSERT INTO game_action(GameId, Position, Type, OverrideDefaultArgs, IsPlayAction, EmulatorId, TrackingMode, InitialTrackingDelay, TrackingFrequency)
                VALUES (@GameId, 9, 0, 0, 0, @Id, 0, 0, 0);
                """, new { GameId = Id.ToString("D"), Id = invalid }));
            Assert.Contains("game_action.EmulatorId must be a canonical lowercase 36-character identity", insert.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Game_identity_must_be_canonical()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Id, "570"))); return 0; });
        using var connection = DatabaseTestDirectory.Connect(files.PathFor());
        foreach (var invalid in new[] { Id.ToString("D").ToUpperInvariant(), "zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz" })
        {
            var update = await Assert.ThrowsAsync<SqliteException>(() =>
                connection.ExecuteAsync("UPDATE game SET Id=@Id;", new { Id = invalid }));
            Assert.Contains("game.Id must be a canonical lowercase 36-character identity", update.Message, StringComparison.Ordinal);
            var insert = await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
                INSERT INTO game(Id, PluginId, Hidden, Favorite, EnableSystemHdr, IncludeLibraryPluginAction, IsInstalled,
                    OverrideInstallState, UseGlobalPreScript, UseGlobalPostScript, UseGlobalGameStartedScript, Playtime, PlayCount,
                    GenreIdsPresent, PlatformIdsPresent, PublisherIdsPresent, DeveloperIdsPresent, CategoryIdsPresent, TagIdsPresent,
                    FeatureIdsPresent, SeriesIdsPresent, AgeRatingIdsPresent, RegionIdsPresent, LinksPresent, GameActionsPresent,
                    RomsPresent, LegacyJson)
                VALUES (@Id, '00000000-0000-0000-0000-000000000000', 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    '00000000000000000000', '00000000000000000000', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '{}');
                """, new { Id = invalid }));
            Assert.Contains("game.Id must be a canonical lowercase 36-character identity", insert.Message, StringComparison.Ordinal);
        }
        Assert.Equal("570", (await database.ReadAsync(s => s.Games.GetAsync(Id)))!.Value.GameId);
    }

    [Fact]
    public async Task Nullable_provider_game_id_round_trips_through_the_stored_column()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Id, null!) { Name = "no provider id" })); return 0; });
        using (var connection = DatabaseTestDirectory.Connect(files.PathFor()))
            Assert.Null(await connection.QuerySingleAsync<string?>("SELECT GameId FROM game WHERE Id=@Id;", new { Id = Id.ToString("D") }));
        var read = (await database.ReadAsync(s => s.Games.GetAsync(Id)))!;
        Assert.Null(read.Value.GameId);
        await database.WriteAsync(async s => { await s.Games.UpdateAsync(read); return 0; });
        Assert.Null((await database.ReadAsync(s => s.Games.GetAsync(Id)))!.Value.GameId);
    }
}
