using Dapper;
using Microsoft.Data.Sqlite;
using Playnite.Core.Database;
using Playnite.Core.Import;
using Playnite.Core.Models;
using Xunit;

namespace Playnite.Core.Tests.Import;

public sealed partial class LibraryMigrationTests
{
    private Task<MigrationReport> ReplaceAsync(string envelope, Action<string>? checkpoint = null, CancellationToken cancellationToken = default)
        => LibraryMigration.RunAsync(envelope, Fixture(), Destination, checkpoint: checkpoint, cancellationToken: cancellationToken, replaceExisting: true);

    private async Task<LibraryDatabase> SeedLiveAsync()
    {
        var database = await LibraryDatabase.OpenAsync(Destination);
        await database.WriteAsync(async session =>
        {
            await session.Games.AddAsync(new(new Game(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "old") { Name = "Complete old library" }, "{}"));
            return 0;
        });
        return database;
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.DurableReceipt")]
    [Trait("Invariant", "LibraryImport.RepeatRunSafe")]
    [Trait("Invariant", "LibraryImport.NoCommitWithoutVerify")]
    public async Task Replacement_embeds_source_receipt_and_refuses_without_external_audit_artifacts()
    {
        await SeedLiveAsync();
        var old = await File.ReadAllBytesAsync(Destination);
        var source = Snapshot(Fixture());
        var report = await ReplaceAsync(await ReadEnvelope());
        Assert.Equal(old, await File.ReadAllBytesAsync(Path.Combine(Destination + ".import", "previous.sqlite")));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(report), await File.ReadAllTextAsync(Destination + ".import-report.json"));
        using (var connection = new SqliteConnection($"Data Source={Destination};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            Assert.Equal(report.SourceHash, await connection.ExecuteScalarAsync<string>("SELECT SourceHash FROM playnite_migration_receipt WHERE Id=1;"));
            Assert.Equal(report.SourceManifestHash, await connection.ExecuteScalarAsync<string>("SELECT SourceManifestHash FROM playnite_migration_receipt WHERE Id=1;"));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(report), await connection.ExecuteScalarAsync<string>("SELECT ReportJson FROM playnite_migration_receipt WHERE Id=1;"));
        }
        File.Delete(Destination + ".import-report.json");
        Directory.Delete(Destination + ".import", recursive: true);
        var published = await File.ReadAllBytesAsync(Destination);
        var envelope = await ReadEnvelope();
        var error = await Assert.ThrowsAsync<IOException>(() => ReplaceAsync(envelope));
        Assert.Contains("durable migration receipt", error.Message);
        Assert.Equal(published, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(source, Snapshot(Fixture()));
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Transformed")]
    [InlineData("Copied")]
    [InlineData("PartialWrite")]
    [InlineData("Written")]
    [InlineData("Verified")]
    [InlineData("BeforeSwap")]
    [InlineData("Publishing")]
    [InlineData("Swapped")]
    [InlineData("ReceiptConfirmed")]
    [InlineData("Committed")]
    [Trait("Invariant", "LibraryImport.SourceUnchanged")]
    [Trait("Invariant", "LibraryImport.AtomicLive")]
    [Trait("Invariant", "LibraryImport.CrashSafe")]
    [Trait("Invariant", "LibraryImport.CleanResumable")]
    [Trait("Invariant", "LibraryImport.DurableReceipt")]
    public async Task SIGKILL_replacing_live_at_every_state_leaves_whole_old_or_receipted_new_and_can_resume(string state)
    {
        var database = await SeedLiveAsync();
        var old = await File.ReadAllBytesAsync(Destination);
        var source = Snapshot(Fixture());
        using var process = StartCli("--replace-existing", "--pause-at=" + state);
        var output = process.StandardOutput.ReadToEndAsync();
        try
        {
            Assert.Equal("checkpoint:" + state, await process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        await output;
        Assert.NotEqual(0, process.ExitCode);
        using var matrix = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root, "tests", "golden", "import-replacement", "crash-matrix.json")));
        Assert.Equal(11, matrix.RootElement.EnumerateObject().Count());
        var isNew = matrix.RootElement.GetProperty(state).GetString() == "new";
        if (!isNew) Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
        var games = await database.ReadAsync(s => s.Games.GetAllAsync());
        Assert.Equal(isNew ? new[] { "220", "570", "400" } : new[] { "old" }, games.Select(g => g.Value.GameId));
        if (isNew)
        {
            File.Delete(Destination + ".import-report.json");
            var envelope = await ReadEnvelope();
            await Assert.ThrowsAsync<IOException>(() => ReplaceAsync(envelope));
        }
        else
        {
            Assert.Equal(3, (await ReplaceAsync(await ReadEnvelope())).ImportedSteam);
            Assert.Equal(3, (await database.ReadAsync(s => s.Games.GetAllAsync())).Count);
        }
        Assert.Equal(source, Snapshot(Fixture()));
    }

    [Theory]
    [InlineData("BeforeSwap", false)]
    [InlineData("Publishing", false)]
    [InlineData("Swapped", false)]
    [InlineData("ReceiptConfirmed", false)]
    [InlineData("BeforeSwap", true)]
    [InlineData("Publishing", true)]
    [InlineData("Swapped", true)]
    [InlineData("ReceiptConfirmed", true)]
    [Trait("Invariant", "LibraryImport.AtomicLive")]
    [Trait("Invariant", "LibraryImport.BackupBeforeWrite")]
    [Trait("Invariant", "LibraryImport.CleanResumable")]
    public async Task Publication_window_failure_or_cancellation_restores_exact_pre_run_bytes(string state, bool cancellation)
    {
        var database = await SeedLiveAsync();
        var old = await File.ReadAllBytesAsync(Destination);
        var source = Snapshot(Fixture());
        using var cancel = new CancellationTokenSource();
        var envelope = await ReadEnvelope();
        var failure = await Record.ExceptionAsync(() => ReplaceAsync(envelope, checkpoint: s =>
        {
            if (s != state) return;
            if (cancellation) cancel.Cancel();
            else throw new IOException("Injected publication failure");
        }, cancellationToken: cancel.Token));
        if (cancellation) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.Equal("Injected publication failure", Assert.IsType<IOException>(failure).Message);
        // Compare before reopening SQLite: recovery must restore bytes, not just equivalent rows.
        Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
        Assert.False(File.Exists(Destination + ".import-report.json"));
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) Assert.False(File.Exists(Destination + suffix));
        Assert.Equal("old", Assert.Single(await database.ReadAsync(s => s.Games.GetAllAsync())).Value.GameId);
        Assert.Equal(3, (await ReplaceAsync(envelope)).ImportedSteam);
        Assert.Equal(source, Snapshot(Fixture()));
    }

