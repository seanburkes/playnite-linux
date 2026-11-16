using System.Collections.ObjectModel;
using Dapper;
using Microsoft.Data.Sqlite;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Serialization;
using Playnite.Core.Tests.Serialization;
using Xunit;
using GamePlatform = Playnite.Core.Models.Platform;

namespace Playnite.Core.Tests.Database;

public sealed class RepositoryTests
{
    private static readonly Guid Id = Guid.Parse("87654321-abcd-4abc-9def-123456789abc");
    private static readonly Guid MetadataId = Guid.Parse("12345678-abcd-4abc-9def-123456789abc");
    private const string Unknown = "{ \"unmappedWindowsPath\": \"C:\\\\Games\\\\日本語\", \"future\": {\"n\":18446744073709551615} }";

    [Fact]
    public void Explicit_sql_mapping_covers_every_persistent_writable_game_property()
    {
        var transient = new[] { "IsInstalling", "IsUninstalling", "IsLaunching", "IsRunning" };
        var expected = typeof(Game).GetProperties().Where(p => p.CanWrite && !transient.Contains(p.Name)).Select(p => p.Name).Order();
        var columns = GameRow.Columns.Split(',', StringSplitOptions.TrimEntries)
            .Except(["LegacyJson", "ReleaseMonth", "ReleaseDay"])
            .Select(c => c == "ReleaseYear" ? "ReleaseDate" : c.EndsWith("Present", StringComparison.Ordinal) ? c[..^7] : c).Order();
        Assert.Equal(expected, columns);
        var mapper = File.ReadAllText(RepositoryPaths.Combine("legacy", "source", "Playnite", "Database", "Collections", "GamesCollection.cs"));
        foreach (var property in transient)
            Assert.Contains($"game.{property} = false;", mapper);
    }

