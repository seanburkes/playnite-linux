using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Serialization;

namespace Playnite.Core.Import;

/// <summary>One decision for every source document, including explicitly unsupported records.</summary>
public sealed record MigrationDecision(string File, string Collection, string Identity, string Outcome, string Reason);

/// <summary>Auditable migration plan/result. Documents not imported are retained in the intermediate.</summary>
public sealed record MigrationReport(int Version, string SourceHash, string SourceManifestHash, int ExpectedSteam, int ImportedSteam,
    bool DryRun, IReadOnlyList<MigrationDecision> Decisions, IReadOnlyList<AssetResult> Assets)
{
    public int MissingAssets => Assets.Count(a => a.Outcome == "missing");
    public int UnreadableAssets => Assets.Count(a => a.Outcome == "unreadable");
    public int EmptyAssets => Assets.Count(a => a.Outcome == "empty");
}

/// <summary>Consumes JSON v1, never LiteDB. Publishes only a verified, closed SQLite file.</summary>
public static class LibraryMigration
{
    private const string Steam = "cb91dfc9-b977-43bf-8e70-55f46e410fab";
    private sealed record Row(string File, string Collection, string Identity, JObject Data);

    /// <summary>Plans without writes, or publishes a verified artifact; replacing an unreceipted library requires explicit consent.</summary>
    public static async Task<MigrationReport> RunAsync(string intermediate, string sourceDirectory, string destination, bool dryRun = false,
        Action<string>? checkpoint = null, CancellationToken cancellationToken = default, bool replaceExisting = false)
    {
        if (!Path.IsPathFullyQualified(sourceDirectory)) throw new ArgumentException("Source must be absolute.", nameof(sourceDirectory));
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("Destination must be absolute.", nameof(destination));
        sourceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        destination = Path.GetFullPath(destination);
        RequirePlainDirectory(sourceDirectory);
        RequirePlainDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(destination)) throw new IOException("Destination is an existing directory, not a database file.");
        var artifacts = new[] { destination, destination + ".assets", destination + ".import", destination + ".import.lock", destination + ".import-report.json", destination + ".publication.lock" };
        if (artifacts.Any(path => path == sourceDirectory || path.StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)) ||
            sourceDirectory.StartsWith(destination + ".import" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || sourceDirectory == Path.GetPathRoot(sourceDirectory))
            throw new ArgumentException("Destination and migration artifacts must be outside the source directory.");
        if (IsLink(destination))
            throw new IOException("Destination must not be a symbolic link.");
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new JsonTextReader(new StringReader(intermediate)) { DateParseHandling = DateParseHandling.None, MaxDepth = 128 };
        var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read() || (string?)root["format"] != "playnite-v10-library" || (int?)root["version"] != 1)
            throw new InvalidDataException("Unsupported or trailing intermediate format; expected playnite-v10-library v1.");
        var files = root["files"] as JArray ?? throw new InvalidDataException("Missing source manifest.");
        var rows = (root["records"] as JArray ?? throw new InvalidDataException("Missing records.")).Select((token, index) =>
        {
            var data = token["data"] as JObject ?? throw new InvalidDataException($"Record {index} is not an object.");
            return new Row((string?)token["file"] ?? throw new InvalidDataException("Missing provenance file."),
                (string?)token["collection"] ?? throw new InvalidDataException("Missing provenance collection."),
                data["_id"]?.Type == JTokenType.String ? (string)data["_id"]! : data["_id"]?.ToString(Formatting.None) ?? $"missing-id:{index}", data);
        }).ToArray();
        if (files.Count == 0 || files.Select(f => (string?)f["path"]).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new InvalidDataException("Empty or duplicate source manifest.");
        foreach (var row in rows)
            if (!files.Any(f => (string?)f["path"] == row.File)) throw new InvalidDataException($"Unmanifested record: {row.File}/{row.Identity}");
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToString(Formatting.None))));
        var sourceManifestHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            files.OrderBy(f => (string?)f["path"], StringComparer.Ordinal)
                .Select(f => new { path = (string?)f["path"], size = (long?)f["size"], sha256 = (string?)f["sha256"] }))));
        checkpoint?.Invoke("Read");
        var decisions = new List<MigrationDecision>();
        var games = new List<StoredGame>();
        var metadata = new List<(Row Row, DatabaseObject Value)>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (row.File == "games.db" && row.Collection == "Game")
                {
                    if (!string.Equals((string?)row.Data["PluginId"], Steam, StringComparison.OrdinalIgnoreCase))
                    {
                        decisions.Add(Decision(row, "skipped", "Non-Steam provider; document retained in intermediate."));
                        continue;
                    }
                    var game = Bind<Game>(row.Data);
                    game.IsInstalling = game.IsUninstalling = game.IsLaunching = game.IsRunning = false;
                    games.Add(new StoredGame(game, row.Data.ToString(Formatting.None)));
                    decisions.Add(Decision(row, "import", "Steam; GUID, fields, Windows paths and unknown keys preserved; transient flags reset."));
                }
                else if (BindMetadata(row) is { } value)
                {
                    metadata.Add((row, value));
                    decisions.Add(Decision(row, "import", "Named metadata and complete legacy document preserved."));
                }
                else decisions.Add(Decision(row, "skipped", "Unsupported file/collection; document retained in intermediate."));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or Serialization.SerializationException or InvalidDataException or InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidDataException($"Cannot transform {row.File}/{row.Collection}/{row.Identity}: {exception.Message}", exception);
            }
        }
        if (games.Select(g => g.Value.Id).Distinct().Count() != games.Count || metadata.Select(m => (m.Value.GetType(), m.Value.Id)).Distinct().Count() != metadata.Count)
            throw new InvalidDataException("Duplicate source identities; refusing the entire import.");
        ValidateReferences(games, metadata.Select(m => m.Value).ToArray());
        checkpoint?.Invoke("Transformed");
        var references = new List<AssetReference>();
        foreach (var row in rows.Where(r => decisions.Any(d => d.File == r.File && d.Collection == r.Collection && d.Identity == r.Identity && d.Outcome == "import")))
        {
            var fields = row.Collection == "Game" ? new[] { "Icon", "CoverImage", "BackgroundImage" } : row.Collection == "Platform" ? ["Icon", "Cover", "Background"] : [];
            foreach (var field in fields)
            {
                if ((string?)row.Data[field] is not { Length: > 0 } path) continue;
                var manifest = files.SingleOrDefault(f => (string?)f["path"] == "files/" + path.Replace('\\', '/'));
                references.Add(new(row.File, row.Collection, row.Identity, field, path, (string?)manifest?["sha256"], (long?)manifest?["size"], (string?)manifest?["status"] ?? "readable"));
            }
        }
        if (dryRun)
            return new(1, sourceHash, sourceManifestHash, games.Count, 0, true, decisions,
                await AssetCopy.RunAsync(sourceDirectory, destination + ".assets", references, dryRun: true, cancellationToken: cancellationToken).ConfigureAwait(false));
        var staging = destination + ".import";
        foreach (var artifact in new[] { destination + ".import.lock", destination + ".import-report.json", staging })
            if (IsLink(artifact))
                throw new IOException("Migration artifacts must not be symbolic links.");
        // Lock file is deliberately retained; deleting a lock inode permits concurrent owners.
        using var lease = new FileStream(destination + ".import.lock", System.IO.FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using (await DatabasePublicationLease.AcquireAsync(destination, false, cancellationToken).ConfigureAwait(false))
            await CheckDestinationAsync(destination, replaceExisting, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(staging);
        if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(staging).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Staging area must not contain symbolic links.");
        var manifestPath = Path.Combine(staging, "manifest.json");
        var ownership = new JObject { ["destination"] = destination, ["sourceHash"] = sourceHash };
        if (Directory.EnumerateFileSystemEntries(staging).Any())
        {
            if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != ownership.ToString(Formatting.None))
                throw new IOException("Staging area is not owned by this source/destination; refusing cleanup.");
        }
        await File.WriteAllTextAsync(manifestPath, ownership.ToString(Formatting.None), cancellationToken).ConfigureAwait(false);
        var reportPath = destination + ".import-report.json";
        if (File.Exists(reportPath))
        {
            File.Copy(reportPath, Path.Combine(staging, "previous-report.json"), overwrite: true);
            File.Delete(reportPath);
        }
        await File.WriteAllTextAsync(Path.Combine(staging, "source.json"), intermediate, cancellationToken).ConfigureAwait(false);
        var assets = await AssetCopy.RunAsync(sourceDirectory, destination + ".assets", references, checkpoint: checkpoint, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var asset in assets)
        {
            var reference = asset.Reference;
            var migratedReference = asset.Destination ?? $"playnite-asset://{asset.Outcome}/{Uri.EscapeDataString(reference.Path)}";
            if (reference.Collection == "Game")
            {
                var game = games.Single(g => g.Value.Id == Guid.Parse(reference.Identity)).Value;
                switch (reference.Field)
                {
                    case "Icon": game.Icon = migratedReference; break;
                    case "CoverImage": game.CoverImage = migratedReference; break;
                    case "BackgroundImage": game.BackgroundImage = migratedReference; break;
                }
            }
            else
            {
                var platform = (Platform)metadata.Single(m => m.Row.Identity == reference.Identity && m.Row.Collection == reference.Collection).Value;
                switch (reference.Field)
                {
                    case "Icon": platform.Icon = migratedReference; break;
                    case "Cover": platform.Cover = migratedReference; break;
                    case "Background": platform.Background = migratedReference; break;
                }
            }
        }
        var report = new MigrationReport(1, sourceHash, sourceManifestHash, games.Count, games.Count, false, decisions, assets);
        checkpoint?.Invoke("Copied");
        var temporary = Path.Combine(staging, "library.sqlite");
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(temporary + suffix);
        var database = await LibraryDatabase.OpenAsync(temporary, cancellationToken).ConfigureAwait(false);
        await database.WriteAsync(async session =>
        {
            foreach (var (row, value) in metadata) await AddMetadataAsync(session, value, row.Data.ToString(Formatting.None)).ConfigureAwait(false);
            foreach (var game in games)
            {
                await session.Games.AddAsync(game).ConfigureAwait(false);
                checkpoint?.Invoke("PartialWrite");
            }
            return 0;
        }, cancellationToken).ConfigureAwait(false);
        checkpoint?.Invoke("Written");
        await database.ReadAsync(async session =>
        {
            var actual = await session.Games.GetAllAsync().ConfigureAwait(false);
            if (actual.Count != games.Count) throw new InvalidDataException("Game count verification failed.");
            foreach (var expected in games)
            {
                var found = actual.SingleOrDefault(g => g.Value.Id == expected.Value.Id);
                if (found == null || JsonSerialization.ToJson(found.Value) != JsonSerialization.ToJson(expected.Value) || found.LegacyJson != expected.LegacyJson)
                    throw new InvalidDataException($"Game verification failed: {expected.Value.Id}");
            }
            foreach (var (row, value) in metadata) await VerifyMetadataAsync(session, value, row.Data.ToString(Formatting.None)).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    CREATE TABLE playnite_migration_receipt(Id INTEGER PRIMARY KEY CHECK(Id=1), SourceHash TEXT NOT NULL, SourceManifestHash TEXT NOT NULL, ReportJson TEXT NOT NULL);
                    INSERT INTO playnite_migration_receipt VALUES(1, @sourceHash, @sourceManifestHash, @reportJson);
                    """, new { sourceHash, sourceManifestHash, reportJson = System.Text.Json.JsonSerializer.Serialize(report) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (await connection.ExecuteScalarAsync<string>(new CommandDefinition("PRAGMA integrity_check;", cancellationToken: cancellationToken)).ConfigureAwait(false) != "ok" ||
                (await connection.QueryAsync(new CommandDefinition("PRAGMA foreign_key_check;", cancellationToken: cancellationToken)).ConfigureAwait(false)).Any())
                throw new InvalidDataException("SQLite integrity verification failed.");
            var checkpointResult = await connection.QuerySingleAsync<WalCheckpoint>(new CommandDefinition("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (checkpointResult.Busy != 0 || checkpointResult.Log != checkpointResult.Checkpointed)
                throw new InvalidDataException("WAL checkpoint was incomplete; refusing publication of the main file alone.");
        }
        checkpoint?.Invoke("Verified");
        var stagedReport = Path.Combine(staging, "report.json");
        await File.WriteAllTextAsync(stagedReport, System.Text.Json.JsonSerializer.Serialize(report), cancellationToken).ConfigureAwait(false);
        using (var durable = new FileStream(temporary, System.IO.FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(flushToDisk: true);
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke("BeforeSwap");
        cancellationToken.ThrowIfCancellationRequested();
        using (await DatabasePublicationLease.AcquireAsync(destination, true, cancellationToken).ConfigureAwait(false))
        {
            // All supported connections have drained, including their WAL checkpoint/close.
            // External SQLite connections do not participate in this protocol: fail closed.
            if (File.Exists(destination + "-shm") || File.Exists(destination + "-wal"))
                throw new IOException("Live SQLite sidecars remain; close external database connections before replacement.");
            await CheckDestinationAsync(destination, replaceExisting, cancellationToken).ConfigureAwait(false);
            var previous = Path.Combine(staging, "previous.sqlite");
            var existed = File.Exists(destination);
            if (existed)
            {
                File.Copy(destination, previous, overwrite: true);
                using var retained = new FileStream(previous, System.IO.FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                retained.Flush(flushToDisk: true);
            }
            var swapped = false;
            try
            {
                checkpoint?.Invoke("Publishing");
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: existed);
                swapped = true;
                checkpoint?.Invoke("Swapped");
                cancellationToken.ThrowIfCancellationRequested();
                if (!await HasReceiptAsync(destination, cancellationToken, report).ConfigureAwait(false))
                    throw new InvalidDataException("Published artifact lost its migration receipt.");
                checkpoint?.Invoke("ReceiptConfirmed");
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                // The exclusive lease hides the unconfirmed swap from supported readers.
                if (swapped)
                {
                    foreach (var sidecar in Sidecars(destination)) File.Delete(sidecar);
                    if (existed) File.Move(previous, destination, overwrite: true);
                    else File.Delete(destination);
                }
                File.Delete(reportPath);
                throw;
            }
        }
        checkpoint?.Invoke("Committed");
        checkpoint?.Invoke("BeforeReportMove");
        File.Move(stagedReport, reportPath, overwrite: true);
        return report;
    }

    private static IEnumerable<string> Sidecars(string path) => new[] { path + "-wal", path + "-shm", path + "-journal" }
        .Concat(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!)
            .Where(p => Path.GetFileName(p).StartsWith(Path.GetFileName(path) + "-mj", StringComparison.Ordinal)));

    private static async Task CheckDestinationAsync(string path, bool replaceExisting, CancellationToken cancellationToken)
    {
        var sidecars = Sidecars(path).ToArray();
        if (sidecars.Any(IsLink)) throw new IOException("Live SQLite sidecars must not be symbolic links.");
        if (File.Exists(path + "-journal") || sidecars.Skip(3).Any())
            throw new IOException("Live SQLite rollback journal or super-journal remains; close external database connections and recover the library before replacement.");
        if (!File.Exists(path)) return;
        if (await HasReceiptAsync(path, cancellationToken).ConfigureAwait(false))
            throw new IOException("Destination contains a durable migration receipt; refusing a second run.");
        if (!replaceExisting)
            throw new IOException("Destination has no durable migration receipt; use explicit --replace-existing consent (replaceExisting in the Core API). Earlier successful imports without receipts cannot be distinguished from other libraries.");
    }

    private static async Task<bool> HasReceiptAsync(string path, CancellationToken cancellationToken, MigrationReport? expectedReport = null)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // A read-only WAL connection can leave sidecars behind on close. Query-only on a
        // read-write handle preserves SQL read safety and permits SQLite's final close cleanup.
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA query_only=ON;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='playnite_migration_receipt';", cancellationToken: cancellationToken)).ConfigureAwait(false) == 0)
            return false;
        if (expectedReport != null)
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM playnite_migration_receipt WHERE Id=1 AND SourceHash=@SourceHash AND SourceManifestHash=@SourceManifestHash AND ReportJson=@reportJson;",
                new { expectedReport.SourceHash, expectedReport.SourceManifestHash, reportJson = System.Text.Json.JsonSerializer.Serialize(expectedReport) }, cancellationToken: cancellationToken)).ConfigureAwait(false) == 1;
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM playnite_migration_receipt;", cancellationToken: cancellationToken)).ConfigureAwait(false) != 0;
    }

    private static MigrationDecision Decision(Row row, string outcome, string reason) => new(row.File, row.Collection, row.Identity, outcome, reason);

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget != null ||
        (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);

    private sealed class WalCheckpoint
    {
        public int Busy { get; set; }
        public int Log { get; set; }
        public int Checkpointed { get; set; }
    }

    private static void RequirePlainDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Directory does not exist: {path}");
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic-link source/destination ancestors are not supported.");
    }

    private static T Bind<T>(JObject raw) where T : DatabaseObject
    {
        var normalized = (JObject)raw.DeepClone();
        if (!Guid.TryParse((string?)raw["_id"], out var id) || id == Guid.Empty) throw new InvalidDataException("Missing/invalid entity GUID.");
        normalized.Remove("_id");
        normalized["Id"] = id.ToString("D");
        var release = normalized["ReleaseDate"];
        normalized.Remove("ReleaseDate");
        DatabaseObject value = typeof(T).Name switch
        {
            nameof(Game) => new Game(id, (string?)raw["GameId"]!), nameof(Tag) => new Tag(id), nameof(Genre) => new Genre(id),
            nameof(Company) => new Company(id), nameof(GameFeature) => new GameFeature(id), nameof(Series) => new Series(id),
            nameof(Category) => new Category(id), nameof(GameSource) => new GameSource(id), nameof(AgeRating) => new AgeRating(id),
            nameof(Region) => new Region(id), nameof(CompletionStatus) => new CompletionStatus(id), nameof(Platform) => new Platform(id),
            _ => throw new InvalidDataException("Unsupported model type."),
        };
        JsonValueGuard.Validate(normalized, typeof(T), "$");
        var serializer = JsonSerializer.Create(new JsonSerializerSettings { ContractResolver = JsonResolver.Global, TypeNameHandling = TypeNameHandling.None, DateParseHandling = DateParseHandling.None, MaxDepth = 128 });
        using (var input = normalized.CreateReader()) serializer.Populate(input, value);
        if (value is Game game && release != null && release.Type != JTokenType.Null)
        {
            if (release.Type != JTokenType.String || !ReleaseDate.TryDeserialize((string)release!, out var date)) throw new InvalidDataException("Invalid v10 release date.");
            game.ReleaseDate = date;
        }
        return (T)value;
    }

    private static DatabaseObject? BindMetadata(Row row) => (row.File, row.Collection) switch
    {
        ("tags.db", "Tag") => Bind<Tag>(row.Data), ("genres.db", "Genre") => Bind<Genre>(row.Data),
        ("companies.db", "Company") => Bind<Company>(row.Data), ("features.db", "GameFeature") => Bind<GameFeature>(row.Data),
        ("series.db", "Series") => Bind<Series>(row.Data), ("categories.db", "Category") => Bind<Category>(row.Data),
        ("sources.db", "GameSource") => Bind<GameSource>(row.Data), ("ageratings.db", "AgeRating") => Bind<AgeRating>(row.Data),
        ("regions.db", "Region") => Bind<Region>(row.Data), ("completionstatuses.db", "CompletionStatus") => Bind<CompletionStatus>(row.Data),
        ("platforms.db", "Platform") => Bind<Platform>(row.Data), _ => null,
    };

    private static void ValidateReferences(IEnumerable<StoredGame> games, IReadOnlyList<DatabaseObject> metadata)
    {
        void Require<T>(IEnumerable<Guid>? ids, Guid game) where T : DatabaseObject
        {
            foreach (var id in ids ?? [])
                if (!metadata.OfType<T>().Any(m => m.Id == id)) throw new InvalidDataException($"Game {game}: missing {typeof(T).Name} {id}.");
        }
        foreach (var item in games)
        {
            var g = item.Value;
            Require<Tag>(g.TagIds, g.Id); Require<Genre>(g.GenreIds, g.Id); Require<Platform>(g.PlatformIds, g.Id);
            Require<Company>(g.PublisherIds, g.Id); Require<Company>(g.DeveloperIds, g.Id); Require<Category>(g.CategoryIds, g.Id);
            Require<GameFeature>(g.FeatureIds, g.Id); Require<Series>(g.SeriesIds, g.Id); Require<AgeRating>(g.AgeRatingIds, g.Id); Require<Region>(g.RegionIds, g.Id);
            if (g.SourceId != Guid.Empty) Require<GameSource>([g.SourceId], g.Id);
            if (g.CompletionStatusId != Guid.Empty) Require<CompletionStatus>([g.CompletionStatusId], g.Id);
        }
    }

    private static Task AddMetadataAsync(LibrarySession session, DatabaseObject value, string legacy) => value switch
    {
        Tag v => session.Metadata<Tag>().AddAsync(new(v, legacy)), Genre v => session.Metadata<Genre>().AddAsync(new(v, legacy)),
        Company v => session.Metadata<Company>().AddAsync(new(v, legacy)), GameFeature v => session.Metadata<GameFeature>().AddAsync(new(v, legacy)),
        Series v => session.Metadata<Series>().AddAsync(new(v, legacy)), Category v => session.Metadata<Category>().AddAsync(new(v, legacy)),
        GameSource v => session.Metadata<GameSource>().AddAsync(new(v, legacy)), AgeRating v => session.Metadata<AgeRating>().AddAsync(new(v, legacy)),
        Region v => session.Metadata<Region>().AddAsync(new(v, legacy)), CompletionStatus v => session.Metadata<CompletionStatus>().AddAsync(new(v, legacy)),
        Platform v => session.Metadata<Platform>().AddAsync(new(v, legacy)), _ => throw new InvalidDataException("Unsupported metadata."),
    };

    private static async Task VerifyMetadataAsync(LibrarySession session, DatabaseObject value, string legacy)
    {
        async Task Check<T>(T expected) where T : DatabaseObject
        {
            var actual = await session.Metadata<T>().GetAsync(expected.Id).ConfigureAwait(false);
            if (actual == null || actual.LegacyJson != legacy || JsonSerialization.ToJson(actual.Value) != JsonSerialization.ToJson(expected))
                throw new InvalidDataException($"Metadata verification failed: {typeof(T).Name}/{expected.Id}");
        }
        switch (value)
        {
            case Tag v: await Check(v).ConfigureAwait(false); break; case Genre v: await Check(v).ConfigureAwait(false); break;
            case Company v: await Check(v).ConfigureAwait(false); break; case GameFeature v: await Check(v).ConfigureAwait(false); break;
            case Series v: await Check(v).ConfigureAwait(false); break; case Category v: await Check(v).ConfigureAwait(false); break;
            case GameSource v: await Check(v).ConfigureAwait(false); break; case AgeRating v: await Check(v).ConfigureAwait(false); break;
            case Region v: await Check(v).ConfigureAwait(false); break; case CompletionStatus v: await Check(v).ConfigureAwait(false); break;
            case Platform v: await Check(v).ConfigureAwait(false); break;
            default: throw new InvalidDataException("Unsupported metadata.");
        }
    }
}
