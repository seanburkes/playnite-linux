using Playnite.Core.Database;
using Playnite.Core.Library;
using Playnite.Core.Models;
using Playnite.Core.Queries;
using Xunit;
using Playnite.SDK.Models;

namespace Playnite.Core.Tests.Library;

public sealed class LibraryServiceContractTests
{
    [Fact]
    public async Task List_orders_by_sorting_name_then_identity_regardless_of_repository_order()
    {
        var second = Game(2, "Beta", sortingName: "Alpha");
        var first = Game(1, "Zeta", sortingName: "Alpha");
        var unnamed = Game(3, null);
        var snapshot = new[] { second, unnamed, first };

        foreach (var games in new[] { snapshot, snapshot.Reverse().ToArray() })
        {
            var reader = new FakeReader(games);
            var page = await new LibraryService(reader).ListAsync();

            Assert.Equal([Id(3), Id(1), Id(2)], Ids(page));
            Assert.Equal(3, page.TotalCount);
            Assert.Empty(page.Groups);
            Assert.Equal(1, reader.ListCalls);
        }
    }

    [Fact]
    public async Task Get_preserves_the_graph_and_looks_up_only_the_requested_identity()
    {
        var stored = Game(1, "Alpha") with { LegacyJson = """{"future":true}""" };
        var reader = new FakeReader(stored);
        var service = new LibraryService(reader);

        Assert.Same(stored, await service.GetAsync(Id(1)));
        Assert.Null(await service.GetAsync(Id(2)));
        Assert.Equal([Id(1), Id(2)], reader.IdsRead);
        Assert.Equal(0, reader.ListCalls);
    }

    [Fact]
    public async Task Search_matches_display_names_with_v10_acronym_and_case_rules()
    {
        var target = Game(1, "Deep Rock Galactic");
        var alias = Game(2, "Alpha", sortingName: "Deep Rock Galactic");
        var unnamed = Game(3, null);
        var service = new LibraryService(new FakeReader(target, alias, unnamed));

        Assert.Equal([Id(1)], Ids(await service.SearchAsync("  drg  ")));
        Assert.Equal([Id(1)], Ids(await service.SearchAsync("DEEP")));
        Assert.Equal([Id(3), Id(1), Id(2)], Ids(await service.SearchAsync(null)));
        Assert.Equal([Id(3), Id(1), Id(2)], Ids(await service.SearchAsync(" \t")));
        Assert.Empty((await service.SearchAsync("nonexistent")).Games);
    }

    [Fact]
    public async Task Filters_compose_with_search_and_keep_v10_missing_metadata_and_hidden_rules()
    {
        var missing = Game(1, "Alpha One");
        var empty = Game(2, "Alpha Two");
        empty.Value.GenreIds = [];
        var hidden = Game(3, "Alpha Three");
        hidden.Value.Hidden = true;
        var assigned = Game(4, "Alpha Four");
        assigned.Value.GenreIds = [Id(10)];
        var service = new LibraryService(new FakeReader(assigned, empty, hidden, missing));
        var filter = LibraryFilter.Parse("""
            { "genre": { "ids": ["00000000-0000-0000-0000-000000000000"] }, "version": " ", "name": "\t" }
            """);

        Assert.Equal([Id(1), Id(2)], Ids(await service.SearchAsync("alpha", new(Filter: filter))));
        Assert.Equal([Id(3)], Ids(await service.ListAsync(new(Filter: LibraryFilter.Parse("""{ "hidden": true }""")))));
        Assert.Equal(4, (await service.ListAsync()).TotalCount);
    }

    [Fact]
    public async Task Typed_filter_is_a_snapshot_of_the_settings_and_nested_values()
    {
        var selected = Game(1, "Alpha");
        selected.Value.GenreIds = [Id(10)];
        var other = Game(2, "Beta");
        other.Value.Hidden = true;
        other.Value.GenreIds = [Id(20)];
        var settings = new FilterSettings { Genre = new() { Ids = [Id(10)] } };
        var filter = LibraryFilter.FromSettings(settings);
        settings.Hidden = true;
        settings.Genre.Ids[0] = Id(20);

        var service = new LibraryService(new FakeReader(other, selected));
        Assert.Equal([Id(1)], Ids(await service.ListAsync(new(Filter: filter))));
    }

