using System.IO.Compression;
using System.Text;
using Playnite.Core.Plugins;
using Playnite.Core.Themes;

namespace Playnite.Core.Addons;

/// <summary>The active and rollback versions of a package installed through the manager.</summary>
public sealed record ManagedAddonPackage(string Id, Version Version, Version? PreviousVersion)
{
    public override string ToString() => PreviousVersion is null
        ? $"{Id} · v{Version}"
        : $"{Id} · v{Version} (rollback: v{PreviousVersion})";
}

public sealed partial class AddonInstaller
{
    internal const string PackageStoreName = ".playnite-packages";
    private const string StateFileName = "versions";
    private const string RemovedState = "removed";
    private const int MaxPackageEntries = 8192;
    private const long MaxPackageBytes = 1024L * 1024 * 1024;

    private string PackageRoot => Path.Combine(Layout.RootPath, PackageStoreName);

    /// <summary>Lists managed packages, including changes that will take effect on restart.</summary>
    public IReadOnlyList<ManagedAddonPackage> GetPackages(Action<string, string>? reportError = null) =>
        GetPackageStates(reportError).Select(state => state.Package).ToArray();

    private IEnumerable<PackageState> GetPackageStates(Action<string, string>? reportError)
    {
        if (!IsPlainDirectory(Layout.RootPath) || !IsPlainDirectory(PackageRoot))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(PackageRoot).Order(StringComparer.Ordinal))
        {
            PackageState? state;
            try
            {
                state = IsPlainDirectory(directory) ? ReadPackageState(directory) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                reportError?.Invoke(Path.GetFileName(directory), exception.Message);
                continue;
            }

            if (state is not null)
            {
                yield return state;
            }
        }
    }

    /// <summary>Installs a directory or ZIP-based package. Pass a selected id to update it.</summary>
    public async Task<ManagedAddonPackage> InstallPackageAsync(
        string sourcePath, PluginHost host, string? updateId, CancellationToken cancellationToken,
        Action<PluginDescriptor>? validateHostOwnership = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        cancellationToken.ThrowIfCancellationRequested();
        sourcePath = Path.GetFullPath(sourcePath);
        using var packageLock = OpenPackageLock();
        var staging = Path.Combine(PackageRoot, $".staging-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(staging);
        try
        {
            if (Directory.Exists(sourcePath))
            {
                await CopyPackageAsync(sourcePath, staging, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ExtractPackageAsync(sourcePath, staging, cancellationToken).ConfigureAwait(false);
            }

            var descriptor = ValidatePackage(staging, host, updateId);
            validateHostOwnership?.Invoke(descriptor);
            var packageDirectory = GetPackageDirectory(descriptor.Id);
            var current = ReadStateForChange(packageDirectory);
            if (updateId is null && current is not null)
            {
                throw new InvalidDataException($"'{descriptor.Id}' is already installed. Select it and choose Update.");
            }
            if (updateId is not null && (current is null || descriptor.Version <= current.Active.Version))
            {
                throw new InvalidDataException("The update must be a newer version of the selected package.");
            }

            var active = new PackageVersion(descriptor.Version, Path.GetRandomFileName());
            EnsureDirectory(packageDirectory);
            var versionDirectory = Path.Combine(packageDirectory, active.Generation);

            // Discovery only sees the version named by the state file.
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, versionDirectory);
            try
            {
                WritePackageState(packageDirectory, active, current?.Active);
            }
            catch
            {
                Directory.Delete(versionDirectory, recursive: true);
                throw;
            }

            return new ManagedAddonPackage(descriptor.Id, active.Version, current?.Active.Version);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>Switches to the retained version after revalidating it against the host.</summary>
    public async Task<ManagedAddonPackage> RollBackPackageAsync(
        string id, PluginHost host, CancellationToken cancellationToken,
        Action<PluginDescriptor>? validateHostOwnership = null)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        cancellationToken.ThrowIfCancellationRequested();
        using var packageLock = OpenPackageLock();
        var packageDirectory = GetPackageDirectory(id);
        var current = ReadStateForChange(packageDirectory);
        if (current?.Previous is not { } previous)
        {
            throw new InvalidDataException("There is no previous version to restore.");
        }

        var previousDirectory = Path.Combine(packageDirectory, previous.Generation);
        var descriptor = ValidatePackage(previousDirectory, host, id);
        validateHostOwnership?.Invoke(descriptor);
        if (descriptor.Version != previous.Version)
        {
            throw new InvalidDataException("The retained package does not match its recorded version.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        WritePackageState(packageDirectory, previous, previous: null);
        return new ManagedAddonPackage(id, previous.Version, PreviousVersion: null);
    }

    /// <summary>Unpublishes a managed package. Its payload stays until startup cleanup.</summary>
    public async Task UninstallPackageAsync(string id, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        cancellationToken.ThrowIfCancellationRequested();
        using var packageLock = OpenPackageLock();
        var packageDirectory = GetPackageDirectory(id);
        if (ReadStateForChange(packageDirectory) is null)
        {
            throw new InvalidDataException($"'{id}' is not a managed package.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        WritePackageState(packageDirectory, active: null, previous: null);
    }

    /// <summary>Reclaims retired payloads before a desktop session discovers any managed add-ons.</summary>
    public void CleanRetiredPackages(Action<string, string> reportError)
    {
        if (!IsPlainDirectory(Layout.RootPath) || !IsPlainDirectory(PackageRoot))
        {
            return;
        }

        using var packageLock = OpenPackageLock();
        using var exclusiveUsage = TryAcquireExclusiveUsage();
        if (exclusiveUsage is null)
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(PackageRoot))
        {
            var id = Path.GetFileName(directory);
            if (!IsPlainDirectory(directory))
            {
                continue;
            }
            try
            {
                if (id.StartsWith(".staging-", StringComparison.Ordinal))
                {
                    Directory.Delete(directory, recursive: true);
                    continue;
                }

                var state = ReadPackageState(directory);
                if (state is null)
                {
                    if (IsPackageId(id) && ReadStateLines(directory) is null or [RemovedState])
                    {
                        // Keep the marker until cleanup succeeds, so a partial failure remains retired.
                        foreach (var generation in Directory.EnumerateDirectories(directory))
                        {
                            Directory.Delete(generation, recursive: true);
                        }
                        Directory.Delete(directory, recursive: true);
                    }
                    continue;
                }

                foreach (var generation in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(generation);
                    if (IsGeneration(name) && name != state.Active.Generation && name != state.Previous?.Generation)
                    {
                        Directory.Delete(generation, recursive: true);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                reportError(id, exception.Message);
            }
        }
    }

    internal IDisposable? AcquirePackageUsage() =>
        IsPlainDirectory(Layout.RootPath) && IsPlainDirectory(PackageRoot) ? OpenUsageLock(FileShare.ReadWrite) : null;

    private FileStream? TryAcquireExclusiveUsage()
    {
        try
        {
            return OpenUsageLock(FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private FileStream OpenUsageLock(FileShare share) => OpenLock(".usage", share);

    internal IEnumerable<(ManagedAddonPackage Package, string Directory)> GetActivePackageDirectories(
        Action<string, string>? reportError = null) =>
        GetPackageStates(reportError)
            .Select(state => (state.Package, Directory: Path.Combine(PackageRoot, state.Package.Id, state.Active.Generation)))
            .Where(entry => IsPlainDirectory(entry.Directory));

    private PluginDescriptor ValidatePackage(string directory, PluginHost host, string? updateId)
    {
        if (!IsPlainDirectory(directory))
        {
            throw new InvalidDataException("The package directory is missing or is a symbolic link.");
        }

        var descriptor = ReadPackageDescriptor(directory);
        if ((descriptor.Surfaces & host.ActiveSurface) == PluginSurface.None)
        {
            throw new InvalidDataException("The package has no plugin surface compatible with this host.");
        }
        if (string.Equals(descriptor.Id, PluginHost.HostStatePluginId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{descriptor.Id}' is a reserved plugin id.");
        }
        if (updateId is not null && !string.Equals(updateId, descriptor.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The package id '{descriptor.Id}' does not match '{updateId}'.");
        }

        if (descriptor.Surfaces.HasFlag(PluginSurface.Theme))
        {
            var theme = ThemePackParser.LoadFromPluginDescriptor(descriptor, fileTypeProbe);
            if (!theme.IsValid)
            {
                throw new InvalidDataException(string.Join(Environment.NewLine, theme.Diagnostics.Select(item => item.Message)));
            }
        }

        var currentDirectory = GetPackageDirectory(descriptor.Id);
        var recorded = host.GetRecords().Select(record => record.Descriptor);
        using var diskHost = new PluginHost(host.ActiveSurface);
        var disk = diskHost.DiscoverExternal(Layout.RootPath, fileTypeProbe).Discovered;
        foreach (var existing in recorded.Concat(disk))
        {
            if (existing.RootDirectory == directory)
            {
                continue;
            }
            if (string.Equals(existing.Id, descriptor.Id, StringComparison.Ordinal))
            {
                if (updateId is null || existing.RootDirectory is not { } root
                    || !AddonInstallLayout.IsWithin(currentDirectory, root))
                {
                    throw new InvalidDataException($"'{descriptor.Id}' is already registered.");
                }
            }
            else if (descriptor.SourceId != Guid.Empty && descriptor.SourceId == existing.SourceId
                && (descriptor.Capabilities & existing.Capabilities
                    & (PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch)) != 0)
            {
                throw new InvalidDataException($"The package source id is already used by '{existing.Id}'.");
            }
        }

        return descriptor;
    }

    internal PluginDescriptor ReadPackageDescriptor(string directory)
    {
        var manifest = new FileInfo(Path.Combine(directory, PluginManifestParser.PluginManifestFileName));
        var isV2 = manifest.Exists || manifest.LinkTarget is not null;
        if (!isV2)
        {
            manifest = new FileInfo(Path.Combine(directory, PluginManifestParser.LegacyExtensionManifestFileName));
        }
        if (!manifest.Exists || manifest.LinkTarget is not null || !fileTypeProbe.IsRegularFile(manifest.FullName))
        {
            throw new InvalidDataException("The package requires a regular root plugin.json or extension.yaml manifest.");
        }

        using var input = OpenPackageFile(manifest.FullName);
        var buffer = new byte[PluginManifestParser.MaxManifestBytes + 1];
        var size = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (size == buffer.Length)
        {
            throw new InvalidDataException($"'{manifest.Name}' exceeds the {PluginManifestParser.MaxManifestBytes}-byte limit.");
        }

        var text = Encoding.UTF8.GetString(buffer, 0, size);
        var (descriptor, diagnostics) = isV2
            ? PluginManifestParser.ParseV2Manifest(text, directory, manifest.Name)
            : PluginManifestParser.ParseLegacyExtensionManifest(text, directory, manifest.Name);
        if (descriptor is null)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, diagnostics.Select(item => item.Message)));
        }

        var entryPoints = descriptor.EntryPoints;
        foreach (var entry in new[] { entryPoints.Core, entryPoints.Desktop, entryPoints.Fullscreen, entryPoints.Theme }.OfType<string>())
        {
            var path = directory;
            foreach (var segment in entry.Split('/'))
            {
                path = Path.Combine(path, segment);
                if (new FileInfo(path).LinkTarget is not null)
                {
                    throw new InvalidDataException($"Declared entry point '{entry}' contains a symbolic link.");
                }
            }
            if (!fileTypeProbe.IsRegularFile(path))
            {
                throw new InvalidDataException($"Declared entry point '{entry}' is missing or not a regular file.");
            }
        }

        return descriptor;
    }

    private async Task CopyPackageAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!IsPlainDirectory(source) || AddonInstallLayout.IsWithin(source, destination))
        {
            throw new InvalidDataException("Select a regular package folder outside the package storage hierarchy.");
        }

        var (entries, diagnostic) = ListEntries(Path.GetFullPath(source), cancellationToken, MaxPackageEntries);
        if (entries is null)
        {
            throw new InvalidDataException(diagnostic!.Message);
        }

        var buffer = new byte[81920];
        long bytes = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var input = OpenPackageFile(entry.SourcePath);
            bytes += await CopyPackageEntryAsync(
                input, Path.Combine(destination, entry.RelativePath), buffer, MaxPackageBytes - bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExtractPackageAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = OpenPackageFile(source);
        if (input.Length > MaxPackageBytes)
        {
            throw new InvalidDataException("Select a regular package archive of at most 1 GiB.");
        }

        var buffer = new byte[81920];
        // Snapshot the archive before both directory validation and extraction.
        await using var inputFile = new FileStream(Path.Combine(PackageRoot, $".archive-{Path.GetRandomFileName()}"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, buffer.Length,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await CopyPackageBytesAsync(input, inputFile, buffer, MaxPackageBytes, cancellationToken).ConfigureAwait(false);
        await VerifyZipDirectoryAsync(inputFile, cancellationToken).ConfigureAwait(false);
        using var archive = new ZipArchive(inputFile, ZipArchiveMode.Read, leaveOpen: true);

        long bytes = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var relative = directory ? entry.FullName[..^1] : entry.FullName;
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (!AddonPathSafety.IsSafeRelativePath(relative) || !paths.Add(relative)
                || (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000))
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Unsupported or duplicate package entry '{entry.FullName}'.");
            }

            var target = Path.Combine(destination, relative);
            if (directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.Length > MaxPackageBytes - bytes)
            {
                throw new InvalidDataException("The unpacked package exceeds 1 GiB.");
            }

            await using var entryStream = entry.Open();
            bytes += await CopyPackageEntryAsync(entryStream, target, buffer, MaxPackageBytes - bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> CopyPackageEntryAsync(
        Stream input, string target, Memory<byte> buffer, long remainingBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        return await CopyPackageBytesAsync(input, output, buffer, remainingBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> CopyPackageBytesAsync(
        Stream input, Stream output, Memory<byte> buffer, long remainingBytes, CancellationToken cancellationToken)
    {
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            copied += count;
            if (copied > remainingBytes)
            {
                throw new InvalidDataException("The unpacked package exceeds 1 GiB.");
            }
            await output.WriteAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        }
        return copied;
    }

    private string GetPackageDirectory(string id)
    {
        if (!IsPackageId(id))
        {
            throw new InvalidDataException($"Invalid package id '{id}'.");
        }
        return Path.Combine(PackageRoot, id);
    }

    private static bool IsPackageId(string id) =>
        AddonPathSafety.IsSafeSegment(id) && id is not (".lock" or ".usage")
        && !id.StartsWith(".staging-", StringComparison.Ordinal) && !id.StartsWith(".archive-", StringComparison.Ordinal);

    private Stream OpenPackageFile(string path) =>
        fileTypeProbe is IAddonPackageFileReader reader
            ? reader.OpenPackageFile(path)
            : throw new InvalidDataException("No handle-bound file reader is configured for managed packages.");

    private FileStream OpenPackageLock()
    {
        EnsureDirectory(Layout.RootPath);
        EnsureDirectory(PackageRoot);
        return OpenLock(".lock", FileShare.None);
    }

    private FileStream OpenLock(string name, FileShare share)
    {
        var path = Path.Combine(PackageRoot, name);
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || (file.Exists && !fileTypeProbe.IsRegularFile(path)))
        {
            throw new InvalidDataException("The package lock is not a regular file.");
        }
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
    }

    private static bool IsPlainDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        return directory.Exists && directory.LinkTarget is null;
    }

    private static void EnsureDirectory(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
        {
            throw new InvalidDataException($"Refusing symbolic link '{path}'.");
        }
        Directory.CreateDirectory(path);
    }

    private PackageState? ReadStateForChange(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (info.LinkTarget is not null)
        {
            throw new InvalidDataException($"Refusing symbolic link '{directory}'.");
        }
        if (!info.Exists)
        {
            return null;
        }

        return ReadPackageState(directory);
    }

    private PackageState? ReadPackageState(string directory)
    {
        var id = Path.GetFileName(directory);
        if (!IsPackageId(id) || ReadStateLines(directory) is not { } lines || lines is [RemovedState])
        {
            return null;
        }

        if (lines.Length is < 1 or > 2 || ParseVersion(lines[0]) is not { } active)
        {
            throw new InvalidDataException($"The stored active version for '{id}' is invalid.");
        }

        PackageVersion? previous = lines.Length == 2 ? ParseVersion(lines[1]) : null;
        if (lines.Length == 2 && (previous is null || previous.Version >= active.Version || previous.Generation == active.Generation))
        {
            throw new InvalidDataException($"The stored rollback version for '{id}' is invalid.");
        }
        return new PackageState(new ManagedAddonPackage(id, active.Version, previous?.Version), active, previous);
    }

    private string[]? ReadStateLines(string directory)
    {
        var path = Path.Combine(directory, StateFileName);
        var file = new FileInfo(path);
        if (!file.Exists && file.LinkTarget is null)
        {
            return null;
        }

        using var input = OpenPackageFile(path);
        Span<byte> buffer = stackalloc byte[129];
        var count = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (count == buffer.Length)
        {
            throw new InvalidDataException("The stored package version record exceeds 128 bytes.");
        }
        return Encoding.UTF8.GetString(buffer[..count]).Split('\n', StringSplitOptions.TrimEntries);
    }

    private static PackageVersion? ParseVersion(string line)
    {
        var fields = line.Split('|');
        return fields.Length == 2 && Version.TryParse(fields[0], out var version) && IsGeneration(fields[1])
            ? new PackageVersion(version, fields[1])
            : null;
    }

    private static bool IsGeneration(string name) =>
        name.Length == 12 && name[8] == '.' && name.All(character => character == '.' || char.IsAsciiLetterOrDigit(character))
        && name.Count(character => character == '.') == 1;

    private static void WritePackageState(string directory, PackageVersion? active, PackageVersion? previous)
    {
        var temporary = Path.Combine(directory, $".versions-{Path.GetRandomFileName()}");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var content = active is null ? RemovedState : $"{active.Version}|{active.Generation}";
                if (previous is not null)
                {
                    content += $"\n{previous.Version}|{previous.Generation}";
                }
                stream.Write(Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(directory, StateFileName), overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record PackageVersion(Version Version, string Generation);
    private sealed record PackageState(ManagedAddonPackage Package, PackageVersion Active, PackageVersion? Previous);
}
