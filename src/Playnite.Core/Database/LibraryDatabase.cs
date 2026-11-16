using Dapper;
using Microsoft.Data.Sqlite;

namespace Playnite.Core.Database;

/// <summary>
/// A SQLite library destination. Each operation owns and closes its connection.
/// Operations are not reentrant: a <see cref="WriteAsync{T}"/> callback must not start another
/// write on the same destination, because that inner write would wait on its own outer writer
/// until the busy timeout. That rule covers a write started in the background from inside the
/// callback, including fire-and-forget work: start follow-up writes after the callback returns.
/// </summary>
public sealed class LibraryDatabase
{
    private readonly string path;
    private readonly string connectionString;

    private LibraryDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A library database path must be absolute.", nameof(path));
        }

        this.path = CanonicalPath(path);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.path,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    // The nesting guard compares destinations, so different spellings of one file must share a
    // key: absolute normalisation, then each existing directory component and the file itself
    // resolve their symlink target. Hard links have no managed identity API and cannot be
    // detected here; SQLite's busy timeout remains the backstop for that spelling.
    private static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (directory != null)
        {
            full = Path.Combine(ResolveDirectory(directory), Path.GetFileName(full));
        }
        if (File.Exists(full))
        {
            full = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        return full;
    }

    private static string ResolveDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
        }
        return current;
    }

    /// <summary>Current forward-only destination schema version.</summary>
    public const int SchemaVersion = 3;

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

    /// <summary>
    /// Reads a consistent snapshot. Repository objects must not escape the callback.
    /// A write started from inside a read is allowed and is not part of the snapshot, so the
    /// outer read keeps seeing the state it started with; a read started from inside a write
    /// sees only committed state. Neither blocks.
    /// </summary>
    public Task<T> ReadAsync<T>(Func<LibrarySession, Task<T>> read, CancellationToken cancellationToken = default)
        => ExecuteAsync(read, false, cancellationToken);

    /// <summary>
    /// Commits all callback writes together; exceptions or cancellation roll them back.
    /// Nesting another write for the same destination inside this callback fails immediately
    /// with <see cref="InvalidOperationException"/> rather than stalling on the outer writer.
    /// </summary>
    public Task<T> WriteAsync<T>(Func<LibrarySession, Task<T>> write, CancellationToken cancellationToken = default)
        => ExecuteAsync(write, true, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<LibrarySession, Task<T>> operation, bool write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        using var nesting = write ? WriteNestingGuard.Enter(path) : default;
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

/// <summary>
/// Tracks the destination writes active in the current async flow. Two instances opened on the
/// same file share one SQLite writer, so a nested write through either instance would wait on
/// the outer transaction until the busy timeout and then fail; refusing it fails fast instead.
/// </summary>
internal static class WriteNestingGuard
{
    private static readonly AsyncLocal<string[]?> Active = new();

    internal static IDisposable? Enter(string destination)
    {
        var active = Active.Value ?? [];
        if (Array.IndexOf(active, destination) >= 0)
        {
            throw new InvalidOperationException(
                $"WriteAsync is already active for '{destination}'. A nested write would wait on its own outer writer; run it in the outer callback or after it completes.");
        }

        Active.Value = [.. active, destination];
        return new Scope(active);
    }

    private sealed class Scope(string[] previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
