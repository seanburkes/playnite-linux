using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Playnite.Cli;
using Playnite.Core.Database;
using Playnite.Core.Import;
using Playnite.Core.Tests.Serialization;
using Xunit;

namespace Playnite.Core.Tests.Import;

public sealed class LibraryImportServiceIntegrationTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "playnite-import-service-integration-" + Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly string output;

    public LibraryImportServiceIntegrationTests()
    {
        Directory.CreateDirectory(temporary);
        source = Path.Combine(temporary, "source");
        CopyDirectory(Fixture, source);
        output = Path.Combine(temporary, "output");
        Directory.CreateDirectory(output);
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.SourceUnchanged")]
    [Trait("Invariant", "LibraryImport.NoCommitWithoutVerify")]
    public async Task Temporary_fixture_publishes_verified_games_through_the_service()
    {
        var before = Snapshot(source);
        var progress = new RecordingProgress();
        var destination = Path.Combine(output, "library.sqlite");

        var result = await Service().ImportAsync(new(source, destination), progress);

        Assert.Equal(LibraryImportOutcome.Published, result.Outcome);
        Assert.Equal(3, result.Report.ExpectedSteam);
        Assert.Equal(3, result.Report.ImportedSteam);
        Assert.Equal(9, result.Report.Decisions.Count);
        Assert.Single(result.Report.Decisions, decision => decision.Outcome == "skipped");
        Assert.True(File.Exists(destination));
        Assert.Equal(["Reading", "Read", "Transformed"], progress.Items.Take(3).Select(item => item.Checkpoint));
        Assert.Contains(progress.Items, item => item.Stage == LibraryImportStage.Committed);
        Assert.Equal(LibraryImportStage.BeforeReportMove, progress.Items[^1].Stage);

        var database = await LibraryDatabase.OpenAsync(destination);
        var games = await database.ReadAsync(session => session.Games.GetAllAsync());
        Assert.Equal(["220", "570", "400"], games.Select(game => game.Value.GameId));
        Assert.Equal(before, Snapshot(source));
    }

    [Fact]
    public async Task Dry_run_returns_a_plan_and_creates_no_artifacts()
    {
        var sourceBefore = Snapshot(source);
        var destination = Path.Combine(output, "library.sqlite");

        var result = await Service().ImportAsync(new(source, destination, DryRun: true));

        Assert.Equal(LibraryImportOutcome.Planned, result.Outcome);
        Assert.True(result.Report.DryRun);
        Assert.Equal(3, result.Report.ExpectedSteam);
        Assert.Equal(0, result.Report.ImportedSteam);
        Assert.Empty(Directory.GetFileSystemEntries(output));
        Assert.Equal(sourceBefore, Snapshot(source));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Unwritable_destination_is_reported_as_destination_unavailable()
    {
        var destination = Path.Combine(output, "library.sqlite");
        File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            var exception = await Assert.ThrowsAsync<LibraryImportException>(() =>
                Service().ImportAsync(new(source, destination)));

            Assert.Equal(LibraryImportFailure.DestinationUnavailable, exception.Failure);
            Assert.IsAssignableFrom<UnauthorizedAccessException>(exception.InnerException);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Second_real_run_refuses_and_preserves_the_published_database()
    {
        var destination = Path.Combine(output, "library.sqlite");
        var service = Service();
        await service.ImportAsync(new(source, destination));
        var published = Snapshot(output);

        var exception = await Assert.ThrowsAsync<LibraryImportException>(() =>
            service.ImportAsync(new(source, destination)));

        Assert.Equal(LibraryImportFailure.DestinationUnavailable, exception.Failure);
        Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains("second run", exception.Message);
        Assert.Equal(published, Snapshot(output));
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Copied")]
    [InlineData("Written")]
    [InlineData("Verified")]
    [InlineData("BeforeSwap")]
    [Trait("Invariant", "LibraryImport.AtomicLive")]
    [Trait("Invariant", "LibraryImport.SourceUnchanged")]
    public async Task Cancellation_before_publication_returns_no_result_and_reruns_cleanly(string checkpoint)
    {
        var destination = Path.Combine(output, "library.sqlite");
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelAt(checkpoint, cancellation);
        var sourceBefore = Snapshot(source);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().ImportAsync(new(source, destination), progress, cancellation.Token));

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".import-report.json"));
        Assert.Equal(sourceBefore, Snapshot(source));

        var rerun = await Service().ImportAsync(new(source, destination));
        Assert.Equal(LibraryImportOutcome.Published, rerun.Outcome);
        Assert.Equal(3, rerun.Report.ImportedSteam);
    }

    [Fact]
    public async Task Missing_source_is_reported_as_a_reader_failure_without_a_destination()
    {
        var destination = Path.Combine(output, "library.sqlite");

        var exception = await Assert.ThrowsAsync<LibraryImportException>(() =>
            Service().ImportAsync(new(Path.Combine(temporary, "absent"), destination)));

        Assert.Equal(LibraryImportFailure.SourceReadFailed, exception.Failure);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    private static string Fixture => RepositoryPaths.Combine("tests", "fixtures", "v10", "library");

    private static LibraryImportService Service()
    {
        var configuration = typeof(LibraryImportServiceIntegrationTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var reader = RepositoryPaths.Combine("src", "Playnite.LegacyImport", "bin", configuration, "net11.0",
            "Playnite.LegacyImport.dll");
        return new(new LegacyLibraryReaderProcess(reader));
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string[] Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(directory, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();

    private sealed class RecordingProgress : IProgress<LibraryImportProgress>
    {
        public List<LibraryImportProgress> Items { get; } = [];

        public void Report(LibraryImportProgress value) => Items.Add(value);
    }

    private sealed class CancelAt(string checkpoint, CancellationTokenSource cancellation) : IProgress<LibraryImportProgress>
    {
        public void Report(LibraryImportProgress value)
        {
            if (value.Checkpoint == checkpoint)
            {
                cancellation.Cancel();
            }
        }
    }

    public void Dispose() => Directory.Delete(temporary, recursive: true);
}
