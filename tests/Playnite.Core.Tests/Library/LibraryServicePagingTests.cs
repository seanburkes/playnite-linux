using Playnite.Core.Database;
using Playnite.Core.Library;
using Playnite.Core.Models;
using Playnite.Core.Queries;
using Playnite.Core.Tests.Database;
using Xunit;

namespace Playnite.Core.Tests.Library;

/// <summary>
/// Covers the p6-001 adoption of <see cref="PagedLibraryReader"/>: a plain bounded list reads
/// only its window, while filters, text search and grouping keep their v10 in-memory semantics.
/// </summary>
public sealed class LibraryServicePagingTests
{
    [Fact]
    public async Task Unfiltered_page_reads_only_the_requested_window()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = Enumerable.Range(1, 1000).Select(Id).ToArray();
        var session = new SpySession { Total = ids.Length, Ids = ids };
        var snapshot = new FakeReader();
        var service = new LibraryService(snapshot, new PagedLibraryReader(new SpySessionFactory(session)));

        var page = await service.ListAsync(new(Offset: 995, Limit: 5), cancellationToken);

        var read = Assert.Single(session.PageReads);
        Assert.Contains("LIMIT @Limit OFFSET @Offset", read.Sql, StringComparison.Ordinal);
        Assert.Equal(995, read.Offset);
        Assert.Equal(5, read.Limit);
        Assert.Equal(1, session.CountCalls);
        Assert.Equal(ids[995..], session.GraphReads);
        Assert.Equal(ids[995..], Ids(page));
        Assert.Equal(1000, page.TotalCount);
        Assert.Empty(page.Groups);
        Assert.Equal(0, snapshot.ListCalls);
    }

    [Fact]
    public async Task Null_limit_reads_the_remainder_window_without_a_full_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = Enumerable.Range(1, 8).Select(Id).ToArray();
        var session = new SpySession { Total = ids.Length, Ids = ids };
        var snapshot = new FakeReader();
        var service = new LibraryService(snapshot, new PagedLibraryReader(new SpySessionFactory(session)));

        var page = await service.ListAsync(cancellationToken: cancellationToken);

        var read = Assert.Single(session.PageReads);
        Assert.Equal(0, read.Offset);
        Assert.Equal(LibraryQuery.MaxLimit, read.Limit);
        Assert.Equal(ids, session.GraphReads);
        Assert.Equal(ids, Ids(page));
        Assert.Equal(8, page.TotalCount);
        Assert.Equal(0, snapshot.ListCalls);
    }

    [Fact]
    public async Task Search_and_grouped_queries_keep_the_snapshot_path()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var snapshot = new FakeReader(Game(1, "Alpha"), Game(2, "Beta"), Game(3, "Gamma"));
        var session = new SpySession { Total = 3, Ids = [Id(1), Id(2), Id(3)] };
        var service = new LibraryService(snapshot, new PagedLibraryReader(new SpySessionFactory(session)));

        Assert.Equal([Id(1)], Ids(await service.SearchAsync("Alpha", cancellationToken: cancellationToken)));
        Assert.Empty(session.PageReads);
        Assert.Equal(1, snapshot.ListCalls);

        await service.ListAsync(new(GroupBy: LibraryGroupBy.Name), cancellationToken);
        Assert.Empty(session.PageReads);
        Assert.Equal(2, snapshot.ListCalls);
    }

    [Fact]
    public async Task Null_limit_beyond_the_largest_page_falls_back_to_one_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var total = LibraryQuery.MaxLimit + 3;
        var ids = Enumerable.Range(1, total).Select(Id).ToArray();
        var session = new SpySession { Total = total, Ids = ids };
        var games = ids.Select((id, index) => new StoredGame(new Game(id, $"Game {index:D5}"))).ToArray();
        var snapshot = new FakeReader(games);
        var service = new LibraryService(snapshot, new PagedLibraryReader(new SpySessionFactory(session)));

        var page = await service.ListAsync(cancellationToken: cancellationToken);

        var read = Assert.Single(session.PageReads);
        Assert.Equal(LibraryQuery.MaxLimit, read.Limit);
        Assert.Equal(1, snapshot.ListCalls);
        Assert.Equal(total, page.Games.Count);
        Assert.Equal(total, page.TotalCount);
    }

    [Theory]
    [InlineData(-1, 10, "Offset")]
    [InlineData(LibraryQuery.MaxOffset + 1, 10, "Offset")]
    [InlineData(0, 0, "Limit")]
    [InlineData(0, -1, "Limit")]
    [InlineData(0, LibraryQuery.MaxLimit + 1, "Limit")]
    public async Task Windowed_bounds_are_rejected_before_any_read(int offset, int limit, string parameter)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = new SpySession { Total = 1, Ids = [Id(1)] };
        var snapshot = new FakeReader(Game(1, "Alpha"));
        var service = new LibraryService(snapshot, new PagedLibraryReader(new SpySessionFactory(session)));

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ListAsync(new(Offset: offset, Limit: limit), cancellationToken));

        Assert.Equal(parameter, error.ParamName);
        Assert.Empty(session.PageReads);
        Assert.Equal(0, snapshot.ListCalls);
    }

    [Fact]
    public async Task Unknown_group_key_is_rejected_before_any_read()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = new SpySession { Total = 1, Ids = [Id(1)] };
        var snapshot = new FakeReader(Game(1, "Alpha"));
        var service = new LibraryService(snapshot, new PagedLibraryReader(new SpySessionFactory(session)));

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ListAsync(new(GroupBy: (LibraryGroupBy)123), cancellationToken));

        Assert.Equal("GroupBy", error.ParamName);
        Assert.Empty(session.PageReads);
        Assert.Equal(0, snapshot.ListCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Text_order_matches_the_paged_reader_for_mixed_case_accented_and_numeric_titles(bool descending)
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var games = TextGames();
        var database = await SeedAsync(files, cancellationToken, games);
        var service = new LibraryService(database);
        var reader = new PagedLibraryReader(database);

        var expected = games.Select(stored => stored.Value)
            .OrderBy(game => game, new PagedLibraryComparer(SortOrder.Name, descending))
            .Select(game => game.Id)
            .ToArray();
        var fromService = await service.ListAsync(
            new(SortBy: SortOrder.Name, Descending: descending, Limit: 20), cancellationToken);
        var fromReader = await reader.ListAsync(
            new PagedLibraryQuery(SortBy: SortOrder.Name, Descending: descending, Limit: 20), cancellationToken);

        Assert.Equal(expected, Ids(fromReader).ToArray());
        Assert.Equal(expected, Ids(fromService).ToArray());
        Assert.Equal(9, fromService.TotalCount);

        // NOCASE keeps ASCII case-only titles equal and the stored identity breaks that tie, so
        // the identity order stays ascending in both directions.
        Guid[] ascending = [Id(14), Id(18), Id(16), Id(17), Id(10), Id(11), Id(15), Id(13), Id(12)];
        Guid[] descendingOrder = [Id(12), Id(13), Id(10), Id(11), Id(15), Id(17), Id(16), Id(18), Id(14)];
        Assert.Equal(descending ? descendingOrder : ascending, Ids(fromReader));
        Assert.Equal(descending ? descendingOrder : ascending, Ids(fromService));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Filtered_text_order_matches_the_paged_reader_for_mixed_case_accented_and_numeric_titles(bool descending)
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var games = TextGames();
        var database = await SeedAsync(files, cancellationToken, games);
        var service = new LibraryService(database);
        var reader = new PagedLibraryReader(database);

        // A non-null filter forces the one-snapshot in-memory path; the paged reader is the
        // NOCASE oracle. If LibraryService falls back to GameComparer the two disagree.
        var fromService = await service.ListAsync(
            new(Filter: MatchAll(), SortBy: SortOrder.Name, Descending: descending, Limit: 20), cancellationToken);
        var fromReader = await reader.ListAsync(
            new PagedLibraryQuery(SortOrder.Name, descending, Limit: 20), cancellationToken);

        Assert.Equal(Ids(fromReader), Ids(fromService));
        Assert.Equal(9, fromService.TotalCount);

        Guid[] ascending = [Id(14), Id(18), Id(16), Id(17), Id(10), Id(11), Id(15), Id(13), Id(12)];
        Guid[] descendingOrder = [Id(12), Id(13), Id(10), Id(11), Id(15), Id(17), Id(16), Id(18), Id(14)];
        Assert.Equal(descending ? descendingOrder : ascending, Ids(fromReader));
        Assert.Equal(descending ? descendingOrder : ascending, Ids(fromService));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Filtered_in_memory_sort_keeps_the_nocase_order_without_reading_a_page(bool descending)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var games = TextGames();
        var snapshot = new FakeReader(games);
        var session = new SpySession { Total = games.Length, Ids = games.Select(game => game.Value.Id).ToArray() };
        var service = new LibraryService(snapshot, new PagedLibraryReader(new SpySessionFactory(session)));

        var page = await service.ListAsync(
            new(Filter: MatchAll(), SortBy: SortOrder.Name, Descending: descending, Limit: 20), cancellationToken);

        Assert.Empty(session.PageReads);
        Assert.Equal(1, snapshot.ListCalls);
        var expected = games.Select(game => game.Value)
            .OrderBy(game => game, new PagedLibraryComparer(SortOrder.Name, descending))
            .Select(game => game.Id)
            .ToArray();
        Assert.Equal(expected, Ids(page));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_or_null_search_reads_the_paged_window(string? term)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = Enumerable.Range(1, 5).Select(Id).ToArray();
        var session = new SpySession { Total = ids.Length, Ids = ids };
        var snapshot = new FakeReader(ids.Select((_, index) => Game(index + 1, $"Game {index}")).ToArray());
        var service = new LibraryService(snapshot, new PagedLibraryReader(new SpySessionFactory(session)));

        var page = await service.SearchAsync(term, new(Offset: 1, Limit: 2), cancellationToken);

        var read = Assert.Single(session.PageReads);
        Assert.Equal(1, read.Offset);
        Assert.Equal(2, read.Limit);
        Assert.Equal(ids[1..3], session.GraphReads);
        Assert.Equal(ids[1..3], Ids(page));
        Assert.Equal(0, snapshot.ListCalls);
    }

    [Fact]
    public async Task Non_name_order_matches_the_paged_reader_and_breaks_ties_by_nocase_name()
    {
        using var files = new DatabaseTestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var games = TextGames();
        var database = await SeedAsync(files, cancellationToken, games);
        var service = new LibraryService(database);
        var reader = new PagedLibraryReader(database);

        foreach (var descending in new[] { false, true })
        {
            var expected = games.Select(stored => stored.Value)
                .OrderBy(game => game, new PagedLibraryComparer(SortOrder.Version, descending))
                .Select(game => game.Id)
                .ToArray();
            var fromService = await service.ListAsync(
                new(SortBy: SortOrder.Version, Descending: descending, Limit: 20), cancellationToken);
            var fromReader = await reader.ListAsync(
                new PagedLibraryQuery(SortBy: SortOrder.Version, Descending: descending, Limit: 20), cancellationToken);

            Assert.Equal(expected, Ids(fromReader).ToArray());
            Assert.Equal(expected, Ids(fromService).ToArray());
        }
    }

    private static LibraryFilter MatchAll() => LibraryFilter.FromSettings(new FilterSettings());

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
        new(new Game(Id(14), "14") { InstallDirectory = "C:/Games/B" }),
        new(new Game(Id(16), "16") { Name = "10 Games", Version = "10.0", InstallDirectory = "C:/Games/10" }),
        new(new Game(Id(17), "17") { Name = "2 Games", Version = "2.0", InstallDirectory = "C:/Games/2" }),
        new(new Game(Id(18), "18") { Name = "1 Game", Version = "1.0", InstallDirectory = "C:/Games/1" }),
    ];

    private static StoredGame Game(int id, string? name)
        => new(new Game(Id(id), id.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Name = name! });

    private static Guid Id(int id) => Guid.Parse($"00000000-0000-0000-0000-{id:x12}");

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

    private sealed class FakeReader(params StoredGame[] games) : ILibraryGameReader
    {
        public int ListCalls { get; private set; }

        public Task<StoredGame?> GetAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(games.SingleOrDefault(game => game.Value.Id == id));

        public Task<IReadOnlyList<StoredGame>> ListAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<StoredGame>>(games);
        }
    }

    private sealed class SpySessionFactory(SpySession session) : IPagedLibrarySessionFactory
    {
        public Task<T> ReadAsync<T>(Func<IPagedLibrarySession, Task<T>> read, CancellationToken cancellationToken)
            => read(session);
    }

    private sealed class SpySession : IPagedLibrarySession
    {
        public long Total { get; init; }
        public Guid[] Ids { get; init; } = [];
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
            return Task.FromResult(new StoredGame(new Game(id, id.ToString("D")) { Name = id.ToString("D") }));
        }
    }
}
