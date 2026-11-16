using Dapper;
using Microsoft.Data.Sqlite;
using Playnite.Core.Models;

namespace Playnite.Core.Database;

/// <summary>
/// A single snapshot or write transaction. Use serially, only within its owning callback.
/// SQL is only reachable through the transaction-bound methods below; no raw connection escapes.
/// </summary>
public sealed class LibrarySession
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly CancellationToken cancellationToken;

    internal LibrarySession(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.cancellationToken = cancellationToken;
        Games = new GameRepository(this);
    }

    /// <summary>Games, including their ordered dependent rows.</summary>
    public GameRepository Games { get; }

    /// <summary>Named metadata. Only v10 library metadata model types are accepted.</summary>
    public MetadataRepository<T> Metadata<T>() where T : DatabaseObject => new(this);

    /// <summary>Executes SQL bound to this callback's transaction and cancellation token.</summary>
    internal Task<int> ExecuteAsync(string sql, object? parameters = null)
        => connection.ExecuteAsync(Command(sql, parameters));

    /// <summary>Queries rows bound to this callback's transaction and cancellation token.</summary>
    internal Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null)
        => connection.QueryAsync<T>(Command(sql, parameters));

    /// <summary>Queries at most one row bound to this callback's transaction and cancellation token.</summary>
    internal Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null)
        => connection.QuerySingleOrDefaultAsync<T>(Command(sql, parameters));

    internal async Task AtomicAsync(Func<Task> write)
    {
        await ExecuteAsync("SAVEPOINT game_write;").ConfigureAwait(false);
        try
        {
            await write().ConfigureAwait(false);
            await ExecuteAsync("RELEASE game_write;").ConfigureAwait(false);
        }
        catch
        {
            // Cleanup must also run after cancellation, without the cancelled token.
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK TO game_write; RELEASE game_write;", transaction: transaction)).ConfigureAwait(false);
            throw;
        }
    }

    private CommandDefinition Command(string sql, object? parameters)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(sql, parameters, transaction, cancellationToken: cancellationToken);
    }
}