    [Theory]
    [InlineData("""{"genreTypo":{}}""", "genreTypo")]
    [InlineData("""{"Genre":{"Text":"Adventure"}}""", "Text")]
    [InlineData("""{"Genre":{"IsSet":true}}""", "IsSet")]
    public void Named_filters_reject_unknown_or_unsupported_keys(string json, string key)
        => Assert.Contains(key, Assert.Throws<System.Text.Json.JsonException>(() => LibraryFilter.Parse(json)).Message);

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("null")]
    public void Omitted_named_filters_return_null(string? json) => Assert.Null(LibraryFilter.Parse(json));

    [Fact]
    public async Task Descending_sort_reverses_primary_null_order_but_preserves_name_and_identity_ties()
    {
        var first = Game(1, "Beta");
        var second = Game(2, "Beta");
        var third = Game(3, "Alpha");
        var noScore = Game(4, "Gamma");
        first.Value.UserScore = second.Value.UserScore = third.Value.UserScore = 80;
        var service = new LibraryService(new FakeReader(second, noScore, first, third));

        Assert.Equal([Id(4), Id(3), Id(1), Id(2)], Ids(await service.ListAsync(new(SortBy: SortOrder.UserScore))));
        Assert.Equal([Id(3), Id(1), Id(2), Id(4)], Ids(await service.ListAsync(new(SortBy: SortOrder.UserScore, Descending: true))));
    }

    [Fact]
    public async Task Grouping_orders_years_before_paging_and_retains_missing_keys_and_member_order()
    {
        var service = new LibraryService(new FakeReader(
            Game(4, "Alpha"), Game(3, "Beta", year: 2020), Game(2, "Alpha", year: 2020), Game(1, "Gamma", year: 2024)));

        var page = await service.ListAsync(new(GroupBy: LibraryGroupBy.ReleaseYear, Offset: 2, Limit: 2));

        Assert.Equal(4, page.TotalCount);
        Assert.Equal([Id(3), Id(4)], Ids(page));
        Assert.Collection(page.Groups,
            group => { Assert.Equal(2020, Assert.IsType<int>(group.Key)); Assert.Equal(Id(3), Assert.Single(group.Games).Value.Id); },
            group => { Assert.Null(group.Key); Assert.Equal(Id(4), Assert.Single(group.Games).Value.Id); });
    }

    [Fact]
    public async Task Date_filter_and_group_use_the_local_day_across_utc_midnight()
    {
        // These straddle UTC midnight and both belong to November 18 at UTC−07.
        // They also straddle midnight if the zone is applied with the wrong sign.
        var beforeUtcMidnight = Game(1, "Alpha");
        beforeUtcMidnight.Value.LastActivity = new DateTime(2026, 11, 18, 16, 0, 0, DateTimeKind.Unspecified);
        var afterUtcMidnight = Game(2, "Beta");
        afterUtcMidnight.Value.LastActivity = new DateTime(2026, 11, 19, 1, 0, 0, DateTimeKind.Utc);
        var neverPlayed = Game(3, "Gamma");
        neverPlayed.Value.LastActivity = DateTime.MinValue;
        var missingActivity = Game(4, "Delta");
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test-West", TimeSpan.FromHours(-7), "Test West", "Test West");
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 19, 6, 0, 0, TimeSpan.Zero), zone);
        var service = new LibraryService(new FakeReader(neverPlayed, afterUtcMidnight, beforeUtcMidnight, missingActivity), time);
        var filter = LibraryFilter.FromSettings(new() { LastActivity = new() { Values = [(int)PastTimeSegment.Today] } });

        var groups = (await service.ListAsync(new(GroupBy: LibraryGroupBy.LastActivity))).Groups;
        Assert.Collection(groups,
            group =>
            {
                Assert.Equal(PastTimeSegment.Today, Assert.IsType<PastTimeSegment>(group.Key));
                Assert.Equal([Id(1), Id(2)], group.Games.Select(game => game.Value.Id));
            },
            group =>
            {
                Assert.Equal(PastTimeSegment.Never, Assert.IsType<PastTimeSegment>(group.Key));
                Assert.Equal([Id(4), Id(3)], group.Games.Select(game => game.Value.Id));
            });

        Assert.Equal([Id(1), Id(2)], Ids(await service.ListAsync(new(Filter: filter))));
    }

    [Fact]
    public async Task Flat_pages_have_no_gaps_and_the_largest_valid_offset_retains_total()
    {
        var service = new LibraryService(new FakeReader(Game(3, "Gamma"), Game(2, "Beta"), Game(1, "Alpha")));

        Assert.Equal([Id(1), Id(2)], Ids(await service.ListAsync(new(Offset: 0, Limit: 2))));
        Assert.Equal([Id(3)], Ids(await service.ListAsync(new(Offset: 2, Limit: 2))));
        var pastEnd = await service.ListAsync(new(Offset: LibraryQuery.MaxOffset, Limit: LibraryQuery.MaxLimit));
        Assert.Equal(3, pastEnd.TotalCount);
        Assert.Empty(pastEnd.Games);
        Assert.Empty(pastEnd.Groups);
    }

    [Fact]
    public async Task Empty_library_returns_an_empty_page()
    {
        var service = new LibraryService(new FakeReader());
        var page = await service.ListAsync(new(GroupBy: LibraryGroupBy.Name));

        Assert.Empty(page.Games);
        Assert.Empty(page.Groups);
        Assert.Equal(0, page.TotalCount);
    }

    [Theory]
    [InlineData(123, 0, 0, 1, "SortBy", "Unknown library sort key")]
    [InlineData(0, 123, 0, 1, "GroupBy", "Unknown library group key")]
    [InlineData(0, 0, -1, 1, "Offset", "-1")]
    [InlineData(0, 0, 0, 0, "Limit", "0")]
    [InlineData(0, 0, LibraryQuery.MaxOffset + 1, 1, "Offset", "1000001")]
    [InlineData(0, 0, 0, LibraryQuery.MaxLimit + 1, "Limit", "10001")]
    public async Task Invalid_query_keys_and_bounds_fail_before_read(
        int sort, int group, int offset, int limit, string parameter, string message)
    {
        var reader = new FakeReader();
        var service = new LibraryService(reader);
        var query = new LibraryQuery(SortBy: (SortOrder)sort, GroupBy: (LibraryGroupBy)group, Offset: offset, Limit: limit);

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(parameter, () => service.ListAsync(query));
        Assert.Contains(message, error.Message);
        Assert.Equal(0, reader.ListCalls);
    }

    [Fact]
    public async Task Cancellation_before_a_request_never_reads_the_repository()
    {
        var reader = new FakeReader();
        var service = new LibraryService(reader);
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(Id(1), canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListAsync(cancellationToken: canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("Alpha", cancellationToken: canceled));
        Assert.Empty(reader.IdsRead);
        Assert.Equal(0, reader.ListCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_arriving_at_repository_completion_does_not_publish_results(bool get)
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new FakeReader { BeforeReturn = cancellation.Cancel };
        var service = new LibraryService(reader);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => get
            ? service.GetAsync(Id(1), cancellation.Token)
            : (Task)service.SearchAsync("Alpha", cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, reader.Token);
    }

    private static StoredGame Game(int id, string? name, string? sortingName = null, int? year = null)
        => new(new Game(Id(id), id.ToString())
        {
            Name = name!,
            SortingName = sortingName!,
            ReleaseDate = year is int value ? new ReleaseDate(value) : null,
        });

    private static Guid Id(int id) => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)id);

    private static IEnumerable<Guid> Ids(LibraryPage page) => page.Games.Select(game => game.Value.Id);

    private sealed class FakeReader(params StoredGame[] games) : ILibraryGameReader
    {
        public List<Guid> IdsRead { get; } = [];
        public int ListCalls { get; private set; }
        public CancellationToken Token { get; private set; }
        public Action? BeforeReturn { get; init; }

        public Task<StoredGame?> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            IdsRead.Add(id);
            Token = cancellationToken;
            BeforeReturn?.Invoke();
            return Task.FromResult(games.SingleOrDefault(game => game.Value.Id == id));
        }

        public Task<IReadOnlyList<StoredGame>> ListAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            Token = cancellationToken;
            BeforeReturn?.Invoke();
            return Task.FromResult<IReadOnlyList<StoredGame>>(games);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
