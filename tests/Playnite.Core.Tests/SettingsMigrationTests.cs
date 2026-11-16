using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Playnite.Settings;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "playnite-settings-" + Guid.NewGuid());
    private string Source => Path.Combine(directory, "source", "config.json");
    private string Destination => Path.Combine(directory, "destination", "settings.json");
    private static readonly SettingsPathMapping[] Mappings = [new(@"C:\Games", "/games"), new(@"C:\Games\Special", "/special")];

    public SettingsMigrationTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData("fullscreenConfig.json")]
    [InlineData("windowPositions.json")]
    public async Task FrozenV10SettingsRoundTripByteForByte(string name)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "golden", "settings", name));
        await File.WriteAllBytesAsync(Source, bytes);
        await SettingsMigration.MigrateAsync(Source, Destination, []);
        Assert.Equal(bytes, SettingsMigration.ExportV10(await File.ReadAllBytesAsync(Destination)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Source));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Source)!));
        if (name == "config.json")
        {
            using var envelope = JsonDocument.Parse(await File.ReadAllBytesAsync(Destination));
            Assert.Empty(envelope.RootElement.GetProperty("legacy").EnumerateObject());
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(bytes).RootElement,
                envelope.RootElement.GetProperty("settings")));
        }
    }

    [Fact]
    public async Task FrozenV10ConfigWithWindowsPathsChangesOnlyExplicitlyMappedStringBytes()
    {
        var golden = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "settings", "config.json"));
        var fixture = golden.Replace("\"DatabasePath\": null", "\"DatabasePath\": \"C:\\\\Games\\\\Library\"", StringComparison.Ordinal)
            .Replace("\"AutoBackupDir\": null", "\"AutoBackupDir\": \"D:\\\\Backups\"", StringComparison.Ordinal);
        Assert.NotEqual(golden, fixture);
        await Seed(fixture);
        var result = await SettingsMigration.MigrateAsync(Source, Destination, Mappings);
        Assert.Single(result.Report.RewrittenPaths);
        Assert.Equal("AutoBackupDir", Assert.Single(result.Report.UnmappedPaths).Property);
        Assert.Empty(result.Report.PreservedUnknownProperties);
        var expected = fixture.Replace("C:\\\\Games\\\\Library", "/games/Library", StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), SettingsMigration.ExportV10(await File.ReadAllBytesAsync(Destination)));
        Assert.Equal(fixture, await File.ReadAllTextAsync(Source));
    }

    [Fact]
    public async Task NativeSettingsRewriteOnlyMappedPathsAndPreserveUnknownAndUnmappedInBag()
    {
        await Seed("""{"Version":7,"DatabasePath":"c:\\GAMES\\Special\\猫 game","AutoBackupDir":"D:\\Backups","Future":{"Paths":["Z:\\Extras"],"value":null},"DisabledPlugins":["one","two"]}""");
        var original = await File.ReadAllBytesAsync(Source);
        var result = await SettingsMigration.MigrateAsync(Source, Destination, Mappings);
        var rewritten = Assert.Single(result.Report.RewrittenPaths);
        Assert.Equal("/special/猫 game", rewritten.Destination);
        Assert.Equal(2, result.Report.UnmappedPaths.Count);
        Assert.Contains(result.Report.UnmappedPaths, item => item.Property == "Future.Paths[0]");
        Assert.Equal(["Future"], result.Report.PreservedUnknownProperties);
        using var migrated = JsonDocument.Parse(await File.ReadAllBytesAsync(Destination));
        var settings = migrated.RootElement.GetProperty("settings");
        Assert.Equal("/special/猫 game", settings.GetProperty("DatabasePath").GetString());
        Assert.False(settings.TryGetProperty("Future", out _));
        Assert.False(settings.TryGetProperty("AutoBackupDir", out _));
        var legacy = migrated.RootElement.GetProperty("legacy");
        Assert.Equal(@"D:\Backups", legacy.GetProperty("AutoBackupDir").GetString());
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("Future").GetProperty("value").ValueKind);
        Assert.Equal(2, migrated.RootElement.GetProperty("report").GetProperty("unmappedPaths").GetArrayLength());
        Assert.Equal(original, await File.ReadAllBytesAsync(Source));
        var exported = Encoding.UTF8.GetString(SettingsMigration.ExportV10(await File.ReadAllBytesAsync(Destination)));
        Assert.Contains("\"DisabledPlugins\":[\"one\",\"two\"]", exported);
        Assert.Contains("D:\\\\Backups", exported);
    }

    [Theory]
    [InlineData(@"C:\GamesOther\x")]
    [InlineData(@"C:\Games\..\escape")]
    [InlineData(@"C:\Games\.\x")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"Z:\unknown")]
    [InlineData(@"C:Games")]
    [InlineData(@"C:")]
    [InlineData(@"\root-relative")]
    [InlineData(@"Games\relative")]
    public async Task UnmappedPathsAreNeverDroppedOrGuessed(string path)
    {
        await Seed(JsonSerializer.Serialize(new { DatabasePath = path }));
        var original = await File.ReadAllBytesAsync(Source);
        var result = await SettingsMigration.MigrateAsync(Source, Destination, Mappings);
        Assert.Empty(result.Report.RewrittenPaths);
        Assert.Equal(path, Assert.Single(result.Report.UnmappedPaths).Source);
        Assert.Equal(original, SettingsMigration.ExportV10(await File.ReadAllBytesAsync(Destination)));
    }

    [Theory]
    [InlineData("C:/Games/sub/game", "/games/sub/game")]
    [InlineData(@"C:\Games", "/games")]
    [InlineData(@"C:\Games\A B", "/games/A B")]
    public async Task MappingHonorsRootBoundariesAndNormalizesOnlyRewrittenSeparators(string path, string expected)
    {
        await Seed(JsonSerializer.Serialize(new { DatabasePath = path }));
        var result = await SettingsMigration.MigrateAsync(Source, Destination, Mappings);
        Assert.Equal(expected, Assert.Single(result.Report.RewrittenPaths).Destination);
    }

    [Fact]
    public async Task DryRunReportsWithoutCreatingOrChangingAnyFiles()
    {
        await Seed("""{"DatabasePath":"C:\\Games\\x","Future":false}""");
        Directory.Delete(Path.GetDirectoryName(Destination)!);
        var original = await File.ReadAllBytesAsync(Source);
        var result = await SettingsMigration.MigrateAsync(Source, Destination, Mappings, dryRun: true);
        Assert.True(result.DryRun);
        Assert.Single(result.Report.RewrittenPaths);
        Assert.Equal(["Future"], result.Report.PreservedUnknownProperties);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Destination)));
        Assert.Equal(original, await File.ReadAllBytesAsync(Source));
    }

    [Fact]
    public async Task RepeatedImportIsByteIdenticalAndDoesNotWriteAgain()
    {
        await Seed("""{"DatabasePath":"C:\\Games\\x","Version":7}""");
        await SettingsMigration.MigrateAsync(Source, Destination, Mappings);
        var bytes = await File.ReadAllBytesAsync(Destination);
        var result = await SettingsMigration.MigrateAsync(Source, Destination, Mappings.Reverse().ToArray(),
            checkpoint: _ => throw new InvalidOperationException("Must not write"));
        Assert.True(result.AlreadyMigrated);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task ExistingDifferentDestinationIsNeverOverwritten()
    {
        await Seed("{}");
        await File.WriteAllTextAsync(Destination, "existing");
        await Assert.ThrowsAsync<IOException>(() => SettingsMigration.MigrateAsync(Source, Destination, Mappings));
        Assert.Equal("existing", await File.ReadAllTextAsync(Destination));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Theory]
    [InlineData(SettingsWriteStep.BeforeWrite)]
    [InlineData(SettingsWriteStep.Written)]
    [InlineData(SettingsWriteStep.Flushed)]
    [InlineData(SettingsWriteStep.Verified)]
    [InlineData(SettingsWriteStep.Committed)]
    public async Task FailureAtEveryWriteBoundaryLeavesCleanOrCompleteDestination(SettingsWriteStep step)
    {
        await Seed("""{"Version":7,"DisabledPlugins":["a","b"]}""");
        var original = await File.ReadAllBytesAsync(Source);
        await Assert.ThrowsAsync<InjectedCrash>(() => SettingsMigration.MigrateAsync(Source, Destination, Mappings,
            checkpoint: current => { if (current == step) throw new InjectedCrash(); }));
        Assert.Equal(original, await File.ReadAllBytesAsync(Source));
        Assert.False(File.Exists(Destination + ".settings-migration.tmp"));
        Assert.Equal(step == SettingsWriteStep.Committed, File.Exists(Destination));
        var result = await SettingsMigration.MigrateAsync(Source, Destination, Mappings);
        Assert.Equal(step == SettingsWriteStep.Committed, result.AlreadyMigrated);
        Assert.Equal(original, SettingsMigration.ExportV10(await File.ReadAllBytesAsync(Destination)));
    }

    [Theory]
    [InlineData(SettingsWriteStep.BeforeWrite)]
    [InlineData(SettingsWriteStep.Written)]
    [InlineData(SettingsWriteStep.Flushed)]
    [InlineData(SettingsWriteStep.Verified)]
    public async Task CancellationBeforeCommitLeavesNoDestination(SettingsWriteStep step)
    {
        await Seed("{}");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SettingsMigration.MigrateAsync(Source, Destination, [],
            checkpoint: current => { if (current == step) cancellation.Cancel(); }, cancellationToken: cancellation.Token));
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Destination + ".settings-migration.tmp"));
    }

    [Fact]
    public async Task AbandonedPartialStageCanBeResumed()
    {
        await Seed("{}");
        await File.WriteAllTextAsync(Destination + ".settings-migration.tmp", "partial write from dead process");
        await SettingsMigration.MigrateAsync(Source, Destination, []);
        Assert.Equal("{}", Encoding.UTF8.GetString(SettingsMigration.ExportV10(await File.ReadAllBytesAsync(Destination))));
        Assert.False(File.Exists(Destination + ".settings-migration.tmp"));
    }

    [Theory]
    [InlineData(SettingsWriteStep.BeforeWrite)]
    [InlineData(SettingsWriteStep.Written)]
    [InlineData(SettingsWriteStep.Flushed)]
    [InlineData(SettingsWriteStep.Verified)]
    [InlineData(SettingsWriteStep.Committed)]
    public async Task ProcessDeathAtEveryWriteBoundaryIsRestartableWithoutManagedCleanup(SettingsWriteStep step)
    {
        await Seed("""{"Version":7,"DisabledPlugins":["a","b"]}""");
        var original = await File.ReadAllBytesAsync(Source);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var argument in new[]
        {
            "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Playnite.Core.Tests.runtimeconfig.json"),
            "--depsfile", Path.Combine(AppContext.BaseDirectory, "Playnite.Core.Tests.deps.json"),
            Path.Combine(AppContext.BaseDirectory, "SettingsMigration.CrashHost.dll"), Source, Destination, step.ToString()
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        Assert.NotEqual(0, process.ExitCode);
        Assert.Equal(step == SettingsWriteStep.Committed, File.Exists(Destination));
        Assert.Equal(step is not (SettingsWriteStep.BeforeWrite or SettingsWriteStep.Committed),
            File.Exists(Destination + ".settings-migration.tmp"));
        Assert.Equal(original, await File.ReadAllBytesAsync(Source));
        var result = await SettingsMigration.MigrateAsync(Source, Destination, []);
        Assert.Equal(step == SettingsWriteStep.Committed, result.AlreadyMigrated);
        Assert.Equal(original, SettingsMigration.ExportV10(await File.ReadAllBytesAsync(Destination)));
        Assert.False(File.Exists(Destination + ".settings-migration.tmp"));
    }

    [Theory]
    [InlineData("{\"Version\":7,\"Version\":8}")]
    [InlineData("{\"Version\":7} {}")]
    [InlineData("{\"Version\":")]
    [InlineData("[]")]
    public async Task InvalidOrAmbiguousSettingsNeverCreateDestination(string text)
    {
        await Seed(text);
        await Assert.ThrowsAnyAsync<Exception>(() => SettingsMigration.MigrateAsync(Source, Destination, []));
        Assert.False(File.Exists(Destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Fact]
    public async Task LexicalScalarAndCollectionBytesIncludingBomArePreserved()
    {
        var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\r\n \"Version\":7,\"Future\":[1e+2, -0.0, \"\\u732b\",null,false] } \r\n")).ToArray();
        await File.WriteAllBytesAsync(Source, original);
        await SettingsMigration.MigrateAsync(Source, Destination, []);
        Assert.Equal(original, SettingsMigration.ExportV10(await File.ReadAllBytesAsync(Destination)));
    }

    [Fact]
    public async Task SourceDirectoryDestinationIsRejected()
    {
        await Seed("{}");
        await Assert.ThrowsAsync<ArgumentException>(() => SettingsMigration.MigrateAsync(Source, Source + ".new", []));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Source)!));
    }

    [Fact]
    public async Task SourceChangeDuringMigrationRefusesCommit()
    {
        await Seed("{}");
        await Assert.ThrowsAsync<IOException>(() => SettingsMigration.MigrateAsync(Source, Destination, [], checkpoint: step =>
        {
            if (step == SettingsWriteStep.Verified) File.WriteAllText(Source, "{\"Version\":7}");
        }));
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Destination + ".settings-migration.tmp"));
    }

    [Fact]
    public async Task StagingSymlinkCannotWriteIntoSource()
    {
        await Seed("{}");
        File.CreateSymbolicLink(Destination + ".settings-migration.tmp", Source);
        await Assert.ThrowsAsync<IOException>(() => SettingsMigration.MigrateAsync(Source, Destination, []));
        Assert.Equal("{}", await File.ReadAllTextAsync(Source));
        Assert.False(File.Exists(Destination));
    }

    [Theory]
    [InlineData("relative", "/games")]
    [InlineData(@"C:\Games", "relative")]
    [InlineData(@"C:\Games", "/games/../elsewhere")]
    public async Task InvalidMappingIsRejectedBeforeWriting(string windows, string linux)
    {
        await Seed("{}");
        await Assert.ThrowsAsync<ArgumentException>(() => SettingsMigration.MigrateAsync(Source, Destination, [new(windows, linux)]));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Fact]
    public async Task DuplicateMappingsAreRejectedRatherThanDependingOnInputOrder()
    {
        await Seed("{}");
        await Assert.ThrowsAsync<ArgumentException>(() => SettingsMigration.MigrateAsync(Source, Destination,
            [new(@"C:\Games", "/one"), new(@"c:\games", "/two")]));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task ScalarAndOrderedCollectionRoundTripProperty()
    {
        var random = new Random(4004);
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var fixture = JsonSerializer.Serialize(new
            {
                Version = random.Next(),
                DisabledPlugins = Enumerable.Range(0, random.Next(20)).Select(_ => "猫\\\"" + random.NextInt64()).ToArray(),
                Language = iteration % 2 == 0 ? null : "en_US",
                BackgroundImageDarkAmount = random.NextDouble()
            });
            await Seed(fixture);
            var destination = Destination + iteration;
            await SettingsMigration.MigrateAsync(Source, destination, []);
            Assert.Equal(Encoding.UTF8.GetBytes(fixture), SettingsMigration.ExportV10(await File.ReadAllBytesAsync(destination)));
        }
    }

    [Fact]
    public async Task LosslessExportRejectsDivergenceFromNativeSettings()
    {
        await Seed("""{"Version":7}""");
        await SettingsMigration.MigrateAsync(Source, Destination, []);
        var envelope = await File.ReadAllTextAsync(Destination);
        var changed = envelope.Replace("\"Version\":7", "\"Version\":8", StringComparison.Ordinal);
        Assert.NotEqual(envelope, changed);
        Assert.Throws<InvalidDataException>(() => SettingsMigration.ExportV10(Encoding.UTF8.GetBytes(changed)));
    }

    [Fact]
    public async Task EntireV10FixtureLibraryRemainsByteIdentical()
    {
        await Seed("{}");
        var fixture = Serialization.RepositoryPaths.Combine("tests", "fixtures", "v10", "library");
        var files = Directory.GetFiles(fixture, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var snapshots = new Dictionary<string, byte[]>();
        foreach (var file in files)
        {
            var target = Path.Combine(Path.GetDirectoryName(Source)!, "library", Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = await File.ReadAllBytesAsync(file);
            await File.WriteAllBytesAsync(target, bytes);
            snapshots.Add(target, bytes);
        }
        snapshots.Add(Source, await File.ReadAllBytesAsync(Source));
        await SettingsMigration.MigrateAsync(Source, Destination, []);
        Assert.Equal(snapshots.Count, Directory.GetFiles(Path.GetDirectoryName(Source)!, "*", SearchOption.AllDirectories).Length);
        foreach (var (file, bytes) in snapshots) Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
    }

    private Task Seed(string text) => File.WriteAllTextAsync(Source, text);
    public void Dispose() => Directory.Delete(directory, recursive: true);
    private sealed class InjectedCrash : Exception;
}
