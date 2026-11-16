using Dapper;
using Microsoft.Data.Sqlite;
using Playnite.Core.Database;
using Playnite.Core.Import;
using Playnite.Core.Models;
using Xunit;

namespace Playnite.Core.Tests.Import;

public sealed partial class LibraryMigrationTests
{
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
        var report = await LibraryMigration.RunAsync(await ReadEnvelope(), Fixture(), Destination);
        Assert.Equal(old, await File.ReadAllBytesAsync(Path.Combine(Destination + ".import", "previous.sqlite")));
        using (var connection = new SqliteConnection($"Data Source={Destination};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            Assert.Equal(report.SourceHash, await connection.ExecuteScalarAsync<string>("SELECT SourceHash FROM migration_receipt WHERE Id=1;"));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(report), await connection.ExecuteScalarAsync<string>("SELECT ReportJson FROM migration_receipt WHERE Id=1;"));
        }
        File.Delete(Destination + ".import-report.json");
        Directory.Delete(Destination + ".import", recursive: true);
        var published = await File.ReadAllBytesAsync(Destination);
        var envelope = await ReadEnvelope();
        var error = await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination));
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
        using var process = StartCli("--pause-at=" + state);
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
            await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination));
        }
        else
        {
            Assert.Equal(3, (await LibraryMigration.RunAsync(await ReadEnvelope(), Fixture(), Destination)).ImportedSteam);
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
        var failure = await Record.ExceptionAsync(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination, checkpoint: s =>
        {
            if (s != state) return;
            if (cancellation) cancel.Cancel();
            else throw new IOException("Injected publication failure");
        }, cancellationToken: cancel.Token));
        if (cancellation) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.Equal("Injected publication failure", Assert.IsType<IOException>(failure).Message);
        // Compare before reopening SQLite: recovery must restore bytes, not just equivalent rows.
        Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
        Assert.Equal("old", Assert.Single(await database.ReadAsync(s => s.Games.GetAllAsync())).Value.GameId);
        Assert.Equal(3, (await LibraryMigration.RunAsync(envelope, Fixture(), Destination)).ImportedSteam);
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
        using var process = StartCli("--pause-at=" + state);
        var output = process.StandardOutput.ReadToEndAsync();
        try
        {
            Assert.Equal("checkpoint:" + state, await process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            var read = database.ReadAsync(s => s.Games.GetAllAsync());
            await Task.Delay(100);
            Assert.False(read.IsCompleted, "A reader must not bypass the exclusive publication lease.");
            using var cancel = new CancellationTokenSource();
            var cancelled = database.ReadAsync(s => s.Games.GetAllAsync(), cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            await process.StandardInput.WriteLineAsync();
            await process.StandardInput.FlushAsync();
            Assert.Equal(new[] { "220", "570", "400" }, (await read.WaitAsync(TimeSpan.FromSeconds(30))).Select(g => g.Value.GameId));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
            await output;
        }
        finally
        {
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
        var replacement = LibraryMigration.RunAsync(await ReadEnvelope(), Fixture(), Destination);
        try
        {
            await Task.Delay(100);
            Assert.False(replacement.IsCompleted);
        }
        finally { release.TrySetResult(); }
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
        await Assert.ThrowsAsync<FileNotFoundException>(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination, checkpoint: state =>
        {
            if (state == "Publishing") File.Delete(Path.Combine(Destination + ".import", "library.sqlite"));
        }));
        Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(old, await File.ReadAllBytesAsync(Path.Combine(Destination + ".import", "previous.sqlite")));
        Assert.Equal(3, (await LibraryMigration.RunAsync(envelope, Fixture(), Destination)).ImportedSteam);
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.DurableReceipt")]
    public async Task Receipt_confirmation_failure_restores_byte_identical_old_library_before_readers_resume()
    {
        await SeedLiveAsync();
        var old = await File.ReadAllBytesAsync(Destination);
        var envelope = await ReadEnvelope();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination, checkpoint: state =>
        {
            if (state != "Swapped") return;
            using var tamper = new SqliteConnection($"Data Source={Destination};Pooling=False");
            tamper.Open();
            tamper.Execute("UPDATE migration_receipt SET SourceHash='wrong source';");
        }));
        Assert.Contains("migration receipt", error.Message);
        Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(3, (await LibraryMigration.RunAsync(envelope, Fixture(), Destination)).ImportedSteam);
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.DurableReceipt")]
    [Trait("Invariant", "LibraryImport.CrashSafe")]
    public async Task Post_completion_failure_never_rolls_back_the_confirmed_publication()
    {
        await SeedLiveAsync();
        var envelope = await ReadEnvelope();
        await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination, checkpoint: state =>
        {
            if (state == "Committed") throw new IOException("Injected post-completion failure");
        }));
        var database = await LibraryDatabase.OpenAsync(Destination);
        Assert.Equal(3, (await database.ReadAsync(s => s.Games.GetAllAsync())).Count);
        await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination));
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.ReaderNeverPartial")]
    [Trait("Invariant", "LibraryImport.AtomicLive")]
    public async Task Reader_started_after_unconfirmed_rename_sees_only_old_after_rollback()
    {
        var database = await SeedLiveAsync();
        Task<IReadOnlyList<StoredGame>>? read = null;
        var envelope = await ReadEnvelope();
        await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination, checkpoint: state =>
        {
            if (state != "Swapped") return;
            read = database.ReadAsync(s => s.Games.GetAllAsync());
            Assert.False(read.IsCompleted);
            throw new IOException("Injected unconfirmed swap failure");
        }));
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
            var error = await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Fixture(), Destination));
            Assert.Contains("close external database connections", error.Message);
            Assert.Equal(old, await File.ReadAllBytesAsync(Destination));
            Assert.Equal(1L, await external.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM game;"));
        }
        Assert.Equal(3, (await LibraryMigration.RunAsync(envelope, Fixture(), Destination)).ImportedSteam);
        Assert.Equal(3, (await database.ReadAsync(s => s.Games.GetAllAsync())).Count);
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
