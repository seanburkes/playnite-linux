using Dapper;
using Microsoft.Data.Sqlite;

namespace Playnite.Core.Database;

/// <summary>A SQLite library destination. Each operation owns and closes its connection.</summary>
public sealed class LibraryDatabase
{
    private readonly string connectionString;

    private LibraryDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A library database path must be absolute.", nameof(path));
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    /// <summary>Current forward-only destination schema version.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Opens or upgrades a destination, refusing unknown schema versions.</summary>
    public static async Task<LibraryDatabase> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var database = new LibraryDatabase(path);
        await using var connection = await database.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode=WAL;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        await SchemaMigrator.ApplyAsync(connection, SchemaVersion, cancellationToken).ConfigureAwait(false);
        return database;
    }

    private async Task<SqliteConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition("PRAGMA busy_timeout=5000;", cancellationToken: cancellationToken)).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads a consistent snapshot. Repository objects must not escape the callback.</summary>
    public Task<T> ReadAsync<T>(Func<LibrarySession, Task<T>> read, CancellationToken cancellationToken = default)
        => ExecuteAsync(read, false, cancellationToken);

    /// <summary>Commits all callback writes together; exceptions or cancellation roll them back.</summary>
    public Task<T> WriteAsync<T>(Func<LibrarySession, Task<T>> write, CancellationToken cancellationToken = default)
        => ExecuteAsync(write, true, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<LibrarySession, Task<T>> operation, bool write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!write)
        {
            await connection.ExecuteAsync(new CommandDefinition("PRAGMA query_only=ON;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        // Acquire the writer lock before reading any state; no deferred read-to-write upgrade.
        await using var transaction = connection.BeginTransaction(deferred: !write);
        var session = new LibrarySession(connection, transaction, cancellationToken);
        var result = await operation(session).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
