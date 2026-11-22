using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
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
    public required string OutputFile { get; init; }
    public IReadOnlyCollection<BackupDataItem> BackupItems { get; init; } = [];
    public bool Overwrite { get; init; }
    public int RotatingBackups { get; init; }
}

public sealed class BackupRestoreOptions
{
    public required string BackupFile { get; init; }
    public required string DataDir { get; init; }
    public required string LibraryDir { get; init; }
    public required string WorkDirectory { get; init; }
    public IReadOnlyCollection<BackupDataItem> RestoreItems { get; init; } = [];
    /// <summary>Explicit opt-in to v10's destructive selected-category replacement.</summary>
    public bool Overwrite { get; init; }
}

public sealed record BackupRestoreReport(bool LegacyFormat, IReadOnlyList<string> Warnings);

/// <summary>v10 backup categories and ZIP layout, plus a versioned integrity manifest.</summary>
public static class Backup
{
    public const string ManifestName = "backup-manifest.json";
    public const int FormatVersion = 1;
    private static readonly string[] ConfigFiles = ["config.json", "fullscreenConfig.json"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Member(string Name, long Size, string Sha256);
    private sealed record Manifest(int FormatVersion, List<Member> Members);

    public static string GetAutoBackupFileName(DateTimeOffset timestamp) =>
        $"PlayniteBackup-{timestamp.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)}.zip";

    public static async Task BackupDataAsync(BackupOptions options, DateTimeOffset timestamp,
        ArchiveLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new ArchiveLimits();
        limits.Validate();
        if (options.RotatingBackups < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var output = Path.GetFullPath(options.OutputFile);
        CheckPath(output);
        if (File.Exists(output) && !options.Overwrite)
        {
            throw new IOException("Backup output already exists.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var files = GatherFiles(options, cancellationToken).Take(limits.EntryCount).ToList();
        long total = 0;
        if (files.Count >= limits.EntryCount)
        {
            throw new InvalidDataException("Archive exceeds entry limit.");
        }

        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (Archive.NormalizeName(file.Name) != file.Name || !names.Add(file.Name))
            {
                throw new InvalidDataException("Unsafe or duplicate source member.");
            }

            CheckPath(file.Path);
            if (Path.GetFullPath(file.Path) == output)
            {
                throw new IOException("Backup output is also a source file.");
            }

            var size = new FileInfo(file.Path).Length;
            if (size > limits.EntryBytes || size > limits.TotalBytes - total)
            {
                throw new InvalidDataException("Archive exceeds decompression limits.");
            }

            total += size;
        }

        var temporary = output + "." + Path.GetRandomFileName();
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81_920, FileOptions.Asynchronous))
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, true);
                var members = new List<Member>();
                long actualTotal = 0;
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(file.Name);
                    entry.LastWriteTime = timestamp;
                    await using var source = File.OpenRead(file.Path);
                    await using var target = entry.Open();
                    var (size, hash) = await CopyAndHashAsync(source, target,
                        Math.Min(limits.EntryBytes, limits.TotalBytes - actualTotal), cancellationToken).ConfigureAwait(false);
                    actualTotal += size;
                    members.Add(new Member(file.Name, size, hash));
                }

                var manifest = JsonSerializer.SerializeToUtf8Bytes(new Manifest(FormatVersion, members), JsonOptions);
                if (manifest.LongLength > Math.Min(limits.EntryBytes, 1_048_576) ||
                    manifest.LongLength > limits.TotalBytes - actualTotal)
                {
                    throw new InvalidDataException("Backup manifest exceeds archive limits.");
                }

