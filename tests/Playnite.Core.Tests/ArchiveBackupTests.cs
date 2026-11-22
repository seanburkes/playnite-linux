using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class ArchiveBackupTests : IDisposable
{
    private readonly string root = Path.Combine("/tmp/opencode", "archive-tests-" + Path.GetRandomFileName());
    private static readonly DateTimeOffset Timestamp = new(2021, 7, 4, 1, 1, 2, TimeSpan.Zero);
    private static readonly BackupDataItem[] All = Enum.GetValues<BackupDataItem>();
    private string Data => Path.Combine(root, "data");
    private string Library => Path.Combine(root, "library");
    private string Work => Path.Combine(root, "work");
    private string Zip => Path.Combine(root, "backup.zip");
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "archive-v10", "backup.zip");

    public ArchiveBackupTests()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Work);
    }

    public void Dispose() => Directory.Delete(root, true);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private BackupOptions CreateOptions(string? output = null, bool overwrite = false,
        int rotating = 0, BackupDataItem[]? items = null) => new()
    {
        DataDir = Data, LibraryDir = Library, OutputFile = output ?? Zip,
        Overwrite = overwrite, RotatingBackups = rotating, BackupItems = items ?? All
    };

    private BackupRestoreOptions RestoreOptions(string? zip = null, bool overwrite = false,
        BackupDataItem[]? items = null, string? work = null) => new()
    {
        BackupFile = zip ?? Zip, DataDir = Data, LibraryDir = Library,
        WorkDirectory = work ?? Work, RestoreItems = items ?? All, Overwrite = overwrite
    };

    private static void MakeZip(string path, params (string Name, string Content)[] members)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in members)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using var output = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            output.Write(content);
        }
    }

    [Fact]
    public async Task V10_windows_layout_imports_real_opaque_database_and_round_trips()
    {
        var report = await Backup.RestoreBackupAsync(RestoreOptions(Fixture));
        Assert.True(report.LegacyFormat);
        Assert.Single(report.Warnings);
        Assert.Equal(All.Order(), Backup.GetRestoreSelections(Fixture).Order());
        var expected = Directory.GetFiles(Data, "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Library, "*", SearchOption.AllDirectories))
            .ToDictionary(p => p, File.ReadAllBytes);
        Assert.StartsWith("SQLite format 3", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Library, "games.db"))));
        await Backup.BackupDataAsync(CreateOptions(), Timestamp);
        Assert.Equal(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "archive-listing.txt")), Archive.GetArchiveFiles(Zip));
        using (var zip = ZipFile.OpenRead(Zip))
        {
            Assert.All(zip.Entries, e => Assert.Equal(Timestamp.DateTime, e.LastWriteTime.DateTime));
        }

        Directory.Delete(Data, true);
        Directory.Delete(Library, true);
        report = await Backup.RestoreBackupAsync(RestoreOptions());
        Assert.False(report.LegacyFormat);
        Assert.Empty(report.Warnings);
        foreach (var (path, bytes) in expected)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void Fixture_provenance_checks_the_exact_bytes_used()
    {
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "archive-v10", "provenance.json")));
        Assert.Equal(provenance.RootElement.GetProperty("archiveSha256").GetString(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Fixture))).ToLowerInvariant());
        using var zip = ZipFile.OpenRead(Fixture);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            Assert.Equal(provenance.RootElement.GetProperty("members").GetProperty(entry.FullName.Replace('\\', '/')).GetString(),
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
    }

    [Fact]
    public async Task Always_includes_settings_and_library_but_not_unselected_optional_data_or_default_themes()
    {
        Write(Path.Combine(Data, "config.json"), "{}");
        Write(Path.Combine(Data, "fullscreenConfig.json"), "{}");
        Write(Path.Combine(Data, "cache/log.txt"), "excluded");
        Write(Path.Combine(Library, "games.db"), "db");
        Write(Path.Combine(Library, "files/id/image"), "optional");
        Write(Path.Combine(Data, "Themes/Desktop/Default/theme.yaml"), "excluded");
        Write(Path.Combine(Data, "Themes/Desktop/Custom/theme.yaml"), "custom");
        Write(Path.Combine(Data, "Themes/Desktop/loose.txt"), "excluded");
        Write(Path.Combine(Data, "Themes/Unknown/Custom/theme.yaml"), "excluded");
        await Backup.BackupDataAsync(CreateOptions(items: []), Timestamp);
        Assert.Equal(["config.json", "fullscreenConfig.json", "library/games.db", Backup.ManifestName], Archive.GetArchiveFiles(Zip));
        await Backup.BackupDataAsync(CreateOptions(overwrite: true, items: [BackupDataItem.Themes]), Timestamp);
        Assert.Equal(["config.json", "fullscreenConfig.json", "library/games.db",
            "themes/Desktop/Custom/theme.yaml", Backup.ManifestName], Archive.GetArchiveFiles(Zip));
    }

    [Fact]
    public async Task Identical_sources_and_clock_produce_identical_archive_bytes()
    {
        Write(Path.Combine(Data, "config.json"), "{\"a\":1}");
        await Backup.BackupDataAsync(CreateOptions(), Timestamp);
        var other = Path.Combine(root, "other.zip");
        await Backup.BackupDataAsync(CreateOptions(other), Timestamp);
        Assert.Equal(File.ReadAllBytes(Zip), File.ReadAllBytes(other));
    }

    [Fact]
    public async Task Legacy_missing_members_are_permissive_and_reported()
    {
        MakeZip(Zip, ("unrecognized.txt", "ignored"));
        var report = await Backup.RestoreBackupAsync(RestoreOptions());
        Assert.True(report.LegacyFormat);
        Assert.Equal(5, report.Warnings.Count);
        Assert.False(Directory.Exists(Data));
        Assert.False(Directory.Exists(Library));
    }

    [Fact]
    public async Task Restore_existing_target_requires_explicit_overwrite_and_preserves_defaults()
    {
        Write(Path.Combine(Data, "config.json"), "old");
        Write(Path.Combine(Library, "obsolete.db"), "old");
        Write(Path.Combine(Library, "files/keep"), "media");
        Write(Path.Combine(Data, "Themes/Desktop/Default/theme.yaml"), "default");
        Write(Path.Combine(Data, "Themes/Desktop/Obsolete/theme.yaml"), "old");
        await Assert.ThrowsAsync<IOException>(() => Backup.RestoreBackupAsync(RestoreOptions(Fixture)));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Data, "config.json")));
        await Backup.RestoreBackupAsync(RestoreOptions(Fixture, overwrite: true));
        Assert.False(File.Exists(Path.Combine(Library, "obsolete.db")));
        Assert.False(File.Exists(Path.Combine(Library, "files/keep")));
        Assert.False(Directory.Exists(Path.Combine(Data, "Themes/Desktop/Obsolete")));
        Assert.Equal("default", File.ReadAllText(Path.Combine(Data, "Themes/Desktop/Default/theme.yaml")));
    }

    [Fact]
    public async Task Selected_library_restore_does_not_delete_media_or_unselected_settings()
    {
        Write(Path.Combine(Data, "config.json"), "keep");
        Write(Path.Combine(Library, "files/keep"), "keep");
        Write(Path.Combine(Library, "obsolete"), "delete");
        await Backup.RestoreBackupAsync(RestoreOptions(Fixture, true, [BackupDataItem.Library]));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Data, "config.json")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Library, "files/keep")));
        Assert.False(File.Exists(Path.Combine(Library, "obsolete")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("library/../../escape")]
    [InlineData("library\\..\\escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("\\\\server\\file")]
    [InlineData("library//empty")]
    [InlineData("library/./dot")]
    [InlineData("library/a:stream")]
    [InlineData("library/\u0001control")]
    public async Task Traversal_and_nonportable_names_are_rejected_before_target_writes(string name)
    {
        MakeZip(Zip, ("config.json", "new"), (name, "bad"));
        Write(Path.Combine(Data, "config.json"), "keep");
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(RestoreOptions(overwrite: true)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Data, "config.json")));
        Assert.Empty(Directory.GetFileSystemEntries(Work));
        Assert.Throws<InvalidDataException>(() => Archive.GetArchiveFiles(Zip));
    }

    [Theory]
    [InlineData("library/a", "LIBRARY/A")]
    [InlineData("library/a", "library\\a")]
    [InlineData("library/a", "library/a/b")]
    public async Task Duplicate_and_file_directory_collisions_fail_before_writing(string first, string second)
    {
        MakeZip(Zip, (first, "a"), (second, "b"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(RestoreOptions()));
        Assert.False(Directory.Exists(Library));
    }

    [Theory]
    [InlineData(1, 100, 100)]
    [InlineData(10, 3, 100)]
    [InlineData(10, 100, 5)]
    public async Task Declared_entry_count_per_entry_and_total_limits_are_enforced(int count, long entry, long total)
    {
        MakeZip(Zip, ("config.json", "1234"), ("fullscreenConfig.json", "5678"));
        var limits = new ArchiveLimits(count, entry, total);
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(RestoreOptions(), limits));
        Assert.Throws<InvalidDataException>(() => Archive.GetArchiveFiles(Zip, limits));
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public async Task Highly_compressed_payload_is_rejected_without_extracting()
    {
        using (var zip = ZipFile.Open(Zip, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("library/bomb", CompressionLevel.SmallestSize).Open())
        {
            stream.Write(new byte[1_048_576]);
        }

        Assert.True(new FileInfo(Zip).Length < 4096);
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(RestoreOptions(),
            new ArchiveLimits(10, 4096, 8192)));
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Theory]
    [InlineData("wrong-version")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("hash")]
    [InlineData("null")]
    [InlineData("malformed")]
    public async Task Version_manifest_members_and_integrity_validate_before_destructive_restore(string fault)
    {
        Write(Path.Combine(Data, "config.json"), "new");
        await Backup.BackupDataAsync(CreateOptions(), Timestamp);
        using (var zip = ZipFile.Open(Zip, ZipArchiveMode.Update))
        {
            var manifest = zip.GetEntry(Backup.ManifestName)!;
            using var reader = new StreamReader(manifest.Open());
            var text = reader.ReadToEnd();
            reader.Close();
            manifest.Delete();
            text = fault switch
            {
                "wrong-version" => text.Replace("\"formatVersion\":1", "\"formatVersion\":99"),
                "hash" => text.Replace(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("new"))), new string('0', 64)),
                "null" => "null",
                "malformed" => "{",
                _ => text
            };
            if (fault == "missing") zip.GetEntry("config.json")!.Delete();
            if (fault == "extra") zip.CreateEntry("library/extra");
            using var writer = new StreamWriter(zip.CreateEntry(Backup.ManifestName).Open());
            writer.Write(text);
        }

        Write(Path.Combine(Data, "config.json"), "keep");
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(RestoreOptions(overwrite: true)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Data, "config.json")));
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Fact]
    public async Task Unselected_corrupt_member_also_blocks_restore_and_cleans_staging()
    {
        MakeZip(Zip, ("config.json", "new"), ("library/data", "unique-bad-payload"));
        var bytes = File.ReadAllBytes(Zip);
        var offset = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("unique-bad-payload"));
        Assert.True(offset > 0);
        bytes[offset] ^= 1;
        File.WriteAllBytes(Zip, bytes);
        Write(Path.Combine(Data, "config.json"), "keep");
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(overwrite: true, items: [BackupDataItem.Settings])));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Data, "config.json")));
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Fact]
    public async Task Truncated_archive_is_rejected_before_writes()
    {
        var bytes = File.ReadAllBytes(Fixture);
        File.WriteAllBytes(Zip, bytes[..(bytes.Length / 2)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(RestoreOptions()));
        Assert.False(Directory.Exists(Data));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Symbolic_links_in_archive_or_destination_are_rejected(bool archiveLink)
    {
        MakeZip(Zip, ("library/data", "payload"));
        if (archiveLink)
        {
            using var zip = ZipFile.Open(Zip, ZipArchiveMode.Update);
            zip.GetEntry("library/data")!.ExternalAttributes = unchecked((int)0xA1FF0000);
        }
        else
        {
            Directory.CreateSymbolicLink(Library, Work);
        }

        if (archiveLink)
            await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(RestoreOptions(overwrite: true)));
        else
            await Assert.ThrowsAsync<IOException>(() => Backup.RestoreBackupAsync(RestoreOptions(overwrite: true)));
        Assert.Empty(Directory.GetFileSystemEntries(Work));
        if (!archiveLink) Directory.Delete(Library);
    }

    [Fact]
    public async Task Source_symlink_and_case_collisions_are_rejected()
    {
        Directory.CreateDirectory(Data);
        File.CreateSymbolicLink(Path.Combine(Data, "config.json"), Path.Combine(Fixture));
        await Assert.ThrowsAsync<IOException>(() => Backup.BackupDataAsync(CreateOptions(), Timestamp));
        File.Delete(Path.Combine(Data, "config.json"));
        Write(Path.Combine(Library, "A"), "a");
        Write(Path.Combine(Library, "a"), "b");
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.BackupDataAsync(CreateOptions(), Timestamp));
        Assert.False(File.Exists(Zip));
    }

    [Fact]
    public async Task Restore_work_directory_cannot_be_inside_a_selected_target()
    {
        Directory.CreateDirectory(Library);
        await Assert.ThrowsAsync<IOException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(Fixture, true, work: Library)));
        Assert.Empty(Directory.GetFileSystemEntries(Library));
    }

    [Fact]
    public async Task Existing_backup_is_preserved_unless_explicitly_overwritten()
    {
        File.WriteAllText(Zip, "keep");
        await Assert.ThrowsAsync<IOException>(() => Backup.BackupDataAsync(CreateOptions(), Timestamp));
        Assert.Equal("keep", File.ReadAllText(Zip));
        await Backup.BackupDataAsync(CreateOptions(overwrite: true), Timestamp);
        Assert.Equal([Backup.ManifestName], Archive.GetArchiveFiles(Zip));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    [InlineData(10, 4)]
    public async Task V10_rotation_keeps_current_plus_requested_older_backups(int rotating, int expected)
    {
        for (var day = 1; day <= 3; day++)
            File.WriteAllText(Path.Combine(root, $"PlayniteBackup-2021-07-0{day}-01-01-02.zip"), "");
        var output = Path.Combine(root, Backup.GetAutoBackupFileName(Timestamp));
        await Backup.BackupDataAsync(CreateOptions(output, rotating: rotating), Timestamp);
        Assert.Equal(expected, Directory.GetFiles(root, "*.zip").Length);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task Cancellation_preserves_existing_backup_and_restore_targets()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        File.WriteAllText(Zip, "keep");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Backup.BackupDataAsync(
            CreateOptions(overwrite: true), Timestamp, cancellationToken: cancellation.Token));
        Assert.Equal("keep", File.ReadAllText(Zip));
        Write(Path.Combine(Data, "config.json"), "keep");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(Fixture, true), cancellationToken: cancellation.Token));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Data, "config.json")));
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Fact]
    public async Task Archive_entry_stream_owns_no_archive_handle_and_missing_member_returns_null()
    {
        File.Copy(Fixture, Zip);
        using var stream = await Archive.GetEntryStreamAsync(Zip, "config.json");
        Assert.NotNull(stream);
        Assert.Equal(0, stream.Position);
        File.Delete(Zip);
        using var reader = new StreamReader(stream);
        Assert.Contains("DatabasePath", await reader.ReadToEndAsync());
        Assert.Null(await Archive.GetEntryStreamAsync(Fixture, "missing"));
    }

    [Fact]
    public async Task Seeded_member_round_trips_preserve_bytes_and_reject_traversal_mutations()
    {
        var random = new Random(8008);
        for (var i = 0; i < 24; i++)
        {
            var payload = Convert.ToHexString(Enumerable.Range(0, random.Next(0, 1000))
                .Select(_ => (byte)random.Next(256)).ToArray());
            var name = $"extension/id-{i}/日本語-{random.Next()}.txt";
            MakeZip(Zip, (name, payload));
            await Backup.RestoreBackupAsync(RestoreOptions(overwrite: true));
            Assert.Equal(payload, File.ReadAllText(Path.Combine(Data, "Extensions", name["extension/".Length..])));
            File.Delete(Zip);
            MakeZip(Zip, (name.Replace("/日本語", "/../日本語"), payload));
            await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(RestoreOptions(overwrite: true)));
            File.Delete(Zip);
        }
    }

    [Fact]
    public async Task Forged_uncompressed_length_is_rejected_by_actual_byte_or_integrity_checks()
    {
        MakeZip(Zip, ("config.json", new string('x', 8192)));
        var bytes = File.ReadAllBytes(Zip);
        var central = bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        Assert.True(central > 0);
        BitConverter.GetBytes(1U).CopyTo(bytes, central + 24);
        File.WriteAllBytes(Zip, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(RestoreOptions(),
            new ArchiveLimits(10, 4096, 4096)));
        Assert.False(Directory.Exists(Data));
        Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Theory]
    [InlineData(1, 10000, 10000)]
    [InlineData(10, 3, 10000)]
    [InlineData(10, 4, 10000)]
    [InlineData(10, 10000, 3)]
    public async Task Create_enforces_limits_and_removes_incomplete_output(int count, long entry, long total)
    {
        Write(Path.Combine(Data, "config.json"), "1234");
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.BackupDataAsync(CreateOptions(), Timestamp,
            new ArchiveLimits(count, entry, total)));
        Assert.False(File.Exists(Zip));
        Assert.Empty(Directory.GetFiles(root, "backup.zip.*"));
    }

    [Fact]
    public async Task Directory_at_a_settings_file_target_is_rejected_before_any_other_category_changes()
    {
        Directory.CreateDirectory(Path.Combine(Data, "config.json"));
        Write(Path.Combine(Library, "keep"), "keep");
        await Assert.ThrowsAsync<IOException>(() => Backup.RestoreBackupAsync(RestoreOptions(Fixture, true)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Library, "keep")));
    }

    [Fact]
    public async Task Seeded_truncated_zip_corpus_cannot_create_targets()
    {
        var bytes = File.ReadAllBytes(Fixture);
        var random = new Random(8009);
        for (var i = 0; i < 32; i++)
        {
            File.WriteAllBytes(Zip, bytes[..random.Next(1, bytes.Length / 2)]);
            await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(RestoreOptions()));
            Assert.False(Directory.Exists(Data));
            Assert.Empty(Directory.GetFileSystemEntries(Work));
        }
    }
}
