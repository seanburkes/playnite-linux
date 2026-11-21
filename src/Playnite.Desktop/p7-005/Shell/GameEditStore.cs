using Playnite.Core.Database;

namespace Playnite.Desktop.Shell;

/// <summary>
/// p7-005's second, instance-owned wrapper over the app-owned XDG library destination.
/// A <see cref="LibraryDatabase"/> holds no open connection between calls: every read and write
/// opens and closes its own connection, and no write is nested inside another flow. The p7-004
/// view-model and its private database are never reached, and no static service is introduced.
/// </summary>
public sealed class GameEditStore
{
    private readonly LibraryDatabase database;

    private GameEditStore(LibraryDatabase database) => this.database = database;

    public static async Task<GameEditStore> OpenAsync(
        string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var database = await LibraryDatabase.OpenAsync(databasePath, cancellationToken).ConfigureAwait(false);
        return new GameEditStore(database);
    }

    /// <summary>Reads one complete game graph, or null when the identity is absent.</summary>
    public Task<StoredGame?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => database.ReadAsync(session => session.Games.GetAsync(id), cancellationToken);

    /// <summary>Atomically replaces one complete game graph. Never nested in another write.</summary>
    public async Task UpdateAsync(StoredGame game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        await database.WriteAsync(async session =>
        {
            await session.Games.UpdateAsync(game).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }
}