                var manifestEntry = zip.CreateEntry(ManifestName);
                manifestEntry.LastWriteTime = timestamp;
                await using var manifestStream = manifestEntry.Open();
                await manifestStream.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, options.Overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }

        // v10 keeps the current backup plus RotatingBackups older automatically named files.
        var backups = Directory.GetFiles(Path.GetDirectoryName(output)!, "PlayniteBackup-*.zip")
            .Where(p => Regex.IsMatch(Path.GetFileName(p), @"^PlayniteBackup-\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}\.zip$", RegexOptions.CultureInvariant))
            .Order(StringComparer.Ordinal).ToList();
        foreach (var file in backups.Take((int)Math.Max(0L, backups.Count - (long)options.RotatingBackups - 1)))
        {
            File.Delete(file);
        }
    }

    public static async Task<BackupRestoreReport> RestoreBackupAsync(BackupRestoreOptions options,
        ArchiveLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new ArchiveLimits();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        await using var source = File.OpenRead(options.BackupFile);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        var entries = ValidateZip(zip, limits);
        var manifestEntry = entries.SingleOrDefault(e => e.Name == ManifestName).Entry;
        Manifest? manifest = null;
        if (manifestEntry is not null)
        {
            using var buffer = new MemoryStream();
            await using var input = manifestEntry.Open();
            await Archive.CopyBoundedAsync(input, buffer, Math.Min(limits.EntryBytes, 1_048_576), cancellationToken).ConfigureAwait(false);
            try
            {
                manifest = JsonSerializer.Deserialize<Manifest>(buffer.ToArray(), JsonOptions)
                    ?? throw new InvalidDataException("Missing backup manifest.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Invalid backup manifest.", ex);
            }

            ValidateManifest(manifest, entries);
        }

        var warnings = new List<string>();
        if (manifest is null)
        {
            warnings.Add("Legacy v10 backup: no version or integrity manifest.");
            foreach (var config in ConfigFiles.Where(c => !entries.Any(e => e.Name == c)))
            {
                warnings.Add($"Missing optional v10 member: {config}");
            }

            if (!entries.Any(e => e.Name.StartsWith("library/", StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add("Missing optional v10 library members.");
            }
        }

        var selected = entries.Where(e => e.Name != ManifestName && Category(e.Name) is { } c &&
            options.RestoreItems.Contains(c)).ToList();
        foreach (var entry in entries.Where(e => e.Name != ManifestName && Category(e.Name) is null))
        {
            warnings.Add($"Unrecognized member ignored: {entry.Name}");
        }

        var roots = selected.Select(e => Category(e.Name)!.Value).Distinct().ToList();
        foreach (var category in roots)
        {
            var root = TargetRoot(options, category);
            CheckPath(root);
            if (category != BackupDataItem.Settings)
            {
                if (File.Exists(root))
                {
                    throw new IOException("Restore directory target is a file.");
                }

                var fullRoot = Path.GetFullPath(root);
                var work = Path.GetFullPath(options.WorkDirectory);
                if (work == fullRoot || work.StartsWith(fullRoot.TrimEnd('/') + "/", StringComparison.Ordinal))
                {
                    throw new IOException("Restore work directory must be outside selected targets.");
                }

                if (options.Overwrite && Directory.Exists(root))
                {
                    CheckTree(root);
                }
            }

            if (!options.Overwrite && category != BackupDataItem.Settings && (Directory.Exists(root) || File.Exists(root)))
            {
                throw new IOException("Restore target already exists; explicit overwrite required.");
            }
        }

        var targetNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in selected)
        {
            var target = TargetPath(options, entry.Name);
            CheckPath(target);
            if (!targetNames.Add(Path.GetFullPath(target)) || Directory.Exists(target))
            {
                throw new IOException("Conflicting restore target.");
            }

            for (var parent = Path.GetDirectoryName(Path.GetFullPath(target)); parent is not null;
                parent = Path.GetDirectoryName(parent))
            {
                if (File.Exists(parent))
                {
                    throw new IOException("Restore parent directory is a file.");
                }
            }

            if (!options.Overwrite && (File.Exists(target) || Directory.Exists(target)))
            {
                throw new IOException("Restore target already exists; explicit overwrite required.");
            }
        }

        foreach (var target in targetNames)
        {
            for (var parent = Path.GetDirectoryName(target); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                if (targetNames.Contains(parent))
                {
                    throw new IOException("Restore targets contain a file/directory collision.");
                }
            }
        }

        CheckPath(options.WorkDirectory);
        if (!Directory.Exists(options.WorkDirectory))
        {
            throw new DirectoryNotFoundException("Restore work directory must exist.");
        }

        var stage = Path.Combine(options.WorkDirectory, "restore-" + Path.GetRandomFileName());
        var expectedMembers = manifest?.Members.ToDictionary(m => m.Name, StringComparer.Ordinal);
        Directory.CreateDirectory(stage);
        try
        {
            long actualTotal = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = Path.Combine(stage, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                await using var input = entry.Entry.Open();
                await using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81_920, FileOptions.Asynchronous);
                var (size, hash) = await CopyAndHashAsync(input, output,
                    Math.Min(limits.EntryBytes, limits.TotalBytes - actualTotal), cancellationToken,
                    entry.Entry.Crc32).ConfigureAwait(false);
                actualTotal += size;
                if (size != entry.Entry.Length || (manifest is not null && entry.Name != ManifestName &&
                    expectedMembers![entry.Name].Sha256 != hash))
                {
                    throw new InvalidDataException("Backup member integrity mismatch.");
                }
            }

            // No live target is changed until every member (including unselected ones) is verified.
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Overwrite)
            {
                foreach (var category in roots)
                {
                    CleanTarget(options, category);
                }
            }

            foreach (var entry in selected)
            {
                var target = TargetPath(options, entry.Name);
                CheckPath(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(stage, entry.Name), target, options.Overwrite);
            }
        }
        finally
        {
            Directory.Delete(stage, true);
        }

        return new BackupRestoreReport(manifest is null, warnings);
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

    private static void ValidateManifest(Manifest manifest, List<(string Name, ZipArchiveEntry Entry)> entries)
    {
        if (manifest.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException("Unsupported backup format version.");
        }

        if (manifest.Members is null || manifest.Members.Count != entries.Count - 1)
        {
            throw new InvalidDataException("Missing or unexpected backup member.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var sizes = entries.ToDictionary(e => e.Name, e => e.Entry.Length, StringComparer.Ordinal);
        foreach (var member in manifest.Members)
        {
            if (member is null || string.IsNullOrEmpty(member.Name) || !names.Add(member.Name) ||
                member.Name == ManifestName || Archive.NormalizeName(member.Name) != member.Name ||
                member.Sha256 is null || !Regex.IsMatch(member.Sha256, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant) ||
                !sizes.TryGetValue(member.Name, out var size) || size != member.Size)
            {
                throw new InvalidDataException("Missing or invalid required backup member.");
            }
        }
    }

    public static List<BackupDataItem> GetRestoreSelections(string backupFile, ArchiveLimits? limits = null)
    {
        using var stream = File.OpenRead(backupFile);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = ValidateZip(zip, limits ?? new ArchiveLimits());
        var result = new List<BackupDataItem> { BackupDataItem.Settings, BackupDataItem.Library };
        foreach (var category in new[] { BackupDataItem.LibraryFiles, BackupDataItem.Extensions,
            BackupDataItem.ExtensionsData, BackupDataItem.Themes })
        {
            if (entries.Any(e => Category(e.Name) == category))
            {
                result.Add(category);
            }
        }

        return result;
    }

    private static IEnumerable<(string Name, string Path)> GatherFiles(BackupOptions options,
        CancellationToken cancellationToken)
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
                yield return ("library/" + Path.GetFileName(file), file);
            }
        }

        foreach (var (category, source, prefix) in new[]
        {
            (BackupDataItem.LibraryFiles, Path.Combine(options.LibraryDir, "files"), "libraryfiles"),
            (BackupDataItem.Extensions, Path.Combine(options.DataDir, "Extensions"), "extension"),
            (BackupDataItem.ExtensionsData, Path.Combine(options.DataDir, "ExtensionsData"), "extensiondata"),
            (BackupDataItem.Themes, Path.Combine(options.DataDir, "Themes"), "themes")
        })
        {
            if (options.BackupItems.Contains(category))
            {
                foreach (var file in Walk(source, prefix, category == BackupDataItem.Themes, cancellationToken))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<(string Name, string Path)> Walk(string source, string prefix, bool themes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckPath(source);
        if (!Directory.Exists(source))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            if (themes && ((prefix == "themes" && name is not ("Desktop" or "Fullscreen")) ||
                (prefix.Count(c => c == '/') == 1 && name == "Default")))
            {
                continue;
            }

            foreach (var file in Walk(dir, prefix + "/" + name, themes, cancellationToken))
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
            "library" when parts.Length == 2 => BackupDataItem.Library,
            "libraryfiles" => BackupDataItem.LibraryFiles,
            "extension" => BackupDataItem.Extensions,
            "extensiondata" => BackupDataItem.ExtensionsData,
            "themes" when parts.Length >= 4 && parts[1] is "Desktop" or "Fullscreen" && parts[2] != "Default" => BackupDataItem.Themes,
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
            foreach (var file in Directory.GetFiles(root))
            {
                File.Delete(file);
            }
        }
        else if (category == BackupDataItem.Themes)
        {
            foreach (var mode in new[] { "Desktop", "Fullscreen" })
            {
                var modeDir = Path.Combine(root, mode);
                CheckPath(modeDir);
                if (!Directory.Exists(modeDir))
                {
                    continue;
                }

                foreach (var dir in Directory.GetDirectories(modeDir).Where(d =>
                    !Path.GetFileName(d).Equals("Default", StringComparison.OrdinalIgnoreCase)))
                {
                    CheckTree(dir);
                    Directory.Delete(dir, true);
                }

                foreach (var file in Directory.GetFiles(modeDir))
                {
                    File.Delete(file);
                }
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

    internal static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current) || new FileInfo(current).LinkTarget is not null) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
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
                throw new InvalidDataException("Archive exceeds decompression limits.");
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
