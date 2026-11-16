using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Playnite;

public enum BackupDataItem
{
    Settings = 0,
    Library = 1,
    LibraryFiles = 2,
    Extensions = 3,
    Themes = 4,
    ExtensionsData = 5
}

public sealed class BackupOptions
{
    public required string DataDir { get; init; }
    public required string LibraryDir { get; init; }
    /// <summary>Exact output path. Ignored when <see cref="OutputDir"/> is set, as in v10.</summary>
    public string? OutputFile { get; init; }
    /// <summary>Directory for an automatically named PlayniteBackup-*.zip.</summary>
    public string? OutputDir { get; init; }
    public IReadOnlyCollection<BackupDataItem> BackupItems { get; init; } = [];
    public int RotatingBackups { get; init; }
}

public sealed class BackupRestoreOptions
{
    public required string BackupFile { get; init; }
    public required string DataDir { get; init; }
    public required string LibraryDir { get; init; }
    /// <summary>Existing directory used to stage and verify every selected member before a live write.</summary>
    public required string WorkDirectory { get; init; }
    public IReadOnlyCollection<BackupDataItem> RestoreItems { get; init; } = [];
}

/// <summary>v10 backup categories, ZIP layout, auto-backup naming and restore semantics.</summary>
public static class Backup
{
    private const string LibraryEntryRoot = "library";
    private const string LibraryFilesEntryRoot = "libraryfiles";
    private const string ExtensionsEntryRoot = "extension";
    private const string ExtensionsDataEntryRoot = "extensiondata";
    private const string ThemesEntryRoot = "themes";
    private const string DefaultThemeDirName = "Default";
    private static readonly string[] ThemeModeDirs = ["Desktop", "Fullscreen"];
    private static readonly string[] ConfigFiles = ["config.json", "fullscreenConfig.json"];

    public static string GetAutoBackupFileName(DateTimeOffset timestamp) =>
        $"PlayniteBackup-{timestamp.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)}.zip";

