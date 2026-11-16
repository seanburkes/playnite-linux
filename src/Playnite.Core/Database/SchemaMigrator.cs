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
            if (version == 3)
            {
                await RequireCanonicalIdentitiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            }
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

    // Version 3 adds the identity triggers, so rows written before it must satisfy the same rule.
    // Only writes outside these repositories can produce a non-canonical identity; refusing the
    // upgrade keeps the destination readable and leaves the decision to repair or discard it with
    // its owner instead of silently rewriting references.
    private static async Task RequireCanonicalIdentitiesAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        CommandDefinition Command(string sql) => new(sql, transaction: transaction, cancellationToken: cancellationToken);
        foreach (var table in LibrarySchema.MetadataTables)
        {
            await RequireCanonicalIdentitiesAsync(connection, Command($"SELECT Id FROM {table};"), $"{table}.Id").ConfigureAwait(false);
        }
        await RequireCanonicalIdentitiesAsync(connection, Command("SELECT EmulatorId FROM game_action;"), "game_action.EmulatorId").ConfigureAwait(false);
    }

    private static async Task RequireCanonicalIdentitiesAsync(SqliteConnection connection,
        CommandDefinition query, string column)
    {
        var values = await connection.QueryAsync<string>(query).ConfigureAwait(false);
        if (values.FirstOrDefault(value => !StorageValues.IsCanonicalIdentity(value)) is { } invalid)
        {
            throw new InvalidDataException(
                $"Schema upgrade to version 3 refused: {column} contains the non-canonical identity '{invalid}'. " +
                "This destination is only writable through the library repositories; repair or discard it instead of upgrading.");
        }
    }
}
