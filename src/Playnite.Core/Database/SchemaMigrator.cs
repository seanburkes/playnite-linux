using Dapper;
using Microsoft.Data.Sqlite;

namespace Playnite.Core.Database;

internal enum MigrationStage { BeforeSql, AfterSql, AfterVersion, AfterCommit }

internal static class SchemaMigrator
{
    internal static async Task ApplyAsync(SqliteConnection connection, int target, CancellationToken cancellationToken,
        Action<int, MigrationStage>? checkpoint = null)
    {
        if (target is < 1 or > LibraryDatabase.SchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = connection.BeginTransaction(deferred: false);
            CommandDefinition Command(string sql, object? args = null) => new(sql, args, transaction, cancellationToken: cancellationToken);
            await connection.ExecuteAsync(Command("""
                CREATE TABLE IF NOT EXISTS schema_version (
                    version INTEGER PRIMARY KEY CHECK(version > 0),
                    applied_at TEXT NOT NULL);
                """)).ConfigureAwait(false);
            var versions = (await connection.QueryAsync<int>(Command("SELECT version FROM schema_version ORDER BY version;" )).ConfigureAwait(false)).ToArray();
            if (!versions.SequenceEqual(Enumerable.Range(1, versions.Length)) || versions.LastOrDefault() > target)
            {
                throw new InvalidDataException("Unknown or non-contiguous library schema; downgrade is not supported.");
            }

            var version = versions.Length + 1;
            if (version > target)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            checkpoint?.Invoke(version, MigrationStage.BeforeSql);
            await connection.ExecuteAsync(Command(version switch
            {
                1 => LibrarySchema.Version1,
                2 => LibrarySchema.Version2,
                3 => LibrarySchema.Version3,
                _ => throw new InvalidDataException($"No migration is defined for schema version {version}."),
            })).ConfigureAwait(false);
            checkpoint?.Invoke(version, MigrationStage.AfterSql);
            await connection.ExecuteAsync(Command("INSERT INTO schema_version(version, applied_at) VALUES (@Version, strftime('%Y-%m-%dT%H:%M:%f+00:00','now'));", new { Version = version })).ConfigureAwait(false);
            checkpoint?.Invoke(version, MigrationStage.AfterVersion);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(version, MigrationStage.AfterCommit);
        }
    }
}
