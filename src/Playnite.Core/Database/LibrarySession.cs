using Dapper;
using Microsoft.Data.Sqlite;
using Playnite.Core.Models;

namespace Playnite.Core.Database;

/// <summary>A single snapshot or write transaction. Use serially, only within its owning callback.</summary>
public sealed class LibrarySession
{
    internal SqliteConnection Connection { get; }
    private readonly SqliteTransaction transaction;
    private readonly CancellationToken cancellationToken;

    internal LibrarySession(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        Connection = connection;
        this.transaction = transaction;
        this.cancellationToken = cancellationToken;
        Games = new GameRepository(this);
    }

    /// <summary>Games, including their ordered dependent rows.</summary>
    public GameRepository Games { get; }

    /// <summary>Named metadata. Only v10 library metadata model types are accepted.</summary>
    public MetadataRepository<T> Metadata<T>() where T : DatabaseObject => new(this);

    internal CommandDefinition Command(string sql, object? parameters = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(sql, parameters, transaction, cancellationToken: cancellationToken);
    }

    internal async Task AtomicAsync(Func<Task> write)
    {
        await Connection.ExecuteAsync(Command("SAVEPOINT game_write;")).ConfigureAwait(false);
        try
        {
            await write().ConfigureAwait(false);
            await Connection.ExecuteAsync(Command("RELEASE game_write;")).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup must also run after cancellation, without the cancelled token.
            await Connection.ExecuteAsync(new CommandDefinition("ROLLBACK TO game_write; RELEASE game_write;", transaction: transaction)).ConfigureAwait(false);
            throw;
        }
    }
}