    public static async Task BackupDataAsync(BackupOptions options, DateTimeOffset timestamp,
        ArchiveLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new ArchiveLimits();
        limits.Validate();
        if (options.RotatingBackups < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Rotating backup count cannot be negative.");
        }

        var output = Path.GetFullPath(ResolveOutput(options, timestamp));
        CheckPath(output);

        // v10 deletes its own output before packing, so an output inside a source root is not a member.
        var files = GatherFiles(options)
            .Where(f => Path.GetFullPath(f.Path) != output)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        if (files.Count > limits.EntryCount)
        {
            throw new InvalidDataException("Archive exceeds entry limit.");
        }

        foreach (var file in files)
        {
            CheckPath(file.Path);
            if (Archive.NormalizeName(file.Name) != file.Name)
            {
                throw new InvalidDataException("Backup member name is not portable.");
            }

            if (!names.Add(file.Name))
            {
                throw new InvalidDataException("Duplicate backup member name.");
            }

            var size = new FileInfo(file.Path).Length;
            if (size > limits.EntryBytes || size > limits.TotalBytes - declaredTotal)
            {
                throw new InvalidDataException("Archive exceeds decompression limits.");
            }

            declaredTotal += size;
        }

        var temporary = output + "." + Path.GetRandomFileName();
        long extractedDeclared = 0;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81_920, FileOptions.Asynchronous))
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, true);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(file.Name);
                    entry.LastWriteTime = timestamp;
                    var declared = new FileInfo(file.Path).Length;
                    var cap = Math.Min(declared, limits.TotalBytes - extractedDeclared);
                    await using var source = File.OpenRead(file.Path);
                    await using var target = entry.Open();
                    var (size, _) = await CopyAndHashAsync(source, target, cap,
                        cancellationToken).ConfigureAwait(false);
                    if (size != declared)
                    {
                        throw new InvalidDataException("Backup source member size mismatch.");
                    }

                    extractedDeclared += declared;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, true);
        }
        finally
        {
            File.Delete(temporary);
        }

        // v10 keeps the current backup plus RotatingBackups older automatically named files.
        var backups = Directory.GetFiles(Path.GetDirectoryName(output)!, "PlayniteBackup-*.zip")
            .Where(p => Regex.IsMatch(Path.GetFileName(p),
                @"^PlayniteBackup-\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}\.zip$", RegexOptions.CultureInvariant))
            .Order(StringComparer.Ordinal).ToList();
        foreach (var file in backups.Take(Math.Max(0, backups.Count - options.RotatingBackups - 1)))
        {
            File.Delete(file);
        }
    }

    public static async Task RestoreBackupAsync(BackupRestoreOptions options,
        ArchiveLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new ArchiveLimits();
        limits.Validate();
        CheckPath(options.BackupFile);
        await using var source = File.OpenRead(options.BackupFile);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        var entries = ValidateZip(zip, limits);

        var selected = entries.Where(e => Category(e.Name) is { } c &&
            options.RestoreItems.Contains(c)).ToList();
        var roots = selected.Select(e => Category(e.Name)!.Value).Distinct().ToList();
        foreach (var category in roots)
        {
            CheckPath(TargetRoot(options, category));
        }

        CheckPath(options.WorkDirectory);
        if (!Directory.Exists(options.WorkDirectory))
        {
            throw new DirectoryNotFoundException("Restore work directory must exist.");
        }

        if (roots.Any(c => IsWithin(options.WorkDirectory, TargetRoot(options, c))))
        {
            throw new IOException("Restore work directory must be outside the selected restore targets.");
        }

        var stage = Path.Combine(options.WorkDirectory, "restore-" + Path.GetRandomFileName());
        Directory.CreateDirectory(stage);
        CheckPath(stage);
        long extractedDeclared = 0;
        try
        {
            foreach (var entry in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var declared = entry.Entry.Length;
                var cap = Math.Min(declared, limits.TotalBytes - extractedDeclared);
                var stagedPath = Path.Combine(stage, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                await using var input = entry.Entry.Open();
                await using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81_920, FileOptions.Asynchronous);
                var (size, _) = await CopyAndHashAsync(input, output, cap, cancellationToken,
                    entry.Entry.Crc32).ConfigureAwait(false);
                if (size != declared)
                {
                    throw new InvalidDataException("Backup member size mismatch.");
                }

                extractedDeclared += declared;
            }

            // No live target is changed until every selected member is verified.
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var category in roots)
            {
                CleanTarget(options, category);
            }

            foreach (var entry in selected)
            {
                var target = TargetPath(options, entry.Name);
                CheckPath(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(stage, entry.Name), target, true);
            }
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                CheckPath(stage);
                Directory.Delete(stage, true);
            }
        }
    }

    /// <summary>
    /// v10's restore-category preselector. The prefix tests are intentionally separator-free,
    /// so "extension" also matches "extensiondata" exactly as v10 does.
    /// </summary>
    public static List<BackupDataItem> GetRestoreSelections(string backupFile, ArchiveLimits? limits = null)
    {
        CheckPath(backupFile);
        using var source = File.OpenRead(backupFile);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        var entries = ValidateZip(zip, limits ?? new ArchiveLimits());
        var selections = new List<BackupDataItem> { BackupDataItem.Settings, BackupDataItem.Library };
        if (entries.Any(e => e.Name.StartsWith(LibraryFilesEntryRoot, StringComparison.OrdinalIgnoreCase)))
        {
            selections.Add(BackupDataItem.LibraryFiles);
        }

        if (entries.Any(e => e.Name.StartsWith(ExtensionsEntryRoot, StringComparison.OrdinalIgnoreCase)))
        {
            selections.Add(BackupDataItem.Extensions);
        }

        if (entries.Any(e => e.Name.StartsWith(ExtensionsDataEntryRoot, StringComparison.OrdinalIgnoreCase)))
        {
            selections.Add(BackupDataItem.ExtensionsData);
        }

        if (entries.Any(e => e.Name.StartsWith(ThemesEntryRoot, StringComparison.OrdinalIgnoreCase)))
        {
            selections.Add(BackupDataItem.Themes);
        }

        return selections;
    }

    private static string ResolveOutput(BackupOptions options, DateTimeOffset timestamp)
    {
        if (!string.IsNullOrEmpty(options.OutputDir))
        {
            return Path.Combine(options.OutputDir, GetAutoBackupFileName(timestamp));
        }

        if (!string.IsNullOrEmpty(options.OutputFile))
        {
            return options.OutputFile;
        }

        throw new ArgumentException("Backup output path not specified.", nameof(options));
    }

    private static List<(string Name, ZipArchiveEntry Entry)> ValidateZip(ZipArchive zip, ArchiveLimits limits)
    {
        limits.Validate();
        if (zip.Entries.Count > limits.EntryCount)
        {
            throw new InvalidDataException("Archive exceeds entry limit.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, ZipArchiveEntry)>();
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = Archive.NormalizeName(entry.FullName);
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!names.Add(name) || kind is not (0 or 0x8000 or 0x4000) ||
                entry.Length < 0 || entry.Length > limits.EntryBytes || entry.Length > limits.TotalBytes - total)
            {
                throw new InvalidDataException("Unsafe, duplicate, or oversized archive member.");
            }

            total += entry.Length;
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                if (entry.Length != 0)
                {
                    throw new InvalidDataException("Nonempty directory member.");
                }

                continue;
            }

            result.Add((name, entry));
        }

        var fileNames = result.Select(e => e.Item1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in fileNames)
        {
            for (var parent = Path.GetDirectoryName(name); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (fileNames.Contains(parent))
                {
                    throw new InvalidDataException("Archive file/directory collision.");
                }
            }
        }

        return result;
    }

    private static IEnumerable<(string Name, string Path)> GatherFiles(BackupOptions options)
    {
        foreach (var config in ConfigFiles)
        {
            var path = Path.Combine(options.DataDir, config);
            if (File.Exists(path))
            {
                yield return (config, path);
            }
        }

        CheckPath(options.LibraryDir);
        if (Directory.Exists(options.LibraryDir))
        {
            foreach (var file in Directory.EnumerateFiles(options.LibraryDir))
            {
                yield return (LibraryEntryRoot + "/" + Path.GetFileName(file), file);
            }
        }

        foreach (var (category, source, prefix) in new[]
        {
            (BackupDataItem.LibraryFiles, Path.Combine(options.LibraryDir, "files"), LibraryFilesEntryRoot),
            (BackupDataItem.Extensions, Path.Combine(options.DataDir, "Extensions"), ExtensionsEntryRoot),
            (BackupDataItem.ExtensionsData, Path.Combine(options.DataDir, "ExtensionsData"), ExtensionsDataEntryRoot),
            (BackupDataItem.Themes, Path.Combine(options.DataDir, "Themes"), ThemesEntryRoot)
        })
        {
            if (options.BackupItems.Contains(category))
            {
                foreach (var file in Walk(source, prefix, category == BackupDataItem.Themes))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<(string Name, string Path)> Walk(string source, string prefix, bool themes)
    {
        CheckPath(source);
        if (!Directory.Exists(source))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            // v10 only packs theme directories within Desktop and Fullscreen.
            if (!themes || prefix.Count(c => c == '/') >= 2)
            {
                var name = prefix + "/" + Path.GetFileName(file);
                Archive.NormalizeName(name);
                yield return (name, file);
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (themes && ((prefix == ThemesEntryRoot && name is not ("Desktop" or "Fullscreen")) ||
                (prefix.Count(c => c == '/') == 1 && name == DefaultThemeDirName)))
            {
                continue;
            }

            foreach (var file in Walk(dir, prefix + "/" + name, themes))
            {
                yield return file;
            }
        }
    }

    private static BackupDataItem? Category(string name)
    {
        if (ConfigFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return BackupDataItem.Settings;
        }

        var parts = name.Split('/');
        if (parts.Length < 2)
        {
            return null;
        }

        return parts[0].ToLowerInvariant() switch
        {
            LibraryEntryRoot when parts.Length == 2 => BackupDataItem.Library,
            LibraryFilesEntryRoot => BackupDataItem.LibraryFiles,
            ExtensionsEntryRoot => BackupDataItem.Extensions,
            ExtensionsDataEntryRoot => BackupDataItem.ExtensionsData,
            ThemesEntryRoot when parts.Length >= 4 && parts[1] is "Desktop" or "Fullscreen" &&
                parts[2] != DefaultThemeDirName => BackupDataItem.Themes,
            _ => null
        };
    }

    private static string TargetRoot(BackupRestoreOptions options, BackupDataItem category) => category switch
    {
        BackupDataItem.Settings => options.DataDir,
        BackupDataItem.Library => options.LibraryDir,
        BackupDataItem.LibraryFiles => Path.Combine(options.LibraryDir, "files"),
        BackupDataItem.Extensions => Path.Combine(options.DataDir, "Extensions"),
        BackupDataItem.ExtensionsData => Path.Combine(options.DataDir, "ExtensionsData"),
        BackupDataItem.Themes => Path.Combine(options.DataDir, "Themes"),
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private static string TargetPath(BackupRestoreOptions options, string name)
    {
        var category = Category(name)!.Value;
        return Path.Combine(TargetRoot(options, category), category == BackupDataItem.Settings
            ? ConfigFiles.Single(c => c.Equals(name, StringComparison.OrdinalIgnoreCase))
            : name[(name.IndexOf('/') + 1)..]);
    }

    private static void CleanTarget(BackupRestoreOptions options, BackupDataItem category)
    {
        var root = TargetRoot(options, category);
        CheckPath(root);
        if (!Directory.Exists(root) || category == BackupDataItem.Settings)
        {
            return;
        }

        if (category == BackupDataItem.Library)
        {
            var files = Directory.GetFiles(root);
            foreach (var file in files)
            {
                CheckPath(file);
            }

            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
        else if (category == BackupDataItem.Themes)
        {
            // Preflight every mode directory and descendant before deleting anything, so a
            // symlink in the second mode cannot surface after the first mode is already gone.
            var modeDirs = ThemeModeDirs
                .Select(mode => Path.Combine(root, mode))
                .Where(Directory.Exists)
                .ToList();
            foreach (var modeDir in modeDirs)
            {
                CheckPath(modeDir);
            }

            var themeDirs = modeDirs
                .SelectMany(Directory.GetDirectories)
                .Where(d => !Path.GetFileName(d).Equals(DefaultThemeDirName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var themeFiles = modeDirs.SelectMany(Directory.GetFiles).ToList();
            foreach (var dir in themeDirs)
            {
                CheckTree(dir);
            }

            foreach (var file in themeFiles)
            {
                CheckPath(file);
            }

            foreach (var dir in themeDirs)
            {
                Directory.Delete(dir, true);
            }

            foreach (var file in themeFiles)
            {
                File.Delete(file);
            }
        }
        else
        {
            CheckTree(root);
            Directory.Delete(root, true);
        }
    }

    private static void CheckTree(string root)
    {
        CheckPath(root);
        foreach (var child in Directory.EnumerateFileSystemEntries(root))
        {
            CheckPath(child);
            if (Directory.Exists(child))
            {
                CheckTree(child);
            }
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = target.EndsWith(Path.DirectorySeparatorChar) ? target : target + Path.DirectorySeparatorChar;
        return full.Equals(target, StringComparison.Ordinal) ||
            full.StartsWith(prefix, StringComparison.Ordinal);
    }

    internal static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null ||
                ((File.Exists(current) || Directory.Exists(current)) &&
                 (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
            {
                throw new IOException("Symbolic links are not allowed in backup or restore paths.");
            }
        }
    }

    private static async Task<(long Size, string Hash)> CopyAndHashAsync(Stream input, Stream output,
        long maximum, CancellationToken cancellationToken, uint? expectedCrc = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long size = 0;
        var crc = uint.MaxValue;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (read > maximum - size)
            {
                throw new InvalidDataException($"Archive exceeds decompression limits: maximum {maximum} bytes.");
            }

            hash.AppendData(buffer, 0, read);
            if (expectedCrc.HasValue)
            {
                for (var i = 0; i < read; i++)
                {
                    crc ^= buffer[i];
                    for (var bit = 0; bit < 8; bit++)
                    {
                        crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xEDB88320U);
                    }
                }
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            size += read;
        }

        if (expectedCrc.HasValue && ~crc != expectedCrc.Value)
        {
            throw new InvalidDataException("Backup member CRC mismatch.");
        }

        return (size, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
