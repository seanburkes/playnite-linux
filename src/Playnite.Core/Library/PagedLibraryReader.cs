using Playnite.Core.Database;
using Playnite.Core.Queries;

namespace Playnite.Core.Library;

/// <summary>
/// Reads one bounded, deterministically ordered page of the stored library in a single SQLite
/// snapshot. Ordering, <c>LIMIT</c> and <c>OFFSET</c> run in SQL; only the requested page's game
/// graphs are loaded.
/// </summary>
/// <remarks>
/// Text columns order with SQLite's BINARY collation, so a page is stable across runs and
/// locales. The final tie-breaker is the stored identity, ascending.
/// </remarks>
public sealed class PagedLibraryReader
{
    internal const string CountSql = "SELECT COUNT(*) FROM game;";

    private const string NameExpression = "COALESCE(NULLIF(game.SortingName, ''), game.Name) COLLATE NOCASE";

    private const string RecentActivityExpression = """
        (CASE WHEN game.LastActivity IS NULL THEN game.Added
              WHEN game.Added IS NULL THEN game.LastActivity
              WHEN game.LastActivity > game.Added THEN game.LastActivity
              ELSE game.Added END)
        """;

    private const string RomListExpression = """
        (SELECT COALESCE(group_concat(COALESCE(roms.Path, ''), ', '), '')
         FROM (SELECT r.Path FROM game_rom r WHERE r.GameId = game.Id ORDER BY r.Position) AS roms) COLLATE NOCASE
        """;

    private readonly IPagedLibrarySessionFactory sessionFactory;

    /// <summary>Reads pages from the supplied destination.</summary>
    public PagedLibraryReader(LibraryDatabase database)
        : this(new SqlitePagedLibrarySessionFactory(database))
    {
    }

    internal PagedLibraryReader(IPagedLibrarySessionFactory sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        this.sessionFactory = sessionFactory;
    }

    /// <summary>Returns one page of the library in the requested total order.</summary>
    public Task<LibraryPage> ListAsync(PagedLibraryQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new PagedLibraryQuery();
        Validate(query);
        return ReadPageAsync(query, cancellationToken);
    }

    private Task<LibraryPage> ReadPageAsync(PagedLibraryQuery query, CancellationToken cancellationToken)
    {
        var pageSql = PageSql(query);
        return sessionFactory.ReadAsync(async session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await session.CountAsync(cancellationToken).ConfigureAwait(false);
            var total = checked((int)count);
            var ids = await session.ReadPageIdsAsync(
                pageSql, query.Offset, query.Limit, cancellationToken).ConfigureAwait(false);

            var games = new List<StoredGame>(ids.Count);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                games.Add(await session.GetGameAsync(Guid.Parse(id), cancellationToken).ConfigureAwait(false));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new LibraryPage(games, total, []);
        }, cancellationToken);
    }

    /// <summary>Validates query bounds and the sort key before a destination is opened.</summary>
    internal static void Validate(PagedLibraryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset, nameof(query.Offset));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Offset, LibraryQuery.MaxOffset, nameof(query.Offset));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit, nameof(query.Limit));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, LibraryQuery.MaxLimit, nameof(query.Limit));
        if (!Enum.IsDefined(query.SortBy))
        {
            throw new ArgumentOutOfRangeException(nameof(query.SortBy), query.SortBy, "Unknown library sort key.");
        }
    }

    /// <summary>Builds the single page statement; stored values remain parameters.</summary>
    internal static string PageSql(PagedLibraryQuery query)
    {
        var direction = query.Descending ? " DESC" : " ASC";
        var terms = new List<string>();
        if (query.SortBy == SortOrder.Name)
        {
            terms.Add(NameExpression + direction);
        }
        else
        {
            foreach (var expression in PrimaryExpressions(query.SortBy))
            {
                terms.Add(expression + direction);
            }
            terms.Add(NameExpression + " ASC");
        }
        terms.Add("game.Id ASC");
        return $"SELECT game.Id FROM game ORDER BY {string.Join(", ", terms)} LIMIT @Limit OFFSET @Offset;";
    }

    private static string[] PrimaryExpressions(SortOrder sort) => sort switch
    {
        SortOrder.Name => [NameExpression],
        SortOrder.LastActivity => ["game.LastActivity"],
        SortOrder.ReleaseDate =>
        [
            "game.ReleaseYear IS NOT NULL",
            "CASE WHEN game.ReleaseYear = 0 THEN 1 ELSE COALESCE(game.ReleaseYear, 0) END",
            "COALESCE(game.ReleaseMonth, 1)",
            "COALESCE(game.ReleaseDay, 1)",
        ],
        SortOrder.Version => ["game.Version COLLATE NOCASE"],
        SortOrder.PlayCount => ["game.PlayCount"],
        SortOrder.Playtime => ["game.Playtime"],
        SortOrder.UserScore => ["game.UserScore"],
        SortOrder.CriticScore => ["game.CriticScore"],
        SortOrder.CommunityScore => ["game.CommunityScore"],
        SortOrder.Added => ["game.Added"],
        SortOrder.Modified => ["game.Modified"],
        SortOrder.IsInstalled => ["game.IsInstalled"],
        SortOrder.Hidden => ["game.Hidden"],
        SortOrder.Favorite => ["game.Favorite"],
        SortOrder.InstallDirectory => ["game.InstallDirectory COLLATE NOCASE"],
        SortOrder.InstallSize => ["game.InstallSize"],
        SortOrder.RecentActivity => [RecentActivityExpression],
        SortOrder.RomList => [RomListExpression],
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unknown library sort key."),
    };

    private sealed class SqlitePagedLibrarySessionFactory(LibraryDatabase database) : IPagedLibrarySessionFactory
    {
        public Task<T> ReadAsync<T>(
            Func<IPagedLibrarySession, Task<T>> read, CancellationToken cancellationToken)
            => database.ReadAsync(session => read(new SqlitePagedLibrarySession(session)), cancellationToken);
    }

    private sealed class SqlitePagedLibrarySession(LibrarySession session) : IPagedLibrarySession
    {
        public Task<long> CountAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return session.QuerySingleOrDefaultAsync<long>(CountSql);
        }

        public async Task<IReadOnlyList<string>> ReadPageIdsAsync(
            string sql, int offset, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = await session.QueryAsync<string>(sql, new { Offset = offset, Limit = limit })
                .ConfigureAwait(false);
            return ids.ToArray();
        }

        public async Task<StoredGame> GetGameAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await session.Games.GetAsync(id).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Game {id} disappeared from the read snapshot.");
        }
    }
}

internal interface IPagedLibrarySessionFactory
{
    Task<T> ReadAsync<T>(Func<IPagedLibrarySession, Task<T>> read, CancellationToken cancellationToken);
}

internal interface IPagedLibrarySession
{
    Task<long> CountAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ReadPageIdsAsync(string sql, int offset, int limit, CancellationToken cancellationToken);
    Task<StoredGame> GetGameAsync(Guid id, CancellationToken cancellationToken);
}
