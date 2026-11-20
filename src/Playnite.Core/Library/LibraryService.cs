using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Queries;

namespace Playnite.Core.Library;

/// <summary>Headless access to detached game graphs in a SQLite library.</summary>
public sealed class LibraryService
{
    private readonly ILibraryGameReader reader;
    private readonly TimeProvider timeProvider;

    /// <summary>Uses the existing database and optional local time source for date-based queries.</summary>
    public LibraryService(LibraryDatabase database, TimeProvider? timeProvider = null)
        : this(new SqliteLibraryGameReader(database), timeProvider)
    {
    }

    internal LibraryService(ILibraryGameReader reader, TimeProvider? timeProvider = null)
    {
        this.reader = reader;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns the game with this library GUID, or null when it is absent.</summary>
    public async Task<StoredGame?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var game = await reader.GetAsync(id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return game;
    }

    /// <summary>Lists a snapshot with optional filtering, sorting, grouping and paging.</summary>
    public Task<LibraryPage> ListAsync(LibraryQuery? query = null, CancellationToken cancellationToken = default)
        => QueryAsync(null, query ?? new(), cancellationToken);

    /// <summary>Lists games whose display names match the v10 text matcher.</summary>
    /// <remarks>A null or whitespace-only term behaves like listing with the same options.</remarks>
    public Task<LibraryPage> SearchAsync(string? term, LibraryQuery? query = null, CancellationToken cancellationToken = default)
        => QueryAsync(term?.Trim(), query ?? new(), cancellationToken);

    private async Task<LibraryPage> QueryAsync(string? term, LibraryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset, nameof(query.Offset));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Offset, LibraryQuery.MaxOffset, nameof(query.Offset));
        if (query.Limit is int limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(query.Limit));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, LibraryQuery.MaxLimit, nameof(query.Limit));
        }
        if (!Enum.IsDefined(query.SortBy))
        {
            throw new ArgumentOutOfRangeException(nameof(query.SortBy), query.SortBy, "Unknown library sort key.");
        }

        var comparer = new GameComparer(query.SortBy, query.Descending);
        var dates = new QueryDates(timeProvider);
        var groupKey = GetGroupKey(query.GroupBy, dates);
        var filter = query.Filter?.CreateMatcher(dates, dates);
        var snapshot = await reader.ListAsync(cancellationToken).ConfigureAwait(false);
        var matches = new List<StoredGame>();
        foreach (var game in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TextSearch.MatchTextFilter(term, game.Value.Name, matchTargetAcronymStart: true) &&
                (filter is null || filter.Match(game.Value)))
            {
                matches.Add(game);
            }
        }

        var sorted = matches.OrderBy(game => game.Value, comparer).ThenBy(game => game.Value.Id);
        LibraryPage page;
        if (groupKey is null)
        {
            page = new(Page(sorted, query).ToArray(), matches.Count, []);
        }
        else
        {
            var groups = sorted.GroupBy(game => groupKey(game.Value));
            // The desktop view inserts a primary group sort ahead of the game's sort.
            var orderedGroups = query.GroupBy is LibraryGroupBy.ReleaseYear or LibraryGroupBy.PlayTime
                or LibraryGroupBy.UserScore or LibraryGroupBy.CriticScore or LibraryGroupBy.CommunityScore
                ? groups.OrderByDescending(group => group.Key)
                : groups.OrderBy(group => group.Key);
            var entries = orderedGroups.SelectMany(group => group.Select(game => (group.Key, Game: game)));
            var window = Page(entries, query).ToArray();
            page = new(window.Select(entry => entry.Game).ToArray(), matches.Count,
                window.GroupBy(entry => entry.Key)
                    .Select(group => new LibraryGroup(group.Key, group.Select(entry => entry.Game).ToArray()))
                    .ToArray());
        }
        cancellationToken.ThrowIfCancellationRequested();
        return page;
    }

    private static IEnumerable<T> Page<T>(IEnumerable<T> items, LibraryQuery query)
    {
        var window = items.Skip(query.Offset);
        return query.Limit is int limit ? window.Take(limit) : window;
    }

    private static Func<Game, IComparable?>? GetGroupKey(LibraryGroupBy groupBy, QueryDates dates) => groupBy switch
    {
        LibraryGroupBy.None => null,
        LibraryGroupBy.Name => game => game.GetNameGroup(),
        LibraryGroupBy.ReleaseYear => game => game.ReleaseYear,
        LibraryGroupBy.InstallationStatus => game => game.InstallationStatus,
        LibraryGroupBy.PlayTime => game => game.PlaytimeCategory,
        LibraryGroupBy.UserScore => game => game.UserScoreGroup,
        LibraryGroupBy.CriticScore => game => game.CriticScoreGroup,
        LibraryGroupBy.CommunityScore => game => game.CommunityScoreGroup,
        LibraryGroupBy.InstallSize => game => game.InstallSizeGroup,
        LibraryGroupBy.LastActivity => game => dates.GetSegment(game.LastActivity, dates.Now),
        LibraryGroupBy.RecentActivity => game => dates.GetSegment(game.RecentActivity, dates.Now),
        LibraryGroupBy.Added => game => dates.GetSegment(game.Added, dates.Now),
        LibraryGroupBy.Modified => game => dates.GetSegment(game.Modified, dates.Now),
        _ => throw new ArgumentOutOfRangeException(nameof(LibraryQuery.GroupBy), groupBy, "Unknown library group key."),
    };

    private sealed class QueryDates : IQueryClock, IDateSegmentCalculator
    {
        private static readonly DateSegmentCalculator Segments = new();
        private readonly TimeZoneInfo zone;

        public QueryDates(TimeProvider provider)
        {
            zone = provider.LocalTimeZone;
            Now = TimeZoneInfo.ConvertTimeFromUtc(provider.GetUtcNow().UtcDateTime, zone);
        }

        public DateTime Now { get; }

        public PastTimeSegment GetSegment(DateTime? value, DateTime now)
        {
            if (value is not DateTime date || date == DateTime.MinValue)
            {
                return PastTimeSegment.Never;
            }

            // Stored ticks represent UTC, including when a reader omits the DateTime kind.
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(date, DateTimeKind.Utc), zone);
            return Segments.GetSegment(local, now);
        }
    }

    private sealed class SqliteLibraryGameReader : ILibraryGameReader
    {
        private readonly LibraryDatabase database;

        public SqliteLibraryGameReader(LibraryDatabase database)
        {
            ArgumentNullException.ThrowIfNull(database);
            this.database = database;
        }

        public Task<StoredGame?> GetAsync(Guid id, CancellationToken cancellationToken)
            => database.ReadAsync(session => session.Games.GetAsync(id), cancellationToken);

        public Task<IReadOnlyList<StoredGame>> ListAsync(CancellationToken cancellationToken)
            => database.ReadAsync(session => session.Games.GetAllAsync(), cancellationToken);
    }
}

internal interface ILibraryGameReader
{
    Task<StoredGame?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredGame>> ListAsync(CancellationToken cancellationToken);
}
