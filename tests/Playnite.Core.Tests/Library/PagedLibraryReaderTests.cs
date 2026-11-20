using System.Globalization;
using Dapper;
using Playnite.Core.Database;
using Playnite.Core.Library;
using Playnite.Core.Models;
using Playnite.Core.Queries;
using Playnite.Core.Tests.Database;
using Xunit;

namespace Playnite.Core.Tests.Library;

public sealed class PagedLibraryReaderTests
{
    [Fact]
    public async Task List_orders_by_effective_name_then_identity()
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await SeedAsync(files, cancellationToken,
            Game(30, "Same"),
            Game(2, "Same"),
            Game(10, "Same"),
            Game(4, "Zeta", sortingName: "Alpha"),
            Game(5, "Beta"));

        var page = await new PagedLibraryReader(database).ListAsync(new(Limit: 10), cancellationToken);

        Assert.Equal([Id(4), Id(5), Id(2), Id(10), Id(30)], Ids(page));
        Assert.Equal(5, page.TotalCount);
        Assert.Empty(page.Groups);
    }

    [Fact]
    public async Task First_middle_last_and_past_end_pages_are_contiguous()
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await SeedAsync(files, cancellationToken,
            Game(5, "Game 05"), Game(1, "Game 01"), Game(4, "Game 04"),
            Game(2, "Game 02"), Game(3, "Game 03"));
        var reader = new PagedLibraryReader(database);

        var first = await reader.ListAsync(new(Offset: 0, Limit: 2), cancellationToken);
        var middle = await reader.ListAsync(new(Offset: 2, Limit: 2), cancellationToken);
        var last = await reader.ListAsync(new(Offset: 4, Limit: 2), cancellationToken);
        var pastEnd = await reader.ListAsync(new(Offset: 5, Limit: 2), cancellationToken);
        var beyondBound = await reader.ListAsync(new(Offset: LibraryQuery.MaxOffset, Limit: 2), cancellationToken);

        Assert.Equal([Id(1), Id(2)], Ids(first));
        Assert.Equal([Id(3), Id(4)], Ids(middle));
        Assert.Equal([Id(5)], Ids(last));
        Assert.All([first, middle, last], page => Assert.Equal(5, page.TotalCount));
        Assert.Empty(pastEnd.Games);
        Assert.Empty(pastEnd.Groups);
        Assert.Equal(5, pastEnd.TotalCount);
        Assert.Empty(beyondBound.Games);
        Assert.Equal(5, beyondBound.TotalCount);
    }

    [Fact]
    public async Task Empty_library_returns_an_empty_page()
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await SeedAsync(files, cancellationToken);

        var page = await new PagedLibraryReader(database).ListAsync(cancellationToken: cancellationToken);

        Assert.Empty(page.Games);
        Assert.Empty(page.Groups);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Duplicate_primary_keys_use_name_and_identity_ties_deterministically()
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var games = TieGames();
        var database = await SeedAsync(files, cancellationToken, games);
        var reader = new PagedLibraryReader(database);

        Guid[] expected = [Id(2), Id(4), Id(9), Id(1), Id(7), Id(3)];

        var first = await reader.ListAsync(new(SortBy: SortOrder.UserScore, Limit: 10), cancellationToken);
        var second = await reader.ListAsync(new(SortBy: SortOrder.UserScore, Limit: 10), cancellationToken);
        var repeated = await reader.ListAsync(new(SortBy: SortOrder.UserScore, Limit: 10), cancellationToken);

        Assert.Equal(expected, Ids(first));
        Assert.Equal(expected, Ids(second));
        Assert.Equal(expected, Ids(repeated));
        Assert.Equal(expected, Ids(await reader.ListAsync(
            new(SortBy: SortOrder.UserScore, Descending: true, Limit: 10), cancellationToken)));

        using var reversedFiles = new DatabaseTestDirectory();
        var reversed = await SeedAsync(reversedFiles, cancellationToken, games.Reverse().ToArray());
        Assert.Equal(expected, Ids(await new PagedLibraryReader(reversed).ListAsync(
            new(SortBy: SortOrder.UserScore, Limit: 10), cancellationToken)));
    }

    [Theory]
    [InlineData(-1, 10, "Offset")]
    [InlineData(LibraryQuery.MaxOffset + 1, 10, "Offset")]
    [InlineData(0, 0, "Limit")]
    [InlineData(0, -1, "Limit")]
    [InlineData(0, LibraryQuery.MaxLimit + 1, "Limit")]
    public async Task Invalid_bounds_are_rejected(int offset, int limit, string parameter)
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await SeedAsync(files, cancellationToken, Game(1, "Game 01"));

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new PagedLibraryReader(database).ListAsync(new(Offset: offset, Limit: limit), cancellationToken));

        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public async Task Unknown_sort_key_is_rejected()
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await SeedAsync(files, cancellationToken, Game(1, "Game 01"));

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new PagedLibraryReader(database).ListAsync(new(SortBy: (SortOrder)123), cancellationToken));

        Assert.Equal("SortBy", error.ParamName);
    }

    [Fact]
    public async Task Cancellation_before_read_throws_without_a_page()
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await SeedAsync(files, cancellationToken, Game(1, "Game 01"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PagedLibraryReader(database).ListAsync(cancellationToken: canceled.Token));
    }

    [Fact]
    public async Task Page_returns_only_the_requested_window()
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var games = Enumerable.Range(1, 25).Select(id => Game(id, $"Game {id:D2}")).ToArray();
        var database = await SeedAsync(files, cancellationToken, games);

        var page = await new PagedLibraryReader(database).ListAsync(new(Offset: 6, Limit: 4), cancellationToken);

        Assert.Equal([Id(7), Id(8), Id(9), Id(10)], Ids(page));
        Assert.Equal(25, page.TotalCount);
    }

    [Fact]
    public async Task Page_read_requests_only_the_window_from_the_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = Enumerable.Range(1, 1000).Select(Id).ToArray();
        var session = new SpySession { Total = ids.Length, Ids = ids };
        var reader = new PagedLibraryReader(new SpySessionFactory(session));

        var page = await reader.ListAsync(new(Offset: 995, Limit: 5), cancellationToken);

        var read = Assert.Single(session.PageReads);
        Assert.Contains("LIMIT @Limit OFFSET @Offset", read.Sql, StringComparison.Ordinal);
        Assert.EndsWith("game.Id ASC LIMIT @Limit OFFSET @Offset;", read.Sql, StringComparison.Ordinal);
        Assert.Equal(995, read.Offset);
        Assert.Equal(5, read.Limit);
        Assert.Equal(1, session.CountCalls);
        Assert.Equal(ids[995..], session.GraphReads);
        Assert.Equal(ids[995..], Ids(page));
        Assert.Equal(1000, page.TotalCount);
    }

    [Fact]
    public async Task Cancellation_during_graph_load_stops_without_a_page()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = Enumerable.Range(1, 10).Select(Id).ToArray();
        using var cancel = new CancellationTokenSource();
        var session = new SpySession { Total = ids.Length, Ids = ids, AfterGraphRead = cancel.Cancel };
        var reader = new PagedLibraryReader(new SpySessionFactory(session));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ListAsync(new(Limit: 10), linked.Token));
        Assert.Single(session.GraphReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Page_sql_pushes_limit_offset_and_identity_tiebreak(bool descending)
    {
        foreach (var sort in Enum.GetValues<SortOrder>())
        {
            var sql = PagedLibraryReader.PageSql(new(SortBy: sort, Descending: descending, Offset: 7, Limit: 9));

            Assert.StartsWith("SELECT game.Id FROM game ORDER BY ", sql);
            Assert.EndsWith(" LIMIT @Limit OFFSET @Offset;", sql);
            Assert.EndsWith("game.Id ASC LIMIT @Limit OFFSET @Offset;", sql);
            if (sort != SortOrder.Name)
            {
                Assert.Contains("COALESCE(NULLIF(game.SortingName, ''), game.Name) COLLATE NOCASE ASC", sql);
            }
        }
        Assert.Equal("SELECT COUNT(*) FROM game;", PagedLibraryReader.CountSql);
    }

    [Theory]
    [InlineData(SortOrder.LastActivity)]
    [InlineData(SortOrder.ReleaseDate)]
    [InlineData(SortOrder.PlayCount)]
    [InlineData(SortOrder.Playtime)]
    [InlineData(SortOrder.UserScore)]
    [InlineData(SortOrder.CriticScore)]
    [InlineData(SortOrder.CommunityScore)]
    [InlineData(SortOrder.Added)]
    [InlineData(SortOrder.Modified)]
    [InlineData(SortOrder.IsInstalled)]
    [InlineData(SortOrder.Hidden)]
    [InlineData(SortOrder.Favorite)]
    [InlineData(SortOrder.InstallSize)]
    [InlineData(SortOrder.RecentActivity)]
    public async Task Numeric_and_date_orders_match_the_in_memory_comparer_in_both_directions(SortOrder sort)
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await SeedAsync(files, cancellationToken, ComparisonGames());
        var service = new LibraryService(database);
        var reader = new PagedLibraryReader(database);

        foreach (var descending in new[] { false, true })
        {
            var expected = await service.ListAsync(
                new LibraryQuery(SortBy: sort, Descending: descending, Limit: 100), cancellationToken);
            var actual = await reader.ListAsync(
                new PagedLibraryQuery(SortBy: sort, Descending: descending, Limit: 100), cancellationToken);

            Assert.Equal(expected.Games.Select(game => game.Value.Id), Ids(actual));
            Assert.Equal(expected.TotalCount, actual.TotalCount);
        }
    }

    [Theory]
    [InlineData(SortOrder.Name)]
    [InlineData(SortOrder.Version)]
    [InlineData(SortOrder.InstallDirectory)]
    [InlineData(SortOrder.RomList)]
    public async Task Text_orders_match_the_canonical_in_memory_comparer(SortOrder sort)
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var games = TextGames();
        var database = await SeedAsync(files, cancellationToken, games);
        var reader = new PagedLibraryReader(database);

        foreach (var descending in new[] { false, true })
        {
            var expected = games.Select(stored => stored.Value)
                .OrderBy(game => game, new PagedLibraryComparer(sort, descending))
                .Select(game => game.Id);
            var actual = await reader.ListAsync(
                new(SortBy: sort, Descending: descending, Limit: 20), cancellationToken);

            Assert.Equal(expected, Ids(actual));
        }
    }

    [Fact]
    public async Task Comparer_total_order_equals_the_paged_text_identity_order()
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var games = TieGames();
        var database = await SeedAsync(files, cancellationToken, games);
        var reader = new PagedLibraryReader(database);

        var page = await reader.ListAsync(new(SortBy: SortOrder.UserScore, Limit: 10), cancellationToken);

        var comparerOrder = games.Select(stored => stored.Value)
            .OrderBy(game => game, new PagedLibraryComparer(SortOrder.UserScore))
            .Select(game => game.Id);
        Assert.Equal(comparerOrder, Ids(page));

        using var connection = DatabaseTestDirectory.Connect(files.PathFor());
        var stored = (await connection.QueryAsync<string>("SELECT Id FROM game;")).ToArray();
        Assert.All(games, game => Assert.Contains(game.Value.Id.ToString("D"), stored));
        var textOrder = games.Select(stored => stored.Value)
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(game => game.Id.ToString("D"), StringComparer.Ordinal)
            .Select(game => game.Id);
        Assert.Equal(textOrder, Ids(page));
    }

    [Fact]
    public async Task Case_only_name_differences_break_by_identity_not_by_bytes()
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await SeedAsync(files, cancellationToken, TextGames());
        var reader = new PagedLibraryReader(database);

        var page = await reader.ListAsync(new(SortBy: SortOrder.Name, Limit: 20), cancellationToken);

        Assert.Equal([Id(10), Id(11), Id(15)],
            Ids(page).Where(id => id == Id(10) || id == Id(11) || id == Id(15)));
    }

    private static StoredGame[] TieGames() =>
    [
        new(new Game(Id(1), "1") { Name = "Tie B", UserScore = 50 }),
        new(new Game(Id(2), "2") { Name = "Tie A", UserScore = 50 }),
        new(new Game(Id(3), "3") { Name = "Tie C", UserScore = 50 }),
        new(new Game(Id(4), "4") { Name = "Tie A", UserScore = 50 }),
        new(new Game(Id(7), "7") { Name = "Tie B", UserScore = 50 }),
        new(new Game(Id(9), "9") { Name = "Tie A", UserScore = 50 }),
    ];

    private static StoredGame[] ComparisonGames() =>
    [
        new(new Game(Id(1), "1")
        {
            Name = "Game 01",
            Playtime = 300,
            PlayCount = 5,
            UserScore = 80,
            CriticScore = 70,
            CommunityScore = 60,
            ReleaseDate = new ReleaseDate(2020, 5),
            Added = Utc(2020, 1, 2),
            Modified = Utc(2020, 2, 3),
            LastActivity = Utc(2020, 3, 4),
            IsInstalled = true,
            Favorite = true,
            InstallDirectory = "C:/Games/01",
            InstallSize = 100,
            Version = "v01",
            Roms = [new GameRom("one", "rom-a")],
        }),
        new(new Game(Id(2), "2")
        {
            Name = "Game 02",
            Playtime = 100,
            PlayCount = 2,
            UserScore = null,
            CriticScore = null,
            CommunityScore = null,
            ReleaseDate = null,
            Added = null,
            Modified = null,
            LastActivity = null,
            IsInstalled = true,
            Hidden = true,
            InstallDirectory = "C:/Games/02",
            InstallSize = null,
            Version = "v02",
        }),
        new(new Game(Id(3), "3")
        {
            Name = "Game 03",
            SortingName = "Game 02",
            Playtime = 300,
            PlayCount = 1,
            UserScore = 50,
            CriticScore = 70,
            CommunityScore = 40,
            ReleaseDate = new ReleaseDate(2020),
            Added = Utc(2020, 2, 2),
            Modified = Utc(2020, 2, 3),
            LastActivity = Utc(2019, 1, 4),
            IsInstalled = false,
            Favorite = true,
            InstallDirectory = "C:/Games/03",
            InstallSize = 200,
            Version = "v03",
            Roms = [new GameRom("two", "rom-b"), new GameRom("three", "rom-c")],
        }),
        new(new Game(Id(4), "4")
        {
            Name = "Game 04",
            Playtime = 0,
            PlayCount = 9,
            UserScore = 100,
            CriticScore = 0,
            CommunityScore = 100,
            ReleaseDate = new ReleaseDate(2020),
            Added = Utc(2021, 5, 6),
            Modified = Utc(2018, 7, 8),
            LastActivity = Utc(2021, 5, 6),
            IsInstalled = false,
            Hidden = false,
            InstallDirectory = "C:/Games/04",
            InstallSize = 0,
            Version = "v04",
        }),
        new(new Game(Id(5), "5")
        {
            Name = "Game 05",
            Playtime = 100,
            PlayCount = 2,
            UserScore = 100,
            CriticScore = 70,
            CommunityScore = 60,
            ReleaseDate = new ReleaseDate(2020, 5, 1),
            Added = Utc(2019, 5, 6),
            Modified = Utc(2020, 7, 8),
            LastActivity = Utc(2020, 5, 6),
            IsInstalled = true,
            Favorite = false,
            InstallDirectory = "C:/Games/05",
            InstallSize = 100,
            Version = "v05",
            Roms = [new GameRom("four", "rom-d")],
        }),
        new(new Game(Id(6), "6")
        {
            Name = "Game 06",
            Playtime = 0,
            PlayCount = 2,
            UserScore = 50,
            CriticScore = 0,
            CommunityScore = 40,
            ReleaseDate = new ReleaseDate(2020, 1),
            Added = Utc(2021, 5, 5),
            Modified = Utc(2018, 7, 9),
            LastActivity = null,
            IsInstalled = false,
            Hidden = true,
            Favorite = false,
            InstallDirectory = "C:/Games/06",
            InstallSize = 0,
            Version = "v06",
        }),
        new(new Game(Id(7), "7")
        {
            Name = "Game 07",
            ReleaseDate = ReleaseDate.Empty,
            Playtime = 0,
            PlayCount = 0,
            IsInstalled = false,
        }),
        new(new Game(Id(8), "8")
        {
            Name = "Game 08",
            ReleaseDate = new ReleaseDate(1),
            Playtime = 0,
            PlayCount = 0,
            IsInstalled = false,
        }),
    ];

    private static StoredGame[] TextGames() =>
    [
        new(new Game(Id(10), "10") { Name = "apple" }),
        new(new Game(Id(11), "11")
        {
            Name = "Apple",
            Version = "1.0",
            InstallDirectory = "C:/Games/A",
            Roms = [new GameRom("a", "rom-a")],
        }),
        new(new Game(Id(15), "15")
        {
            Name = "APPLE",
            Version = "1.0",
            InstallDirectory = "C:/Games/a",
            Roms = [new GameRom("a", "rom-a")],
        }),
        new(new Game(Id(12), "12")
        {
            Name = "Éclair",
            Version = "1.0",
            InstallDirectory = "C:/Games/É",
            Roms = [new GameRom("b", "rom-b")],
        }),
        new(new Game(Id(13), "13")
        {
            Name = "Zelda",
            Version = "2.0",
            Roms = [new GameRom("b", "rom-b"), new GameRom("c", "rom-c")],
        }),
        new(new Game(Id(14), "14")
        {
            InstallDirectory = "C:/Games/B",
        }),
        new(new Game(Id(16), "16")
        {
            Name = "10 Games",
            Version = "10.0",
            InstallDirectory = "C:/Games/10",
        }),
        new(new Game(Id(17), "17")
        {
            Name = "2 Games",
            Version = "2.0",
            InstallDirectory = "C:/Games/2",
        }),
        new(new Game(Id(18), "18")
        {
            Name = "1 Game",
            Version = "1.0",
            InstallDirectory = "C:/Games/1",
        }),
    ];

    private static StoredGame Game(int id, string? name, string? sortingName = null)
        => new(new Game(Id(id), id.ToString(CultureInfo.InvariantCulture))
        {
            Name = name!,
            SortingName = sortingName!,
        });

    private static Guid Id(int id) => Guid.Parse($"00000000-0000-0000-0000-{id:x12}");

    private static DateTime Utc(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<Guid> Ids(LibraryPage page) => page.Games.Select(game => game.Value.Id);

    private static async Task<LibraryDatabase> SeedAsync(
        DatabaseTestDirectory files, CancellationToken cancellationToken, params StoredGame[] games)
    {
        var database = await LibraryDatabase.OpenAsync(files.PathFor(), cancellationToken);
        await database.WriteAsync(async session =>
        {
            foreach (var game in games)
            {
                await session.Games.AddAsync(game);
            }
            return 0;
        }, cancellationToken);
        return database;
    }

    private sealed class SpySessionFactory(SpySession session) : IPagedLibrarySessionFactory
    {
        public Task<T> ReadAsync<T>(
            Func<IPagedLibrarySession, Task<T>> read, CancellationToken cancellationToken)
            => read(session);
    }

    private sealed class SpySession : IPagedLibrarySession
    {
        public long Total { get; init; }
        public Guid[] Ids { get; init; } = [];
        public Action? AfterGraphRead { get; init; }
        public int CountCalls { get; private set; }
        public List<(string Sql, int Offset, int Limit)> PageReads { get; } = [];
        public List<Guid> GraphReads { get; } = [];

        public Task<long> CountAsync(CancellationToken cancellationToken)
        {
            CountCalls++;
            return Task.FromResult(Total);
        }

        public Task<IReadOnlyList<string>> ReadPageIdsAsync(
            string sql, int offset, int limit, CancellationToken cancellationToken)
        {
            PageReads.Add((sql, offset, limit));
            var page = Ids.Skip(offset).Take(limit).Select(id => id.ToString("D")).ToArray();
            return Task.FromResult<IReadOnlyList<string>>(page);
        }

        public Task<StoredGame> GetGameAsync(Guid id, CancellationToken cancellationToken)
        {
            GraphReads.Add(id);
            AfterGraphRead?.Invoke();
            return Task.FromResult(new StoredGame(new Game(id, id.ToString("D")) { Name = id.ToString("D") }));
        }
    }
}
