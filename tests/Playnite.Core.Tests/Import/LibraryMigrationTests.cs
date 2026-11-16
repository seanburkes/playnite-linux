using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using Playnite.Core.Database;
using Playnite.Core.Import;
using Xunit;

namespace Playnite.Core.Tests.Import;

public sealed class LibraryMigrationTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "playnite-import-" + Guid.NewGuid().ToString("N"));
    private string Destination => Path.Combine(temporary, "library.sqlite");
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Playnite.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
    private static string Product(string name) => Path.Combine(Root, "src", name, "bin",
        AppContext.BaseDirectory.Contains("/Release/", StringComparison.Ordinal) ? "Release" : "Debug", "net11.0", name + ".dll");
    private static string Fixture(bool malicious = false) => Path.Combine(Root, "tests", "fixtures", malicious ? "v10-malicious" : "v10", "library");
    public LibraryMigrationTests() => Directory.CreateDirectory(temporary);

    [Fact]
    [Trait("Invariant", "LibraryImport.SourceUnchanged")]
    [Trait("Invariant", "LibraryImport.NoCommitWithoutVerify")]
    public async Task Frozen_Windows_fixture_imports_every_Steam_row_and_reports_every_document()
    {
        var before = Snapshot(Fixture());
        var envelope = await ReadEnvelope();
        var states = new List<string>();
        var report = await LibraryMigration.RunAsync(envelope, Destination, checkpoint: states.Add);
        Assert.Equal(3, report.ExpectedSteam); Assert.Equal(3, report.ImportedSteam);
        Assert.Equal(9, report.Decisions.Count);
        var skip = Assert.Single(report.Decisions.Where(d => d.Outcome == "skipped"));
        Assert.Equal("10000000-0000-0000-0000-000000000004", skip.Identity);
        Assert.Contains("Non-Steam", skip.Reason);
        Assert.Equal(["Read", "Transformed", "Copied", "PartialWrite", "PartialWrite", "PartialWrite", "Written", "Verified", "BeforeSwap", "Committed"], states);
        var database = await LibraryDatabase.OpenAsync(Destination);
        var games = await database.ReadAsync(s => s.Games.GetAllAsync());
        Assert.Equal(new[] { "220", "570", "400" }, games.Select(g => g.Value.GameId));
        Assert.Equal("Synthetic Steam Unicode — 雪", games[2].Value.Name);
        Assert.Equal(new Playnite.Core.Models.ReleaseDate(2019, 7), games[2].Value.ReleaseDate);
        Assert.Equal(@"C:\Synthetic Steam\steamapps\common\Synthetic 220", games[0].Value.InstallDirectory);
        Assert.Equal(3600UL, games[0].Value.Playtime);
        Assert.Equal(before, Snapshot(Fixture()));
        Assert.True(File.Exists(Destination + ".import-report.json"));
        Assert.True(File.Exists(Path.Combine(Destination + ".import", "source.json")));
    }

    [Fact]
    public async Task Repeated_dry_run_is_an_idempotent_plan_and_writes_nothing()
    {
        var before = Snapshot(Fixture());
        var envelope = await ReadEnvelope();
        var first = await LibraryMigration.RunAsync(envelope, Destination, dryRun: true);
        var second = await LibraryMigration.RunAsync(envelope, Destination, dryRun: true);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(3, first.ExpectedSteam); Assert.Equal(0, first.ImportedSteam);
        Assert.Empty(Directory.GetFileSystemEntries(temporary));
        Assert.Equal(before, Snapshot(Fixture()));
    }

    [Fact]
    public async Task Second_real_run_refuses_clearly_and_preserves_existing_database()
    {
        var envelope = await ReadEnvelope();
        await LibraryMigration.RunAsync(envelope, Destination);
        var before = Snapshot(temporary);
        var error = await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Destination));
        Assert.Contains("second run", error.Message);
        Assert.Equal(before, Snapshot(temporary));
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Transformed")]
    [InlineData("Copied")]
    [InlineData("PartialWrite")]
    [InlineData("Written")]
    [InlineData("Verified")]
    [InlineData("BeforeSwap")]
    [InlineData("Committed")]
    [Trait("Invariant", "LibraryImport.SourceUnchanged")]
    [Trait("Invariant", "LibraryImport.ReaderNeverPartial")]
    [Trait("Invariant", "LibraryImport.AtomicLive")]
    [Trait("Invariant", "LibraryImport.BackupBeforeWrite")]
    public async Task SIGKILL_at_each_pipeline_checkpoint_leaves_absent_or_complete_live_and_restartable_staging(string state)
    {
        var before = Snapshot(Fixture());
        using var process = StartCli("--pause-at=" + state);
        var output = process.StandardOutput.ReadToEndAsync();
        var checkpoint = await process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("checkpoint:" + state, checkpoint);
        Assert.Equal(state == "Committed", File.Exists(Destination));
        if (state is "PartialWrite" or "Written" or "Verified" or "BeforeSwap" or "Committed")
            Assert.True(File.Exists(Path.Combine(Destination + ".import", "manifest.json")));
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await output;
        Assert.NotEqual(0, process.ExitCode);
        if (state != "Committed")
        {
            using var retry = StartCli();
            var retryOutput = retry.StandardOutput.ReadToEndAsync();
            var retryError = retry.StandardError.ReadToEndAsync();
            await retry.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(retry.ExitCode == 0, await retryError);
            Assert.Equal(3, JsonDocument.Parse(await retryOutput).RootElement.GetProperty("ImportedSteam").GetInt32());
        }
        var database = await LibraryDatabase.OpenAsync(Destination);
        Assert.Equal(3, (await database.ReadAsync(s => s.Games.GetAllAsync())).Count);
        Assert.Equal(before, Snapshot(Fixture()));
        var envelope = await ReadEnvelope();
        await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Destination));
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Transformed")]
    [InlineData("Copied")]
    [InlineData("PartialWrite")]
    [InlineData("Written")]
    [InlineData("Verified")]
    [InlineData("BeforeSwap")]
    public async Task Cancellation_before_publication_never_creates_live_database_and_can_restart(string state)
    {
        var envelope = await ReadEnvelope();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LibraryMigration.RunAsync(envelope, Destination,
            checkpoint: s => { if (s == state) cancel.Cancel(); }, cancellationToken: cancel.Token));
        Assert.False(File.Exists(Destination));
        Assert.Equal(3, (await LibraryMigration.RunAsync(envelope, Destination)).ImportedSteam);
    }

    [Fact]
    public async Task CLI_dry_run_writes_no_artifacts_and_refuses_a_destination_inside_source()
    {
        using (var process = StartCli("--dry-run"))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, await error);
            Assert.True(JsonDocument.Parse(await output).RootElement.GetProperty("DryRun").GetBoolean());
        }
        Assert.Empty(Directory.GetFileSystemEntries(temporary));
        var before = Snapshot(Fixture());
        var start = Start(Product("Playnite.Cli"));
        foreach (var arg in new[] { "import-v10", Product("Playnite.LegacyImport"), Fixture(), Path.Combine(Fixture(), "forbidden.sqlite") }) start.ArgumentList.Add(arg);
        using var invalid = Process.Start(start)!;
        var invalidError = invalid.StandardError.ReadToEndAsync();
        await invalid.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, invalid.ExitCode);
        Assert.Contains("outside the source", await invalidError);
        Assert.Equal(before, Snapshot(Fixture()));
    }

    [Fact]
    public async Task Different_source_or_symlink_staging_refuses_instead_of_deleting_foreign_files()
    {
        var envelope = await ReadEnvelope();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LibraryMigration.RunAsync(envelope, Destination,
            checkpoint: s => { if (s == "Copied") cancel.Cancel(); }, cancellationToken: cancel.Token));
        var changed = JObject.Parse(envelope); changed["records"]![0]!["data"]!["Name"] = "different snapshot";
        var before = Snapshot(temporary);
        var refusal = await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(changed.ToString(), Destination));
        Assert.Contains("not owned", refusal.Message);
        Assert.Equal(before, Snapshot(temporary));
        var linkTarget = Path.Combine(temporary, "unrelated.txt"); File.WriteAllText(linkTarget, "untouched");
        File.CreateSymbolicLink(Path.Combine(Destination + ".import", "foreign-link"), linkTarget);
        await Assert.ThrowsAsync<IOException>(() => LibraryMigration.RunAsync(envelope, Destination));
        Assert.Equal("untouched", File.ReadAllText(linkTarget));
    }

    [Fact]
    public async Task Missing_reference_and_duplicate_identity_fail_before_any_destination_writes()
    {
        var envelope = JObject.Parse(await ReadEnvelope());
        var records = (JArray)envelope["records"]!;
        var genre = records.Single(r => (string?)r["collection"] == "Genre");
        genre.Remove();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LibraryMigration.RunAsync(envelope.ToString(), Destination));
        Assert.Contains("10000000-0000-0000-0000-000000000001", error.Message);
        Assert.Contains("20000000-0000-0000-0000-000000000003", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(temporary));
        records.Add(genre);
        records.Add(records.First!.DeepClone());
        await Assert.ThrowsAsync<InvalidDataException>(() => LibraryMigration.RunAsync(envelope.ToString(), Destination));
        Assert.Empty(Directory.GetFileSystemEntries(temporary));
    }

    [Fact]
    [Trait("Invariant", "LibraryImport.NoCommitWithoutVerify")]
    public async Task Tampered_written_database_fails_verification_and_never_publishes()
    {
        var envelope = await ReadEnvelope();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LibraryMigration.RunAsync(envelope, Destination, checkpoint: state =>
        {
            if (state != "Written") return;
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(Destination + ".import", "library.sqlite")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE game SET Name='tampered';";
            command.ExecuteNonQuery();
        }));
        Assert.Contains("verification failed", error.Message);
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Destination + ".import-report.json"));
        Assert.Equal(3, (await LibraryMigration.RunAsync(envelope, Destination)).ImportedSteam);
    }

    [Fact]
    public async Task Unknown_collection_is_reported_by_identity_instead_of_dropped()
    {
        var envelope = JObject.Parse(await ReadEnvelope());
        ((JArray)envelope["records"]!).Add(new JObject { ["file"] = "tools.db", ["collection"] = "Unexpected", ["data"] = new JObject { ["_id"] = "unknown-identity" } });
        var report = await LibraryMigration.RunAsync(envelope.ToString(), Destination, dryRun: true);
        Assert.Equal(10, report.Decisions.Count);
        Assert.Contains(report.Decisions, d => d.Identity == "unknown-identity" && d.Outcome == "skipped" && d.Reason.StartsWith("Unsupported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Malicious_type_keys_are_inert_at_every_depth_in_reader_and_writer_with_a_working_constructor_control()
    {
        ConstructorTrap.Calls = 0;
        Assert.Throws<InvalidOperationException>(() => new ConstructorTrap());
        Assert.Equal(1, ConstructorTrap.Calls);
        ConstructorTrap.Calls = 0;
        var before = Snapshot(Fixture(true));
        // Load the raw-reader assembly without linking its types into any product graph.
        var context = new ReaderContext(Product("Playnite.LegacyImport"));
        try
        {
            var assembly = context.LoadFromAssemblyPath(Product("Playnite.LegacyImport"));
            var read = assembly.GetType("Playnite.LegacyImport.LibraryReader", throwOnError: true)!.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = read.Invoke(null, [Fixture(true)]);
            var envelope = JsonSerializer.Serialize(result);
            var document = JObject.Parse(envelope);
            var raw = ((JArray)document["records"]!).Single(r => r["data"]?["_type"] != null)["data"]!;
            Assert.Equal(typeof(ConstructorTrap).AssemblyQualifiedName!.Split(", Version=", StringSplitOptions.None)[0], (string?)raw["_type"]);
            Assert.Equal("Untrusted.Payload, nonexistent-untrusted-assembly", (string?)raw["$type"]);
            Assert.Equal("System.Diagnostics.Process, System.Diagnostics.Process", (string?)raw["UnmappedAdversarial"]?["_type"]);
            Assert.Equal("System.IO.FileInfo, System.Private.CoreLib", (string?)raw["UnmappedAdversarial"]?["$type"]);
            Assert.Equal("Untrusted.Payload, nonexistent-untrusted-assembly", (string?)raw["UnmappedAdversarial"]?["Array"]?[0]?["_type"]);
            Assert.Equal(3, (await LibraryMigration.RunAsync(envelope, Destination)).ImportedSteam);
            var db = await LibraryDatabase.OpenAsync(Destination);
            var saved = await db.ReadAsync(s => s.Games.GetAsync(Guid.Parse("10000000-0000-0000-0000-000000000001")));
            Assert.True(JToken.DeepEquals(raw, JObject.Parse(saved!.LegacyJson)));
            Assert.Equal(0, ConstructorTrap.Calls);
            Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "nonexistent-untrusted-assembly");
            Assert.Equal(before, Snapshot(Fixture(true)));
            var subprocess = await ReadEnvelope(true);
            Assert.True(JToken.DeepEquals(JObject.Parse(envelope), JObject.Parse(subprocess)));
        }
        finally { context.Unload(); }
    }

    public sealed class ConstructorTrap
    {
        public static int Calls;
        public ConstructorTrap() { Calls++; throw new InvalidOperationException("Untrusted constructor invoked."); }
    }

    private sealed class ReaderContext(string reader) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
            => name.Name == "LiteDB" ? LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(reader)!, "LiteDB.dll")) : null;
    }

    private Process StartCli(params string[] options)
    {
        var start = Start(Product("Playnite.Cli"));
        foreach (var arg in new[] { "import-v10", Product("Playnite.LegacyImport"), Fixture(), Destination }.Concat(options)) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private static async Task<string> ReadEnvelope(bool malicious = false)
    {
        var start = Start(Product("Playnite.LegacyImport")); start.ArgumentList.Add(Fixture(malicious));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    private static ProcessStartInfo Start(string dll)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(dll); return start;
    }

    private static string[] Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(p => Path.GetRelativePath(directory, p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    public void Dispose() => Directory.Delete(temporary, recursive: true);
}
