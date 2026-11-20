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

    private const string NameExpression = "COALESCE(NULLIF(game.SortingName, ''), game.Name)";

    private const string RecentActivityExpression = """
        (CASE WHEN game.LastActivity IS NULL THEN game.Added
              WHEN game.Added IS NULL THEN game.LastActivity
              WHEN game.LastActivity > game.Added THEN game.LastActivity
              ELSE game.Added END)
        """;

    private const string RomListExpression = """
        (SELECT COALESCE(group_concat(COALESCE(roms.Path, ''), ', '), '')
         FROM (SELECT r.Path FROM game_rom r WHERE r.GameId = game.Id ORDER BY r.Position) AS roms)
        """;

    private readonly LibraryDatabase database;

    /// <summary>Reads pages from the supplied destination.</summary>
    public PagedLibraryReader(LibraryDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    /// <summary>Returns one page of the library in the requested total order.</summary>
    public Task<LibraryPage> ListAsync(PagedLibraryQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new PagedLibraryQuery();
        Validate(query);
        return ReadPageAsync(query, cancellationToken);
    }

    private Task<LibraryPage> ReadPageAsync(PagedLibraryQuery query, CancellationToken cancellationToken)
        => database.ReadAsync(async session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await session.QuerySingleOrDefaultAsync<long>(CountSql).ConfigureAwait(false);
            var total = checked((int)count);
            var ids = await session.QueryAsync<string>(
                PageSql(query), new { query.Offset, query.Limit }).ConfigureAwait(false);

            var games = new List<StoredGame>(query.Limit);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var game = await session.Games.GetAsync(Guid.Parse(id)).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Game {id} disappeared from the read snapshot.");
                games.Add(game);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new LibraryPage(games, total, []);
        }, cancellationToken);

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
            "COALESCE(game.ReleaseYear, 0)",
            "COALESCE(game.ReleaseMonth, 1)",
            "COALESCE(game.ReleaseDay, 1)",
        ],
        SortOrder.Version => ["game.Version"],
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
        SortOrder.InstallDirectory => ["game.InstallDirectory"],
        SortOrder.InstallSize => ["game.InstallSize"],
        SortOrder.RecentActivity => [RecentActivityExpression],
        SortOrder.RomList => [RomListExpression],
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unknown library sort key."),
    };
}
