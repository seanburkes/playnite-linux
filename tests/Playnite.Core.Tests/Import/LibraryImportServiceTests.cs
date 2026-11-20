using Playnite.Core.Import;
using Xunit;

namespace Playnite.Core.Tests.Import;

public sealed class LibraryImportServiceTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "playnite-import-service-" + Guid.NewGuid().ToString("N"));

    public LibraryImportServiceTests() => Directory.CreateDirectory(temporary);

    [Fact]
    public async Task Import_reads_then_migrates_and_reports_every_checkpoint_in_order()
    {
        var reader = new FakeReader { Output = "envelope" };
        var migrator = new FakeMigrator
        {
            Run = (destination, checkpoint) =>
            {
                foreach (var name in new[]
                {
                    "Read", "Transformed", "Copied", "AssetPartialCopy", "AssetFlushed", "PartialWrite",
                    "Written", "Verified", "BeforeSwap", "Publishing", "Swapped", "ReceiptConfirmed",
                    "Committed", "BeforeReportMove", "FutureCheckpoint",
                })
                {
                    checkpoint?.Invoke(name);
                }

                File.WriteAllText(destination, "published");
                return Task.FromResult(Report());
            },
        };
        var progress = new RecordingProgress();
        var destination = Path.Combine(temporary, "library.sqlite");

        var result = await new LibraryImportService(reader, migrator).ImportAsync(
            new(Path.Combine(temporary, "source"), destination), progress);

        Assert.Equal(LibraryImportOutcome.Published, result.Outcome);
        Assert.Same(migrator.Report, result.Report);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(Path.Combine(temporary, "source"), reader.SourceDirectory);
        Assert.Equal("envelope", migrator.Intermediate);
        Assert.Equal(Path.Combine(temporary, "source"), migrator.SourceDirectory);
        Assert.Equal(destination, migrator.Destination);
        Assert.False(migrator.DryRun);
        Assert.False(migrator.ReplaceExisting);
        Assert.Equal(
        [
            LibraryImportStage.Reading, LibraryImportStage.Read, LibraryImportStage.Transformed, LibraryImportStage.Copied, LibraryImportStage.AssetPartialCopy,
            LibraryImportStage.AssetFlushed, LibraryImportStage.PartialWrite, LibraryImportStage.Written,
            LibraryImportStage.Verified, LibraryImportStage.BeforeSwap, LibraryImportStage.Publishing,
            LibraryImportStage.Swapped, LibraryImportStage.ReceiptConfirmed, LibraryImportStage.Committed,
            LibraryImportStage.BeforeReportMove, LibraryImportStage.Unknown,
        ], progress.Items.Select(item => item.Stage));
        Assert.Equal("FutureCheckpoint", progress.Items[^1].Checkpoint);
    }

    [Fact]
    public async Task Dry_run_returns_Planned_and_passes_consent_options_without_requiring_a_destination()
    {
        var migrator = new FakeMigrator
        {
            Run = (_, _) => Task.FromResult(Report(dryRun: true)),
        };

        var result = await new LibraryImportService(new FakeReader { Output = "envelope" }, migrator).ImportAsync(
            new(Path.Combine(temporary, "source"), Path.Combine(temporary, "library.sqlite"),
                DryRun: true, ReplaceExisting: true));

        Assert.Equal(LibraryImportOutcome.Planned, result.Outcome);
        Assert.True(migrator.DryRun);
        Assert.True(migrator.ReplaceExisting);
        Assert.False(File.Exists(Path.Combine(temporary, "library.sqlite")));
    }

    [Fact]
    public async Task Migration_success_without_a_published_destination_is_a_publication_failure()
    {
        var migrator = new FakeMigrator { Run = (_, _) => Task.FromResult(Report()) };

        var exception = await Assert.ThrowsAsync<LibraryImportException>(() =>
            new LibraryImportService(new FakeReader { Output = "envelope" }, migrator).ImportAsync(
                new(Path.Combine(temporary, "source"), Path.Combine(temporary, "missing.sqlite"))));

        Assert.Equal(LibraryImportFailure.PublicationFailed, exception.Failure);
    }

    [Fact]
    public async Task Reader_io_failure_is_reported_as_source_read_failed()
    {
        var reader = new FakeReader { Failure = new IOException("reader exploded") };

        var exception = await Assert.ThrowsAsync<LibraryImportException>(() =>
            new LibraryImportService(reader, new FakeMigrator()).ImportAsync(
                new(Path.Combine(temporary, "source"), Path.Combine(temporary, "library.sqlite"))));

        Assert.Equal(LibraryImportFailure.SourceReadFailed, exception.Failure);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task Empty_reader_output_is_reported_as_source_read_failed()
    {
        var exception = await Assert.ThrowsAsync<LibraryImportException>(() =>
            new LibraryImportService(new FakeReader { Output = "" }, new FakeMigrator()).ImportAsync(
                new(Path.Combine(temporary, "source"), Path.Combine(temporary, "library.sqlite"))));

        Assert.Equal(LibraryImportFailure.SourceReadFailed, exception.Failure);
    }

    [Theory]
    [InlineData("invalid-data", LibraryImportFailure.SourceRejected)]
    [InlineData("argument", LibraryImportFailure.InvalidRequest)]
    [InlineData("io", LibraryImportFailure.DestinationUnavailable)]
    public async Task Migration_failures_keep_a_clear_failure_category(string failure, LibraryImportFailure expected)
    {
        Exception thrown = failure switch
        {
            "invalid-data" => new InvalidDataException("bad source"),
            "argument" => new ArgumentException("bad path"),
            _ => new IOException("destination busy"),
        };
        var migrator = new FakeMigrator { Run = (_, _) => Task.FromException<MigrationReport>(thrown) };

        var exception = await Assert.ThrowsAsync<LibraryImportException>(() =>
            new LibraryImportService(new FakeReader { Output = "envelope" }, migrator).ImportAsync(
                new(Path.Combine(temporary, "source"), Path.Combine(temporary, "library.sqlite"))));

        Assert.Equal(expected, exception.Failure);
        Assert.Same(thrown, exception.InnerException);
    }

    [Fact]
    public async Task Pre_cancelled_import_stops_before_reading_or_migrating()
    {
        var reader = new FakeReader { Output = "envelope" };
        var migrator = new FakeMigrator();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LibraryImportService(reader, migrator).ImportAsync(
                new(Path.Combine(temporary, "source"), Path.Combine(temporary, "library.sqlite")),
                cancellationToken: cancellation.Token));

        Assert.Equal(0, reader.Calls);
        Assert.Equal(0, migrator.Calls);
    }

    [Fact]
    public async Task Cancellation_during_reading_is_clean_and_returns_no_result()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new FakeReader
        {
            Run = (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult("unreachable");
            },
        };
        var migrator = new FakeMigrator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LibraryImportService(reader, migrator).ImportAsync(
                new(Path.Combine(temporary, "source"), Path.Combine(temporary, "library.sqlite")),
                cancellationToken: cancellation.Token));

        Assert.Equal(0, migrator.Calls);
    }

    [Fact]
    public async Task Cancellation_reported_by_the_migration_is_not_wrapped()
    {
        using var cancellation = new CancellationTokenSource();
        var migrator = new FakeMigrator
        {
            Run = (_, checkpoint) =>
            {
                checkpoint?.Invoke("Copied");
                cancellation.Cancel();
                return Task.FromException<MigrationReport>(new OperationCanceledException(cancellation.Token));
            },
        };
        var progress = new RecordingProgress();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LibraryImportService(new FakeReader { Output = "envelope" }, migrator).ImportAsync(
                new(Path.Combine(temporary, "source"), Path.Combine(temporary, "library.sqlite")),
                progress, cancellation.Token));

        Assert.Equal(LibraryImportStage.Copied, progress.Items[^1].Stage);
    }

    [Fact]
    public async Task Null_request_is_rejected_before_reading()
    {
        var reader = new FakeReader();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new LibraryImportService(reader, new FakeMigrator()).ImportAsync(null!));

        Assert.Equal(0, reader.Calls);
    }

    private static MigrationReport Report(bool dryRun = false) => new(1, "source-hash", "manifest-hash", 3,
        dryRun ? 0 : 3, dryRun,
        [new MigrationDecision("games.db", "Game", "10000000-0000-0000-0000-000000000001", "import", "Steam")],
        []);

    private sealed class FakeReader : ILibraryImportReader
    {
        public string Output { get; init; } = "{}";
        public Exception? Failure { get; init; }
        public Func<string, CancellationToken, Task<string>>? Run { get; init; }
        public int Calls { get; private set; }
        public string? SourceDirectory { get; private set; }

        public Task<string> ReadAsync(string sourceDirectory, CancellationToken cancellationToken = default)
        {
            Calls++;
            SourceDirectory = sourceDirectory;
            if (Run is not null)
            {
                return Run(sourceDirectory, cancellationToken);
            }

            return Failure is null ? Task.FromResult(Output) : Task.FromException<string>(Failure);
        }
    }

    private sealed class FakeMigrator : ILibraryMigrator
    {
        public Func<string, Action<string>?, Task<MigrationReport>>? Run { get; init; }
        public int Calls { get; private set; }
        public string? Intermediate { get; private set; }
        public string? SourceDirectory { get; private set; }
        public string? Destination { get; private set; }
        public bool DryRun { get; private set; }
        public bool ReplaceExisting { get; private set; }
        public MigrationReport Report { get; private set; } = LibraryImportServiceTests.Report();

        public Task<MigrationReport> RunAsync(string intermediate, string sourceDirectory, string destination,
            bool dryRun, Action<string>? checkpoint, CancellationToken cancellationToken, bool replaceExisting)
        {
            Calls++;
            Intermediate = intermediate;
            SourceDirectory = sourceDirectory;
            Destination = destination;
            DryRun = dryRun;
            ReplaceExisting = replaceExisting;
            var task = Run?.Invoke(destination, checkpoint) ?? Task.FromResult(Report);
            return Capture(task);
        }

        private async Task<MigrationReport> Capture(Task<MigrationReport> task)
        {
            Report = await task;
            return Report;
        }
    }

    private sealed class RecordingProgress : IProgress<LibraryImportProgress>
    {
        public List<LibraryImportProgress> Items { get; } = [];

        public void Report(LibraryImportProgress value) => Items.Add(value);
    }

    public void Dispose() => Directory.Delete(temporary, recursive: true);
}