    [Theory]
    [InlineData("Publishing")]
    [InlineData("Swapped")]
    [InlineData("ReceiptConfirmed")]
    [Trait("Invariant", "LibraryImport.ReaderNeverPartial")]
    [Trait("Invariant", "LibraryImport.AtomicLive")]
    public async Task Reader_opening_inside_actual_cross_process_swap_window_waits_then_reads_complete_new(string state)
    {
        var database = await SeedLiveAsync();
        using var process = StartCli("--replace-existing", "--pause-at=" + state);
        var output = process.StandardOutput.ReadToEndAsync();
        try
        {
            Assert.Equal("checkpoint:" + state, await process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            var openingContended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DatabasePublicationLease.Contention.Value = (path, exclusive) =>
            {
                if (path == Destination && !exclusive) openingContended.TrySetResult();
            };
            var opening = LibraryDatabase.OpenAsync(Destination);
            await openingContended.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(opening.IsCompleted, "The actual OS-lock attempt must contend inside the held publication window.");
            using var cancel = new CancellationTokenSource();
            var cancelledContended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DatabasePublicationLease.Contention.Value = (path, exclusive) =>
            {
                if (path == Destination && !exclusive) cancelledContended.TrySetResult();
            };
            var cancelled = database.ReadAsync(s => s.Games.GetAllAsync(), cancel.Token);
            await cancelledContended.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            await process.StandardInput.WriteLineAsync();
            await process.StandardInput.FlushAsync();
            var opened = await opening.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(new[] { "220", "570", "400" }, (await opened.ReadAsync(s => s.Games.GetAllAsync())).Select(g => g.Value.GameId));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
            await output;
        }
        finally
        {
            DatabasePublicationLease.Contention.Value = null;
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.ReaderNeverPartial")]
    public async Task Existing_reader_finishes_old_snapshot_before_replacement_can_close_and_rename_its_WAL_database()
    {
        var database = await SeedLiveAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRead = database.ReadAsync(async session =>
        {
            Assert.Equal("old", Assert.Single(await session.Games.GetAllAsync()).Value.GameId);
            entered.SetResult();
            await release.Task;
            Assert.Equal("old", Assert.Single(await session.Games.GetAllAsync()).Value.GameId);
            return 0;
        });
        await entered.Task;
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DatabasePublicationLease.Contention.Value = (path, exclusive) =>
        {
            if (path == Destination && exclusive) contended.TrySetResult();
        };
        var replacement = ReplaceAsync(await ReadEnvelope());
        try
        {
            await contended.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(replacement.IsCompleted);
        }
        finally { DatabasePublicationLease.Contention.Value = null; release.TrySetResult(); }
        await oldRead;
        Assert.Equal(3, (await replacement.WaitAsync(TimeSpan.FromSeconds(30))).ImportedSteam);
        Assert.Equal(3, (await database.ReadAsync(s => s.Games.GetAllAsync())).Count);
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.AtomicLive")]
    public async Task Real_rename_failure_retains_byte_identical_old_library_and_can_restart()
    {
        await SeedLiveAsync();
        var old = await File.ReadAllBytesAsync(Destination);
        var envelope = await ReadEnvelope();
        await Assert.ThrowsAsync<FileNotFoundException>(() => ReplaceAsync(envelope, checkpoint: state =>
        {
            if (state == "Publishing") File.Delete(Path.Combine(Destination + ".import", "library.sqlite"));
        }));
        Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(old, await File.ReadAllBytesAsync(Path.Combine(Destination + ".import", "previous.sqlite")));
        Assert.Equal(3, (await ReplaceAsync(envelope)).ImportedSteam);
    }

    [Theory]
    [InlineData("SourceHash")]
    [InlineData("SourceManifestHash")]
    [InlineData("ReportJson")]
    [Trait("Invariant", "LibraryImport.DurableReceipt")]
    public async Task Receipt_confirmation_failure_restores_byte_identical_old_library_before_readers_resume(string field)
    {
        await SeedLiveAsync();
        var old = await File.ReadAllBytesAsync(Destination);
        var envelope = await ReadEnvelope();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReplaceAsync(envelope, checkpoint: state =>
        {
            if (state != "Swapped") return;
            using var tamper = new SqliteConnection($"Data Source={Destination};Pooling=False");
            tamper.Open();
            tamper.Execute(field switch
            {
                "SourceHash" => "UPDATE playnite_migration_receipt SET SourceHash='wrong source';",
                "SourceManifestHash" => "UPDATE playnite_migration_receipt SET SourceManifestHash='wrong manifest';",
                _ => "UPDATE playnite_migration_receipt SET ReportJson='wrong report';",
            });
        }));
        Assert.Contains("migration receipt", error.Message);
        Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
        Assert.False(File.Exists(Destination + ".import-report.json"));
        Assert.Equal(3, (await ReplaceAsync(envelope)).ImportedSteam);
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.DurableReceipt")]
    [Trait("Invariant", "LibraryImport.CrashSafe")]
    public async Task Post_completion_failure_leaves_no_success_audit_never_rolls_back_and_refuses_from_artifact_alone()
    {
        await SeedLiveAsync();
        var envelope = await ReadEnvelope();
        await Assert.ThrowsAsync<IOException>(() => ReplaceAsync(envelope, checkpoint: state =>
        {
            if (state != "Committed") return;
            Assert.False(File.Exists(Destination + ".import-report.json"));
            throw new IOException("Injected post-completion failure");
        }));
        Assert.False(File.Exists(Destination + ".import-report.json"));
        var database = await LibraryDatabase.OpenAsync(Destination);
        Assert.Equal(3, (await database.ReadAsync(s => s.Games.GetAllAsync())).Count);
        Directory.Delete(Destination + ".import", recursive: true);
        await Assert.ThrowsAsync<IOException>(() => ReplaceAsync(envelope));
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.ReaderNeverPartial")]
    [Trait("Invariant", "LibraryImport.AtomicLive")]
    public async Task Reader_started_after_unconfirmed_rename_sees_only_old_after_rollback()
    {
        var database = await SeedLiveAsync();
        Task<IReadOnlyList<StoredGame>>? read = null;
        var envelope = await ReadEnvelope();
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DatabasePublicationLease.Contention.Value = (path, exclusive) =>
        {
            if (path == Destination && !exclusive) contended.TrySetResult();
        };
        try
        {
            await Assert.ThrowsAsync<IOException>(() => ReplaceAsync(envelope, checkpoint: state =>
            {
                if (state != "Swapped") return;
                read = database.ReadAsync(s => s.Games.GetAllAsync());
                Assert.True(contended.Task.IsCompletedSuccessfully, "The read must actually attempt the held OS lock.");
                Assert.False(read.IsCompleted);
                throw new IOException("Injected unconfirmed swap failure");
            }));
        }
        finally { DatabasePublicationLease.Contention.Value = null; }
        Assert.NotNull(read);
        Assert.Equal("old", Assert.Single(await read.WaitAsync(TimeSpan.FromSeconds(30))).Value.GameId);
    }

    [Fact]
    public async Task Uncoordinated_raw_SQLite_connection_is_refused_instead_of_renaming_over_live_sidecars()
    {
        var database = await SeedLiveAsync();
        var old = await File.ReadAllBytesAsync(Destination);
        var envelope = await ReadEnvelope();
        await using (var external = new SqliteConnection($"Data Source={Destination};Pooling=False"))
        {
            await external.OpenAsync();
            Assert.Equal(1L, await external.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM game;"));
            var error = await Assert.ThrowsAsync<IOException>(() => ReplaceAsync(envelope));
            Assert.Contains("close external database connections", error.Message);
            Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
            Assert.Equal(1L, await external.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM game;"));
        }
        Assert.Equal(3, (await ReplaceAsync(envelope)).ImportedSteam);
        Assert.Equal(3, (await database.ReadAsync(s => s.Games.GetAllAsync())).Count);
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.AtomicLive")]
    public async Task Unreceipted_earlier_import_requires_explicit_replacement_consent_in_Core_and_CLI()
    {
        var envelope = await ReadEnvelope();
        await LibraryMigration.RunAsync(envelope, Fixture(), Destination);
        // Recreate the earlier importer's artifact shape: schema v3 and imported rows, no receipt.
        using (var older = new SqliteConnection($"Data Source={Destination};Pooling=False"))
        {
            older.Open();
            older.Execute("DROP TABLE playnite_migration_receipt; UPDATE game SET Name='User edit after earlier import' WHERE GameId='220';");
        }
        var old = await File.ReadAllBytesAsync(Destination);
        var audit = await File.ReadAllTextAsync(Destination + ".import-report.json");
        var error = await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination));
        Assert.Contains("--replace-existing", error.Message);
        Assert.Contains("Earlier successful imports", error.Message);
        using (var process = StartCli())
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("--replace-existing", await errors);
            Assert.Empty(await output);
        }
        Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(audit, await File.ReadAllTextAsync(Destination + ".import-report.json"));
        Assert.Equal(3, (await ReplaceAsync(envelope)).ImportedSteam);
        Assert.Equal(old, await File.ReadAllBytesAsync(Path.Combine(Destination + ".import", "previous.sqlite")));
        Assert.Equal(audit, await File.ReadAllTextAsync(Path.Combine(Destination + ".import", "previous-report.json")));
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.AtomicLive")]
    public async Task ReceiptConfirmed_failure_discards_new_sidecars_and_success_audit_before_restoring_old_bytes()
    {
        var database = await SeedLiveAsync();
        var old = await File.ReadAllBytesAsync(Destination);
        var envelope = await ReadEnvelope();
        var superJournal = Destination + "-mj1234569AB";
        await Assert.ThrowsAsync<IOException>(() => ReplaceAsync(envelope, checkpoint: state =>
        {
            if (state != "ReceiptConfirmed") return;
            using (var probe = new SqliteConnection($"Data Source={Destination};Mode=ReadOnly;Pooling=False"))
            {
                probe.Open();
                Assert.Equal(3L, probe.ExecuteScalar<long>("SELECT COUNT(*) FROM game;"));
            }
            Assert.True(File.Exists(Destination + "-wal"));
            Assert.True(File.Exists(Destination + "-shm"));
            File.WriteAllText(Destination + "-journal", "discarded generation journal");
            File.WriteAllText(superJournal, "discarded generation super-journal");
            Assert.False(File.Exists(Destination + ".import-report.json"));
            throw new IOException("Injected confirmation-window failure");
        }));
        Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) Assert.False(File.Exists(Destination + suffix));
        Assert.False(File.Exists(superJournal));
        Assert.False(File.Exists(Destination + ".import-report.json"));
        Assert.Equal("old", Assert.Single(await database.ReadAsync(s => s.Games.GetAllAsync())).Value.GameId);
        Assert.Equal(3, (await ReplaceAsync(envelope)).ImportedSteam);
    }

    [Fact]
    public async Task Live_rollback_journal_is_refused_without_mutation()
    {
        await SeedLiveAsync();
        var envelope = await ReadEnvelope();
        await using (var external = new SqliteConnection($"Data Source={Destination};Pooling=False"))
        {
            await external.OpenAsync();
            await external.ExecuteAsync("PRAGMA journal_mode=DELETE;");
            var old = await File.ReadAllBytesAsync(Destination);
            using var transaction = external.BeginTransaction();
            await external.ExecuteAsync("UPDATE game SET Name='Uncommitted external write';", transaction: transaction);
            Assert.True(File.Exists(Destination + "-journal"));
            var error = await Assert.ThrowsAsync<IOException>(() => ReplaceAsync(envelope));
            Assert.Contains("rollback journal", error.Message);
            Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
            transaction.Rollback();
        }
        Assert.Equal(3, (await ReplaceAsync(envelope)).ImportedSteam);
    }

    [Fact]
    public async Task Existing_super_journal_is_refused_and_not_deleted()
    {
        await SeedLiveAsync();
        var old = await File.ReadAllBytesAsync(Destination);
        var journal = Destination + "-mj1234569AB";
        await File.WriteAllTextAsync(journal, "external super-journal");
        var envelope = await ReadEnvelope();
        var error = await Assert.ThrowsAsync<IOException>(() => ReplaceAsync(envelope));
        Assert.Contains("super-journal", error.Message);
        Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
        Assert.Equal("external super-journal", await File.ReadAllTextAsync(journal));
        File.Delete(journal);
        Assert.Equal(3, (await ReplaceAsync(envelope)).ImportedSteam);
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.DurableReceipt")]
    public async Task Intermediate_fingerprint_and_source_manifest_byte_identity_are_distinct_and_honestly_named()
    {
        var envelope = await ReadEnvelope();
        var report = await LibraryMigration.RunAsync(envelope, Fixture(), Destination, dryRun: true);
        var manifest = Directory.GetFiles(Fixture(), "*", SearchOption.AllDirectories)
            .OrderBy(p => Path.GetRelativePath(Fixture(), p), StringComparer.Ordinal)
            .Select(p => new { path = Path.GetRelativePath(Fixture(), p), size = (long?)new FileInfo(p).Length,
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) });
        var expectedManifest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest)));
        Assert.Equal(expectedManifest, report.SourceManifestHash);
        using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(envelope)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
        var parsed = Newtonsoft.Json.Linq.JObject.Load(reader);
        var expectedIntermediate = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(parsed.ToString(Newtonsoft.Json.Formatting.None))));
        Assert.Equal(expectedIntermediate, report.SourceHash);
        parsed["records"]![0]!["data"]!["Name"] = "different normalized record, identical source manifest";
        var changed = await LibraryMigration.RunAsync(parsed.ToString(), Fixture(), Destination, dryRun: true);
        Assert.NotEqual(report.SourceHash, changed.SourceHash);
        Assert.Equal(report.SourceManifestHash, changed.SourceManifestHash);
        Assert.Empty(Directory.GetFileSystemEntries(temporary));
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.DurableReceipt")]
    [Trait("Invariant", "LibraryImport.SourceUnchanged")]
    public async Task Combined_asset_replacement_receipt_verifies_full_asset_report_and_both_fingerprints()
    {
        await SeedLiveAsync();
        var source = Path.Combine(temporary, "asset-source");
        Directory.CreateDirectory(source);
        foreach (var file in Directory.GetFiles(Fixture(), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(source, Path.GetRelativePath(Fixture(), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var bundle = Path.Combine(Root, "tests", "fixtures", "v10-assets");
        foreach (var file in Directory.GetFiles(Path.Combine(bundle, "files"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(source, Path.GetRelativePath(bundle, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var before = Snapshot(source);
        var start = Start(Product("Playnite.LegacyImport"));
        start.ArgumentList.Add(source);
        using var reader = System.Diagnostics.Process.Start(start)!;
        var envelopeTask = reader.StandardOutput.ReadToEndAsync();
        var errors = reader.StandardError.ReadToEndAsync();
        await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(reader.ExitCode == 0, await errors);
        using var input = new Newtonsoft.Json.JsonTextReader(new StringReader(await envelopeTask)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
        var document = Newtonsoft.Json.Linq.JObject.Load(input);
        foreach (var reference in Newtonsoft.Json.Linq.JArray.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "references.json"))))
        {
            var row = ((Newtonsoft.Json.Linq.JArray)document["records"]!).Single(r => (string?)r["file"] == (string?)reference["file"] &&
                (string?)r["collection"] == (string?)reference["collection"] && (string?)r["data"]?["_id"] == (string?)reference["identity"]);
            row["data"]![(string)reference["field"]!] = reference["path"]!.DeepClone();
        }
        var envelope = document.ToString();
        var report = await LibraryMigration.RunAsync(envelope, source, Destination, replaceExisting: true);
        Assert.Equal(3, report.Assets.Count);
        Assert.Equal(1, report.MissingAssets);
        Assert.Equal(2, report.Assets.Count(a => a.Outcome == "copied"));
        foreach (var asset in report.Assets.Where(a => a.Destination != null))
            Assert.Equal(asset.Reference.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(temporary, asset.Destination!)))));
        using (var connection = new SqliteConnection($"Data Source={Destination};Pooling=False"))
        {
            await connection.OpenAsync();
            Assert.Equal(report.SourceHash, await connection.ExecuteScalarAsync<string>("SELECT SourceHash FROM playnite_migration_receipt WHERE Id=1;"));
            Assert.Equal(report.SourceManifestHash, await connection.ExecuteScalarAsync<string>("SELECT SourceManifestHash FROM playnite_migration_receipt WHERE Id=1;"));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(report), await connection.ExecuteScalarAsync<string>("SELECT ReportJson FROM playnite_migration_receipt WHERE Id=1;"));
        }
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(report), await File.ReadAllTextAsync(Destination + ".import-report.json"));
        File.Delete(Destination + ".import-report.json");
        Directory.Delete(Destination + ".import", recursive: true);
        await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, source, Destination, replaceExisting: true));
        Assert.Equal(before, Snapshot(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_lock_symlink_is_refused_by_both_database_and_import_API(bool dangling)
    {
        var target = Path.Combine(temporary, "unrelated");
        if (!dangling) await File.WriteAllTextAsync(target, "untouched");
        File.CreateSymbolicLink(Destination + ".publication.lock", target);
        await Assert.ThrowsAsync<IOException>(() => LibraryDatabase.OpenAsync(Destination));
        var envelope = await ReadEnvelope();
        await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination));
        if (dangling) Assert.False(File.Exists(target));
        else Assert.Equal("untouched", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(Destination));
    }
}