    [Fact]
    public async Task Runtime_flags_reset_as_the_v10_collection_initializer_does()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var game = new Game(Id, "570") { IsInstalling = true, IsUninstalling = true, IsLaunching = true, IsRunning = true };
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(game)); return 0; });
        var read = (await database.ReadAsync(s => s.Games.GetAsync(Id)))!.Value;
        Assert.False(read.IsInstalling); Assert.False(read.IsUninstalling); Assert.False(read.IsLaunching); Assert.False(read.IsRunning);
    }

    [Fact]
    public async Task Read_transaction_keeps_the_same_graph_after_another_writer_commits()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Id, "570", "before") { Links = [new Link("before", "old")] })); return 0; });
        await database.ReadAsync(async snapshot =>
        {
            var before = (await snapshot.Games.GetAsync(Id))!;
            await database.WriteAsync(async writer =>
            {
                var item = (await writer.Games.GetAsync(Id))!;
                item.Value.Name = "after"; item.Value.Links = [new Link("after", "new")];
                await writer.Games.UpdateAsync(item); return 0;
            });
            var same = (await snapshot.Games.GetAsync(Id))!;
            Assert.Equal(JsonSerialization.ToJson(before.Value), JsonSerialization.ToJson(same.Value));
            return 0;
        });
        Assert.Equal("after", (await database.ReadAsync(s => s.Games.GetAsync(Id)))!.Value.Name);
    }

    [Fact]
    public async Task Fixed_width_unsigned_values_sort_numerically_without_signed_overflow()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var numbers = new[] { ulong.MaxValue, 10UL, 9223372036854775808UL, 0UL, 9UL };
        await database.WriteAsync(async s =>
        {
            foreach (var number in numbers)
                await s.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), number.ToString()) { Playtime = number }));
            return 0;
        });
        using var connection = DatabaseTestDirectory.Connect(files.PathFor());
        var sorted = await connection.QueryAsync<string>("SELECT Playtime FROM game ORDER BY Playtime;");
        Assert.Equal(numbers.Order(), sorted.Select(ulong.Parse));
    }

    [Fact]
    public async Task Full_game_graph_round_trips_updates_and_deletes_without_losing_fields()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var game = CompleteGame();
        var expected = JsonSerialization.ToJson(game);
        await database.WriteAsync(async s =>
        {
            await AddMetadata(s);
            await s.Games.AddAsync(new StoredGame(game, Unknown));
            return 0;
        });
        database = await LibraryDatabase.OpenAsync(files.PathFor());
        var read = await database.ReadAsync(s => s.Games.GetAsync(Id));
        Assert.Equal(expected, JsonSerialization.ToJson(read!.Value));
        Assert.Equal(Unknown, read.LegacyJson);
        Assert.Single(await database.ReadAsync(s => s.Games.GetAllAsync()));
        read.Value.Name = "updated'; DROP TABLE game; --";
        read.Value.Playtime = 7;
        read.Value.Links = [];
        read.Value.TagIds = null;
        read.Value.GameActions![0].Script = "new script";
        await database.WriteAsync(async s => { await s.Games.UpdateAsync(read); return 0; });
        var changed = await database.ReadAsync(s => s.Games.GetAsync(Id));
        Assert.Equal(JsonSerialization.ToJson(read.Value), JsonSerialization.ToJson(changed!.Value));
        Assert.Equal(Unknown, changed.LegacyJson);
        Assert.True(await database.WriteAsync(s => s.Games.DeleteAsync(Id)));
        Assert.False(await database.WriteAsync(s => s.Games.DeleteAsync(Id)));
        Assert.Null(await database.ReadAsync(s => s.Games.GetAsync(Id)));
        using var connection = DatabaseTestDirectory.Connect(files.PathFor());
        foreach (var table in (await connection.QueryAsync<string>("SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'game_%';")).ToArray())
            Assert.Equal(0, await connection.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {table};"));
        Assert.Single(await database.ReadAsync(s => s.Metadata<Tag>().GetAllAsync()));
        Assert.Empty(await connection.QueryAsync("PRAGMA foreign_key_check;"));
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
    public async Task Every_metadata_kind_supports_real_file_crud(string kind)
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await (kind switch
        {
            "tag" => ExerciseMetadata(database, new Tag(MetadataId)),
            "genre" => ExerciseMetadata(database, new Genre(MetadataId)),
            "company" => ExerciseMetadata(database, new Company(MetadataId)),
            "feature" => ExerciseMetadata(database, new GameFeature(MetadataId)),
            "series" => ExerciseMetadata(database, new Series(MetadataId)),
            "category" => ExerciseMetadata(database, new Category(MetadataId)),
            "source" => ExerciseMetadata(database, new GameSource(MetadataId)),
            "age_rating" => ExerciseMetadata(database, new AgeRating(MetadataId)),
            "region" => ExerciseMetadata(database, new Region(MetadataId)),
            "completion_status" => ExerciseMetadata(database, new CompletionStatus(MetadataId)),
            "platform" => ExerciseMetadata(database, new GamePlatform(MetadataId)
            {
                SpecificationId = "pc_windows", Icon = "icons/é.png", Cover = "", Background = "C:\\back.jpg",
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
    }

    private static async Task ExerciseMetadata<T>(LibraryDatabase database, T value) where T : DatabaseObject
    {
        value.Name = "日本語'; DROP TABLE game; --";
        var stored = new StoredMetadata<T>(value, Unknown);
        await database.WriteAsync(async s => { await s.Metadata<T>().AddAsync(stored); return 0; });
        var read = await database.ReadAsync(s => s.Metadata<T>().GetAsync(MetadataId));
        Assert.Equal(JsonSerialization.ToJson(value), JsonSerialization.ToJson(read!.Value));
        Assert.Equal(Unknown, read.LegacyJson);
        Assert.Single(await database.ReadAsync(s => s.Metadata<T>().GetAllAsync()));
        await Assert.ThrowsAsync<SqliteException>(() => database.WriteAsync(async s => { await s.Metadata<T>().AddAsync(stored); return 0; }));
        value.Name = "renamed";
        if (value is GamePlatform platform) { platform.SpecificationId = "changed"; platform.Icon = null; platform.Cover = "new"; platform.Background = null; }
        await database.WriteAsync(async s => { await s.Metadata<T>().UpdateAsync(stored); return 0; });
        read = await database.ReadAsync(s => s.Metadata<T>().GetAsync(MetadataId));
        Assert.Equal(JsonSerialization.ToJson(value), JsonSerialization.ToJson(read!.Value));
        Assert.Equal(Unknown, read.LegacyJson);
        Assert.True(await database.WriteAsync(s => s.Metadata<T>().DeleteAsync(MetadataId)));
        Assert.False(await database.WriteAsync(s => s.Metadata<T>().DeleteAsync(MetadataId)));
        Assert.Null(await database.ReadAsync(s => s.Metadata<T>().GetAsync(MetadataId)));
        Assert.Empty(await database.ReadAsync(s => s.Metadata<T>().GetAllAsync()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => database.WriteAsync(async s => { await s.Metadata<T>().UpdateAsync(stored); return 0; }));
    }

    [Fact]
    public async Task Failed_callback_rolls_back_metadata_games_and_children_together()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.WriteAsync<int>(async s =>
        {
            await AddMetadata(s);
            await s.Games.AddAsync(new StoredGame(CompleteGame()));
            throw new InvalidOperationException("injected callback failure");
        }));
        Assert.Empty(await database.ReadAsync(s => s.Games.GetAllAsync()));
        Assert.Empty(await database.ReadAsync(s => s.Metadata<Tag>().GetAllAsync()));
        using var connection = DatabaseTestDirectory.Connect(files.PathFor());
        Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM game_action;"));
        Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM game_tag;"));
    }

    [Fact]
    public async Task Failed_graph_update_rolls_back_even_if_callback_catches_the_error()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var original = CompleteGame();
        await database.WriteAsync(async s => { await AddMetadata(s); await s.Games.AddAsync(new StoredGame(original, Unknown)); return 0; });
        var expected = JsonSerialization.ToJson(original);
        original.Name = "must roll back";
        original.TagIds = [Guid.NewGuid()];
        await database.WriteAsync(async s =>
        {
            await Assert.ThrowsAsync<SqliteException>(() => s.Games.UpdateAsync(new StoredGame(original)));
            await s.Metadata<Tag>().AddAsync(new StoredMetadata<Tag>(new Tag(Guid.NewGuid(), "unrelated commit")));
            return 0;
        });
        var after = await database.ReadAsync(s => s.Games.GetAsync(Id));
        Assert.Equal(expected, JsonSerialization.ToJson(after!.Value));
        Assert.Equal(Unknown, after.LegacyJson);
        Assert.Equal(2, (await database.ReadAsync(s => s.Metadata<Tag>().GetAllAsync())).Count);
    }

    [Fact]
    public async Task Unresolved_references_fail_without_partial_graphs_or_silent_drops()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await database.WriteAsync(async s =>
        {
            var missing = new Game(Id, "570") { TagIds = [MetadataId] };
            await Assert.ThrowsAsync<SqliteException>(() => s.Games.AddAsync(new StoredGame(missing)));
            Assert.Null(await s.Games.GetAsync(Id));
            await s.Metadata<Tag>().AddAsync(new StoredMetadata<Tag>(new Tag(MetadataId, "resolved")));
            await s.Games.AddAsync(new StoredGame(missing));
            await Assert.ThrowsAsync<SqliteException>(() => s.Metadata<Tag>().DeleteAsync(MetadataId));
            return 0;
        });
        Assert.Equal(new[] { MetadataId }, (await database.ReadAsync(s => s.Games.GetAsync(Id)))!.Value.TagIds);
    }

    [Fact]
    public async Task Concurrent_writers_serialize_read_modify_write_and_readers_keep_a_snapshot()
    {
        using var files = new DatabaseTestDirectory();
        var firstDatabase = await LibraryDatabase.OpenAsync(files.PathFor());
        var secondDatabase = await LibraryDatabase.OpenAsync(files.PathFor());
        await firstDatabase.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Id, "570"))); return 0; });
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(() => firstDatabase.WriteAsync(async s =>
        {
            var item = (await s.Games.GetAsync(Id))!;
            item.Value.PlayCount++;
            await s.Games.UpdateAsync(item);
            firstEntered.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return 0;
        }));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Task.Run(async () =>
        {
            secondStarted.SetResult();
            return await secondDatabase.WriteAsync(async s =>
            {
                secondEntered.SetResult();
                var item = (await s.Games.GetAsync(Id))!;
                item.Value.PlayCount++;
                await s.Games.UpdateAsync(item);
                return 0;
            });
        });
        try
        {
            await secondStarted.Task;
            Assert.Equal(0UL, (await secondDatabase.ReadAsync(s => s.Games.GetAsync(Id)))!.Value.PlayCount);
            Assert.NotSame(secondEntered.Task, await Task.WhenAny(secondEntered.Task, Task.Delay(150)));
        }
        finally { release.TrySetResult(); await Task.WhenAll(first, second); }
        Assert.Equal(2UL, (await firstDatabase.ReadAsync(s => s.Games.GetAsync(Id)))!.Value.PlayCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_commit_rolls_back_and_releases_the_writer(bool preCancelled)
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        using var cancel = new CancellationTokenSource();
        if (preCancelled) cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.WriteAsync(async s =>
        {
            await s.Games.AddAsync(new StoredGame(new Game(Id, "570")));
            cancel.Cancel();
            return 0;
        }, cancel.Token));
        Assert.Empty(await database.ReadAsync(s => s.Games.GetAllAsync()));
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Id, "570"))); return 0; });
        Assert.Single(await database.ReadAsync(s => s.Games.GetAllAsync()));
    }

    [Fact]
    public async Task Read_callback_cannot_write_and_connections_close_after_callbacks()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await Assert.ThrowsAsync<SqliteException>(() => database.ReadAsync(async s =>
        {
            await s.Games.AddAsync(new StoredGame(new Game(Id, "570")));
            return 0;
        }));
        var escaped = await database.ReadAsync(s => Task.FromResult(s.Games));
        await Assert.ThrowsAsync<InvalidOperationException>(() => escaped.GetAsync(Id));
        File.Move(files.PathFor(), files.PathFor("closed.sqlite"));
        var reopened = await LibraryDatabase.OpenAsync(files.PathFor("closed.sqlite"));
        Assert.Empty(await reopened.ReadAsync(s => s.Games.GetAllAsync()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9223372036854775807")]
    [InlineData("9223372036854775808")]
    [InlineData("18446744073709551615")]
    public async Task Entire_unsigned_numeric_domain_survives_sqlite(string number)
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var value = ulong.Parse(number);
        await database.WriteAsync(async s =>
        {
            await s.Games.AddAsync(new StoredGame(new Game(Id, "570") { Playtime = value, PlayCount = value, InstallSize = value }));
            return 0;
        });
        var item = (await database.ReadAsync(s => s.Games.GetAsync(Id)))!.Value;
        Assert.Equal(value, item.Playtime); Assert.Equal(value, item.PlayCount); Assert.Equal(value, item.InstallSize);
        using var connection = DatabaseTestDirectory.Connect(files.PathFor());
        Assert.Equal("text", await connection.QuerySingleAsync<string>("SELECT typeof(Playtime) FROM game;"));
        Assert.Equal(value.ToString("D20"), await connection.QuerySingleAsync<string>("SELECT Playtime FROM game;"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Partial_release_dates_keep_their_precision(int precision)
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        ReleaseDate date = precision switch { 0 => ReleaseDate.Empty, 1 => new(2020), 2 => new(2020, 2), _ => new(2020, 2, 29) };
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Id, "570") { ReleaseDate = date })); return 0; });
        Assert.Equal(date, (await database.ReadAsync(s => s.Games.GetAsync(Id)))!.Value.ReleaseDate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_and_empty_collections_are_distinct(bool empty)
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        var game = new Game(Id, "570");
        foreach (var property in typeof(Game).GetProperties().Where(p => p.PropertyType == typeof(List<Guid>)))
            property.SetValue(game, empty ? new List<Guid>() : null);
        game.Links = empty ? [] : null; game.Roms = empty ? [] : null; game.GameActions = empty ? [] : null;
        await database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(game)); return 0; });
        var read = (await database.ReadAsync(s => s.Games.GetAsync(Id)))!.Value;
        foreach (var property in typeof(Game).GetProperties().Where(p => p.PropertyType == typeof(List<Guid>)))
            Assert.Equal(empty, property.GetValue(read) != null);
        Assert.Equal(empty, read.Links != null); Assert.Equal(empty, read.Roms != null); Assert.Equal(empty, read.GameActions != null);
        Assert.Null(read.ReleaseDate); Assert.Null(read.InstallSize); Assert.Null(read.Added);
        Assert.Equal(Guid.Empty, read.SourceId); Assert.Equal(Guid.Empty, read.CompletionStatusId);
    }

    [Fact]
    public async Task Ambiguous_timestamps_empty_identities_and_invalid_unknown_bags_are_rejected()
    {
        using var files = new DatabaseTestDirectory();
        var database = await LibraryDatabase.OpenAsync(files.PathFor());
        await Assert.ThrowsAsync<ArgumentException>(() => database.WriteAsync(async s =>
        {
            await s.Games.AddAsync(new StoredGame(new Game(Id, "570") { Added = new DateTime(2020, 1, 1) })); return 0;
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Guid.Empty, "570"))); return 0; }));
        await Assert.ThrowsAsync<ArgumentException>(() => database.WriteAsync(async s => { await s.Metadata<Tag>().AddAsync(new StoredMetadata<Tag>(new Tag(Guid.Empty))); return 0; }));
        foreach (var bag in new[] { "[]", "null", "not JSON" })
            await Assert.ThrowsAsync<SqliteException>(() => database.WriteAsync(async s => { await s.Games.AddAsync(new StoredGame(new Game(Id, "570"), bag)); return 0; }));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => database.WriteAsync(async s => { await s.Games.UpdateAsync(new StoredGame(new Game(Id, "570"))); return 0; }));
        Assert.Empty(await database.ReadAsync(s => s.Games.GetAllAsync()));
        Assert.Throws<ArgumentException>(() => LibraryDatabase.OpenAsync("relative.sqlite").GetAwaiter().GetResult());
        await Assert.ThrowsAsync<ArgumentException>(() => database.ReadAsync(s => Task.FromResult(s.Metadata<Game>())));
    }

    private static Game CompleteGame() => new(Id, "570", "日本語'; DROP TABLE game; --")
    {
        PluginId = Guid.Parse("bba5e8d8-6866-484c-a7e0-d9f5a17b3daa"), SortingName = "sort", BackgroundImage = "background",
        Description = "<p>description</p>", Notes = "notes", Icon = "icon", CoverImage = "cover", Manual = "C:\\Manual.pdf",
        InstallDirectory = "C:\\Games\\日本語", Version = "10.64", Hidden = true, Favorite = true, EnableSystemHdr = true,
        IncludeLibraryPluginAction = false, IsInstalled = true, OverrideInstallState = true,
        UseGlobalPreScript = false, UseGlobalPostScript = false, UseGlobalGameStartedScript = false,
        PreScript = "pre", PostScript = "post", GameStartedScript = "started", SourceId = MetadataId, CompletionStatusId = MetadataId,
        UserScore = -1, CriticScore = int.MaxValue, CommunityScore = 0, ReleaseDate = new ReleaseDate(2020, 2, 29),
        LastActivity = new DateTime(2020, 2, 29, 13, 14, 15, DateTimeKind.Utc), Added = new DateTime(2018, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Modified = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc), LastSizeScanDate = new DateTime(2022, 4, 5, 6, 7, 8, DateTimeKind.Utc),
        Playtime = ulong.MaxValue, PlayCount = 9223372036854775808UL, InstallSize = ulong.MaxValue,
        TagIds = [MetadataId, MetadataId], GenreIds = [MetadataId], PlatformIds = [MetadataId], PublisherIds = [MetadataId],
        DeveloperIds = [MetadataId], CategoryIds = [MetadataId], FeatureIds = [MetadataId], SeriesIds = [MetadataId],
        AgeRatingIds = [MetadataId], RegionIds = [MetadataId],
        Links = [new Link("store", "steam://run/570"), new Link(null!, ""), new Link("store", "steam://run/570")],
        Roms = [new GameRom("one", "C:\\roms\\one.iso"), new GameRom("two", "/roms/二.iso")],
        GameActions = [new GameAction
        {
            Type = (GameActionType)97, Name = "play", Path = "steam://run/570", Arguments = "--arg ' spaced '", WorkingDir = "/games/日本語",
            AdditionalArguments = "additional", OverrideDefaultArgs = true, IsPlayAction = true, EmulatorId = MetadataId,
            EmulatorProfileId = "profile", TrackingMode = (TrackingMode)98, TrackingPath = "tracking", Script = "script",
            InitialTrackingDelay = 123, TrackingFrequency = 456,
        }, new GameAction { Name = "secondary" }],
    };

    private static async Task AddMetadata(LibrarySession session)
    {
        await session.Metadata<Tag>().AddAsync(new StoredMetadata<Tag>(new Tag(MetadataId, "tag")));
        await session.Metadata<Genre>().AddAsync(new StoredMetadata<Genre>(new Genre(MetadataId, "genre")));
        await session.Metadata<Company>().AddAsync(new StoredMetadata<Company>(new Company(MetadataId, "company")));
        await session.Metadata<GameFeature>().AddAsync(new StoredMetadata<GameFeature>(new GameFeature(MetadataId, "feature")));
        await session.Metadata<Series>().AddAsync(new StoredMetadata<Series>(new Series(MetadataId, "series")));
        await session.Metadata<Category>().AddAsync(new StoredMetadata<Category>(new Category(MetadataId, "category")));
        await session.Metadata<GameSource>().AddAsync(new StoredMetadata<GameSource>(new GameSource(MetadataId, "source")));
        await session.Metadata<AgeRating>().AddAsync(new StoredMetadata<AgeRating>(new AgeRating(MetadataId, "rating")));
        await session.Metadata<Region>().AddAsync(new StoredMetadata<Region>(new Region(MetadataId, "region")));
        await session.Metadata<CompletionStatus>().AddAsync(new StoredMetadata<CompletionStatus>(new CompletionStatus(MetadataId, "status")));
        await session.Metadata<GamePlatform>().AddAsync(new StoredMetadata<GamePlatform>(new GamePlatform(MetadataId, "platform")));
    }
}
