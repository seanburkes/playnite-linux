using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class BackupTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly BackupDataItem[] AllItems =
    [
        BackupDataItem.Settings, BackupDataItem.Library, BackupDataItem.LibraryFiles,
        BackupDataItem.Extensions, BackupDataItem.Themes, BackupDataItem.ExtensionsData
    ];

    private static readonly string[] AllEntries =
    [
        "config.json",
        "extension/TestAddon/TestAddon.dll",
        "extension/TestAddon/extension.yaml",
        "extensiondata/TestAddon/data.json",
        "fullscreenConfig.json",
        "library/database.json",
        "library/games.db",
        "libraryfiles/Backgrounds/background.jpg",
        "libraryfiles/cover.png",
        "themes/Desktop/Custom/Views/Main.xaml",
        "themes/Desktop/Custom/theme.yaml",
        "themes/Fullscreen/Custom/theme.yaml",
        "backup-manifest.json"
    ];

    [Fact]
    public async Task Round_trip_backup_and_restore_preserves_every_selected_byte()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        await Backup.BackupDataAsync(Options(scratch, scratch.Backup, AllItems), Timestamp);
        Assert.Equal(AllEntries, Archive.GetArchiveFiles(scratch.Backup));

        var report = await Backup.RestoreBackupAsync(RestoreOptions(scratch, AllItems), limits: null);
        Assert.False(report.LegacyFormat);
        Assert.Empty(report.Warnings);
        ArchiveTestData.AssertTree(ExpectedData(scratch), scratch.RestoreData);
        ArchiveTestData.AssertTree(ArchiveTestData.ReadTree(scratch.Library), scratch.RestoreLibrary);
    }

    [Fact]
    public async Task V10_fixture_restores_with_its_layout_and_reports_a_legacy_archive()
    {
        using var scratch = new Scratch();
        var fixture = ArchiveTestData.Fixture("v10-full.zip");
        var report = await Backup.RestoreBackupAsync(
            RestoreOptions(scratch, AllItems, backupFile: fixture));
        Assert.True(report.LegacyFormat);
        Assert.Contains("Legacy v10 backup: no version or integrity manifest.", report.Warnings);
        var (data, library) = FixtureTrees(fixture);
        ArchiveTestData.AssertTree(data, scratch.RestoreData);
        ArchiveTestData.AssertTree(library, scratch.RestoreLibrary);
    }

    [Fact]
    public async Task Backup_naming_matches_v10_and_output_directory_derives_the_file_name()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        Assert.Equal("PlayniteBackup-2026-11-15-12-00-00.zip", Backup.GetAutoBackupFileName(Timestamp));
        var options = new BackupOptions
        {
            DataDir = scratch.Data,
            LibraryDir = scratch.Library,
            OutputDir = scratch.Root,
            BackupItems = [BackupDataItem.Settings, BackupDataItem.Library]
        };
        await Backup.BackupDataAsync(options, Timestamp);
        var expected = Path.Combine(scratch.Root, "PlayniteBackup-2026-11-15-12-00-00.zip");
        Assert.True(File.Exists(expected));
        Assert.Equal(["config.json", "fullscreenConfig.json", "library/database.json",
            "library/games.db", "backup-manifest.json"], Archive.GetArchiveFiles(expected));
    }

    [Fact]
    public async Task Backup_only_packs_library_root_files_and_unselected_roots_are_absent()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        await Backup.BackupDataAsync(Options(scratch, scratch.Backup), Timestamp);
        Assert.Equal(["config.json", "fullscreenConfig.json", "library/database.json",
            "library/games.db", "backup-manifest.json"], Archive.GetArchiveFiles(scratch.Backup));
    }

    [Fact]
    public async Task Themes_are_packed_only_for_desktop_and_fullscreen_and_default_is_excluded()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        await Backup.BackupDataAsync(Options(scratch, scratch.Backup, AllItems), Timestamp);
        var entries = Archive.GetArchiveFiles(scratch.Backup);
        Assert.Contains("themes/Desktop/Custom/theme.yaml", entries);
        Assert.Contains("themes/Desktop/Custom/Views/Main.xaml", entries);
        Assert.Contains("themes/Fullscreen/Custom/theme.yaml", entries);
        Assert.DoesNotContain("themes/Desktop/Default/theme.yaml", entries);
        Assert.DoesNotContain("themes/Desktop/readme.txt", entries);
        Assert.DoesNotContain("themes/Other/theme.yaml", entries);
    }

    [Fact]
    public async Task Automatic_rotation_keeps_the_current_backup_plus_the_requested_count()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        var options = new BackupOptions
        {
            DataDir = scratch.Data,
            LibraryDir = scratch.Library,
            OutputDir = scratch.Root,
            RotatingBackups = 1,
            BackupItems = [BackupDataItem.Settings, BackupDataItem.Library]
        };
        await Backup.BackupDataAsync(options, Timestamp);
        await Backup.BackupDataAsync(options, Timestamp.AddSeconds(1));
        Assert.Equal(2, Directory.GetFiles(scratch.Root, "PlayniteBackup-*.zip").Length);
        await Backup.BackupDataAsync(options, Timestamp.AddSeconds(2));
        var names = Directory.GetFiles(scratch.Root, "PlayniteBackup-*.zip")
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["PlayniteBackup-2026-11-15-12-00-01.zip",
            "PlayniteBackup-2026-11-15-12-00-02.zip"], names);
    }

    [Fact]
    public async Task Existing_backup_output_fails_without_overwrite_and_is_replaced_with_it()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        await Backup.BackupDataAsync(Options(scratch, scratch.Backup), Timestamp);
        var before = File.ReadAllBytes(scratch.Backup);
        await Assert.ThrowsAsync<IOException>(() =>
            Backup.BackupDataAsync(Options(scratch, scratch.Backup), Timestamp));
        Assert.Equal(before, File.ReadAllBytes(scratch.Backup));
        await Backup.BackupDataAsync(Options(scratch, scratch.Backup, overwrite: true), Timestamp);
        Assert.True(File.Exists(scratch.Backup));
    }

    [Fact]
    public async Task Existing_restore_target_fails_without_overwrite_and_leaves_it_untouched()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        await Backup.BackupDataAsync(Options(scratch, scratch.Backup, AllItems), Timestamp);
        Directory.CreateDirectory(scratch.RestoreLibrary);
        File.WriteAllText(Path.Combine(scratch.RestoreLibrary, "old.db"), "old");
        await Assert.ThrowsAsync<IOException>(() =>
            Backup.RestoreBackupAsync(RestoreOptions(scratch, AllItems)));
        Assert.Equal("old", File.ReadAllText(Path.Combine(scratch.RestoreLibrary, "old.db")));
        Assert.False(Directory.Exists(scratch.RestoreData));
    }

    [Fact]
    public async Task Overwrite_restore_replaces_selected_roots_but_keeps_the_default_theme()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        await Backup.BackupDataAsync(Options(scratch, scratch.Backup, AllItems), Timestamp);
        Directory.CreateDirectory(scratch.RestoreLibrary);
        File.WriteAllText(Path.Combine(scratch.RestoreLibrary, "old.db"), "old");
        ArchiveTestData.Write(scratch.RestoreData, "Extensions/Old/old.dll", "old");
        ArchiveTestData.Write(scratch.RestoreData, "Themes/Desktop/Default/theme.yaml", "built-in");
        ArchiveTestData.Write(scratch.RestoreData, "Themes/Desktop/Old/theme.yaml", "old");
        await Backup.RestoreBackupAsync(RestoreOptions(scratch, AllItems, overwrite: true));
        Assert.False(File.Exists(Path.Combine(scratch.RestoreLibrary, "old.db")));
        Assert.False(File.Exists(Path.Combine(scratch.RestoreData, "Extensions/Old/old.dll")));
        Assert.False(File.Exists(Path.Combine(scratch.RestoreData, "Themes/Desktop/Old/theme.yaml")));
        Assert.Equal("built-in", File.ReadAllText(
            Path.Combine(scratch.RestoreData, "Themes/Desktop/Default/theme.yaml")));
        var expected = ExpectedData(scratch);
        expected["Themes/Desktop/Default/theme.yaml"] = Encoding.UTF8.GetBytes("built-in");
        ArchiveTestData.AssertTree(expected, scratch.RestoreData);
        ArchiveTestData.AssertTree(ArchiveTestData.ReadTree(scratch.Library), scratch.RestoreLibrary);
    }

    [Theory]
    [InlineData("v11-wrong-version.zip")]
    [InlineData("v11-member-missing.zip")]
    [InlineData("v11-hash-mismatch.zip")]
    public async Task Manifest_validation_failures_are_rejected_before_any_live_write(string fixture)
    {
        using var scratch = new Scratch();
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(scratch, AllItems, backupFile: ArchiveTestData.Fixture(fixture))));
        Assert.False(Directory.Exists(scratch.RestoreData));
        Assert.False(Directory.Exists(scratch.RestoreLibrary));
    }

    [Fact]
    public async Task Truncated_and_traversal_archives_are_rejected()
    {
        using var scratch = new Scratch();
        var partial = Path.Combine(scratch.Root, "partial.zip");
        File.WriteAllBytes(partial, ArchiveTestData.Truncated(
            File.ReadAllBytes(ArchiveTestData.Fixture("v11-full.zip")), 0.5));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(scratch, AllItems, backupFile: partial)));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(scratch, AllItems, backupFile: ArchiveTestData.Fixture("traversal.zip"))));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(scratch, AllItems, backupFile: ArchiveTestData.Fixture("duplicate.zip"))));
        Assert.False(Directory.Exists(scratch.RestoreData));
    }

    [Fact]
    public async Task A_corrupt_member_crc_is_rejected_before_any_live_write()
    {
        using var scratch = new Scratch();
        var archive = Path.Combine(scratch.Root, "corrupt.zip");
        var payload = Encoding.UTF8.GetBytes("payload-for-crc");
        ArchiveTestData.CreateZip(archive, ("config.json", Encoding.UTF8.GetString(payload)));
        var bytes = File.ReadAllBytes(archive);
        var index = IndexOf(bytes, payload);
        Assert.True(index >= 0);
        bytes[index] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            Backup.RestoreBackupAsync(RestoreOptions(scratch, [BackupDataItem.Settings], backupFile: archive)));
        Assert.False(Directory.Exists(scratch.RestoreData));
    }

    [Fact]
    public async Task Restore_limits_reject_entry_count_per_entry_and_total_sizes()
    {
        using var scratch = new Scratch();
        var fixture = ArchiveTestData.Fixture("v11-full.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(scratch, AllItems, backupFile: fixture), new ArchiveLimits(EntryCount: 2)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(scratch, AllItems, backupFile: fixture), new ArchiveLimits(EntryBytes: 10)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(scratch, AllItems, backupFile: fixture), new ArchiveLimits(TotalBytes: 20)));
        Assert.False(Directory.Exists(scratch.RestoreData));
    }

    [Fact]
    public async Task Backup_limits_reject_the_same_entry_count_and_size_bounds()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        var output = Path.Combine(scratch.Root, "limited.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.BackupDataAsync(
            Options(scratch, output), Timestamp, new ArchiveLimits(EntryCount: 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.BackupDataAsync(
            Options(scratch, output), Timestamp, new ArchiveLimits(EntryBytes: 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Backup.BackupDataAsync(
            Options(scratch, output), Timestamp, new ArchiveLimits(TotalBytes: 10)));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Restore_requires_an_existing_work_directory()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        await Backup.BackupDataAsync(Options(scratch, scratch.Backup), Timestamp);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Backup.RestoreBackupAsync(
            RestoreOptions(scratch, [BackupDataItem.Settings, BackupDataItem.Library],
                workDirectory: Path.Combine(scratch.Root, "missing-work"))));
    }

    [Fact]
    public async Task Unknown_members_are_reported_and_not_restored()
    {
        using var scratch = new Scratch();
        var archive = Path.Combine(scratch.Root, "with-extra.zip");
        File.Copy(ArchiveTestData.Fixture("v10-minimal.zip"), archive);
        ArchiveTestData.AddZipEntry(archive, "unexpected.txt", "ignored");
        var report = await Backup.RestoreBackupAsync(
            RestoreOptions(scratch, [BackupDataItem.Settings, BackupDataItem.Library], backupFile: archive));
        Assert.Contains("Unrecognized member ignored: unexpected.txt", report.Warnings);
        Assert.Contains("Missing optional v10 member: fullscreenConfig.json", report.Warnings);
        Assert.False(File.Exists(Path.Combine(scratch.RestoreData, "unexpected.txt")));
    }

    [Fact]
    public async Task Legacy_archives_without_optional_members_get_v10_shaped_warnings()
    {
        using var scratch = new Scratch();
        var report = await Backup.RestoreBackupAsync(RestoreOptions(
            scratch, [BackupDataItem.Settings, BackupDataItem.Library],
            backupFile: ArchiveTestData.Fixture("v10-extensiondata-only.zip")));
        Assert.True(report.LegacyFormat);
        Assert.Contains("Missing optional v10 member: config.json", report.Warnings);
        Assert.Contains("Missing optional v10 member: fullscreenConfig.json", report.Warnings);
        Assert.Contains("Missing optional v10 library members.", report.Warnings);
    }

    [Fact]
    public void Restore_selections_preserve_v10_presence_checks_and_the_extension_prefix_quirk()
    {
        var all = new[]
        {
            BackupDataItem.Settings, BackupDataItem.Library, BackupDataItem.LibraryFiles,
            BackupDataItem.Extensions, BackupDataItem.ExtensionsData, BackupDataItem.Themes
        };
        Assert.Equal(all, Backup.GetRestoreSelections(ArchiveTestData.Fixture("v10-full.zip")));
        Assert.Equal([BackupDataItem.Settings, BackupDataItem.Library],
            Backup.GetRestoreSelections(ArchiveTestData.Fixture("v10-minimal.zip")));
        Assert.Equal([BackupDataItem.Settings, BackupDataItem.Library,
            BackupDataItem.Extensions, BackupDataItem.ExtensionsData],
            Backup.GetRestoreSelections(ArchiveTestData.Fixture("v10-extensiondata-only.zip")));
        Assert.Equal(all, Backup.GetRestoreSelections(ArchiveTestData.Fixture("v11-full.zip")));
    }

    [Fact]
    public async Task Manifest_is_deterministic_and_records_every_member_size_and_hash()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        var first = Path.Combine(scratch.Root, "first.zip");
        var second = Path.Combine(scratch.Root, "second.zip");
        await Backup.BackupDataAsync(Options(scratch, first, AllItems), Timestamp);
        await Backup.BackupDataAsync(Options(scratch, second, AllItems), Timestamp);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(ArchiveTestData.EntryBytes(
            first, Backup.ManifestName), options)!;
        Assert.Equal(Backup.FormatVersion, manifest.FormatVersion);
        var entries = Archive.GetArchiveFiles(first);
        Assert.Equal(entries.Count - 1, manifest.Members.Count);
        foreach (var member in manifest.Members)
        {
            var bytes = ArchiveTestData.EntryBytes(first, member.Name);
            Assert.Equal(bytes.LongLength, member.Size);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), member.Sha256);
        }
    }

    [Fact]
    public async Task Symbolic_links_in_sources_and_restore_targets_are_rejected()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        var outside = Path.Combine(scratch.Root, "outside.txt");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(scratch.Library, "link.db"), outside);
        await Assert.ThrowsAsync<IOException>(() =>
            Backup.BackupDataAsync(Options(scratch, scratch.Backup), Timestamp));

        File.Delete(Path.Combine(scratch.Library, "link.db"));
        await Backup.BackupDataAsync(Options(scratch, scratch.Backup, AllItems), Timestamp);
        var actual = Path.Combine(scratch.Root, "actual-library");
        Directory.CreateDirectory(actual);
        Directory.CreateSymbolicLink(scratch.RestoreLibrary, actual);
        await Assert.ThrowsAsync<IOException>(() =>
            Backup.RestoreBackupAsync(RestoreOptions(scratch, AllItems)));
    }

    [Fact]
    public async Task Duplicate_case_insensitive_member_names_are_rejected_on_linux()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        ArchiveTestData.Write(scratch.Library, "Games.db", "other");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Backup.BackupDataAsync(Options(scratch, scratch.Backup), Timestamp));
        Assert.False(File.Exists(scratch.Backup));
    }

    [Fact]
    public async Task Linux_file_names_that_would_change_meaning_in_a_portable_archive_are_rejected()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        ArchiveTestData.Write(scratch.Library, "a\\b.db", "ambiguous");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Backup.BackupDataAsync(Options(scratch, scratch.Backup), Timestamp));
        Assert.False(File.Exists(scratch.Backup));
    }

    [Fact]
    public async Task Cancelled_backup_and_restore_leave_no_partial_output()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Backup.BackupDataAsync(Options(scratch, scratch.Backup), Timestamp,
                cancellationToken: cancelled.Token));
        Assert.False(File.Exists(scratch.Backup));

        await Backup.BackupDataAsync(Options(scratch, scratch.Backup, AllItems), Timestamp);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Backup.RestoreBackupAsync(RestoreOptions(scratch, AllItems), cancellationToken: cancelled.Token));
        Assert.False(Directory.Exists(scratch.RestoreData));
    }

    [Fact]
    public async Task Backup_output_inside_the_library_is_not_packed_as_a_source_file()
    {
        using var scratch = new Scratch();
        CreateSource(scratch);
        var output = Path.Combine(scratch.Library, "backup.zip");
        File.WriteAllText(output, "previous output");
        await Backup.BackupDataAsync(Options(scratch, output, overwrite: true), Timestamp);
        Assert.DoesNotContain("library/backup.zip", Archive.GetArchiveFiles(output));
    }

    private static BackupOptions Options(Scratch scratch, string output,
        params BackupDataItem[] items) => Options(scratch, output, false, items);

    private static BackupOptions Options(Scratch scratch, string output, bool overwrite,
        params BackupDataItem[] items) => new()
    {
        DataDir = scratch.Data,
        LibraryDir = scratch.Library,
        OutputFile = output,
        Overwrite = overwrite,
        BackupItems = items
    };

    private static BackupRestoreOptions RestoreOptions(Scratch scratch, IReadOnlyCollection<BackupDataItem> items,
        string? backupFile = null, bool overwrite = false, string? workDirectory = null) => new()
    {
        BackupFile = backupFile ?? scratch.Backup,
        DataDir = scratch.RestoreData,
        LibraryDir = scratch.RestoreLibrary,
        WorkDirectory = workDirectory ?? scratch.Work,
        RestoreItems = items,
        Overwrite = overwrite
    };

    private static void CreateSource(Scratch scratch)
    {
        ArchiveTestData.Write(scratch.Data, "config.json", "{\"DatabasePath\":\"library\"}\n");
        ArchiveTestData.Write(scratch.Data, "fullscreenConfig.json", "{\"Monitor\":0}\n");
        ArchiveTestData.Write(scratch.Data, "Extensions/TestAddon/extension.yaml", "Id: TestAddon\n");
        ArchiveTestData.Write(scratch.Data, "Extensions/TestAddon/TestAddon.dll", "addon");
        ArchiveTestData.Write(scratch.Data, "ExtensionsData/TestAddon/data.json", "{\"enabled\":true}\n");
        ArchiveTestData.Write(scratch.Data, "Themes/Desktop/Default/theme.yaml", "built-in");
        ArchiveTestData.Write(scratch.Data, "Themes/Desktop/readme.txt", "not a theme");
        ArchiveTestData.Write(scratch.Data, "Themes/Desktop/Custom/theme.yaml", "custom");
        ArchiveTestData.Write(scratch.Data, "Themes/Desktop/Custom/Views/Main.xaml", "<Grid />");
        ArchiveTestData.Write(scratch.Data, "Themes/Fullscreen/Custom/theme.yaml", "custom");
        ArchiveTestData.Write(scratch.Data, "Themes/Other/theme.yaml", "not a mode");
        ArchiveTestData.Write(scratch.Library, "games.db", "games");
        ArchiveTestData.Write(scratch.Library, "database.json", "database");
        ArchiveTestData.Write(scratch.Library, "files/cover.png", "cover");
        ArchiveTestData.Write(scratch.Library, "files/Backgrounds/background.jpg", "background");
    }

    private static Dictionary<string, byte[]> ExpectedData(Scratch scratch) =>
        ArchiveTestData.ReadTree(scratch.Data)
            .Where(kv => !kv.Key.StartsWith("Themes/Desktop/Default/", StringComparison.Ordinal) &&
                         kv.Key != "Themes/Desktop/readme.txt" &&
                         !kv.Key.StartsWith("Themes/Other/", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private static (Dictionary<string, byte[]> Data, Dictionary<string, byte[]> Library) FixtureTrees(
        string fixture)
    {
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config.json"] = ArchiveTestData.EntryBytes(fixture, "config.json"),
            ["fullscreenConfig.json"] = ArchiveTestData.EntryBytes(fixture, "fullscreenConfig.json"),
            ["Extensions/TestAddon/extension.yaml"] = ArchiveTestData.EntryBytes(fixture, "extension/TestAddon/extension.yaml"),
            ["Extensions/TestAddon/TestAddon.dll"] = ArchiveTestData.EntryBytes(fixture, "extension/TestAddon/TestAddon.dll"),
            ["ExtensionsData/TestAddon/data.json"] = ArchiveTestData.EntryBytes(fixture, "extensiondata/TestAddon/data.json"),
            ["Themes/Desktop/Custom/theme.yaml"] = ArchiveTestData.EntryBytes(fixture, "themes/Desktop/Custom/theme.yaml"),
            ["Themes/Desktop/Custom/Views/Main.xaml"] = ArchiveTestData.EntryBytes(fixture, "themes/Desktop/Custom/Views/Main.xaml"),
            ["Themes/Fullscreen/Custom/theme.yaml"] = ArchiveTestData.EntryBytes(fixture, "themes/Fullscreen/Custom/theme.yaml")
        };
        var library = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["database.json"] = ArchiveTestData.EntryBytes(fixture, "library/database.json"),
            ["games.db"] = ArchiveTestData.EntryBytes(fixture, "library/games.db"),
            ["files/cover.png"] = ArchiveTestData.EntryBytes(fixture, "libraryfiles/cover.png"),
            ["files/Backgrounds/background.jpg"] = ArchiveTestData.EntryBytes(fixture, "libraryfiles/Backgrounds/background.jpg")
        };
        return (data, library);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("playnite-backup-").FullName;
        internal string Data => Path.Combine(Root, "data");
        internal string Library => Path.Combine(Root, "library");
        internal string Work => Path.Combine(Root, "work");
        internal string Backup => Path.Combine(Root, "backup.zip");
        internal string RestoreData => Path.Combine(Root, "restored-data");
        internal string RestoreLibrary => Path.Combine(Root, "restored-library");

        internal Scratch()
        {
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Library);
            Directory.CreateDirectory(Work);
        }

        public void Dispose() => ArchiveTestData.Delete(Root);
    }
}
