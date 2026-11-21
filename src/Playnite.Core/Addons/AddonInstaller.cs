using Playnite.Core.Manifests;

namespace Playnite.Core.Addons;

/// <summary>
/// Deterministic install and uninstall over <see cref="AddonInstallLayout"/>. A package is a
/// directory that contains the v10 manifest file (<c>extension.yaml</c>); the manifest is parsed
/// and validated first, then every entry is checked before any target directory is created.
/// Entry checks use the injected <see cref="IRegularFileProbe"/> because Unix reports a FIFO,
/// socket or device as a <see cref="FileInfo"/>, and copying such an entry can block forever.
/// There is no download, catalogue or update path in v1.
/// </summary>
public sealed class AddonInstaller
{
    private readonly IRegularFileProbe fileTypeProbe;

    /// <summary>Creates an installer over one extensions root.</summary>
    /// <param name="layout">Directory model for the extensions root.</param>
    /// <param name="fileTypeProbe">Host entry-type classifier used to reject non-regular files.</param>
    public AddonInstaller(AddonInstallLayout layout, IRegularFileProbe fileTypeProbe)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fileTypeProbe);
        Layout = layout;
        this.fileTypeProbe = fileTypeProbe;
    }

    /// <summary>Gets the directory model used by this installer.</summary>
    public AddonInstallLayout Layout { get; }

    /// <summary>Installs a directory containing <c>extension.yaml</c>.</summary>
    /// <param name="sourceDirectory">Unpacked package directory.</param>
    /// <param name="expectedVersion">Optional version the caller requested; a mismatch is refused.</param>
    public AddonInstallResult InstallExtension(string sourceDirectory, Version? expectedVersion = null) =>
        InstallCore(sourceDirectory, expectedVersion);

    /// <summary>Removes every version of one add-on id.</summary>
    public AddonUninstallResult Uninstall(string addonId)
    {
        if (!Layout.TryGetAddonDirectory(addonId, out var addonDirectory, out var layoutDiagnostic))
        {
            return new AddonUninstallResult(false, null, [layoutDiagnostic!]);
        }

        if (!Directory.Exists(addonDirectory))
        {
            return new AddonUninstallResult(
                false,
                addonDirectory,
                [new AddonDiagnostic("not-installed", "$.Id", $"Add-on '{addonId}' is not installed.")]);
        }

        try
        {
            Directory.Delete(addonDirectory!, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new AddonUninstallResult(
                false,
                addonDirectory,
                [new AddonDiagnostic(
                    "uninstall-failed",
                    "$.Id",
                    $"Could not remove '{addonDirectory}': {exception.Message}")]);
        }

        if (Directory.Exists(addonDirectory))
        {
            return new AddonUninstallResult(
                false,
                addonDirectory,
                [new AddonDiagnostic(
                    "uninstall-failed",
                    "$.Id",
                    $"Removing '{addonDirectory}' left the directory behind.")]);
        }

        return new AddonUninstallResult(true, addonDirectory, Array.Empty<AddonDiagnostic>());
    }

    private AddonInstallResult InstallCore(string sourceDirectory, Version? expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("A package directory is required.", nameof(sourceDirectory));
        }

        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        if (!Directory.Exists(source))
        {
            return Fail("missing-source", "$", $"Package directory '{source}' does not exist.");
        }

        var sourceManifest = Path.Combine(source, AddonInstallLayout.ExtensionManifestFileName);
        if (!File.Exists(sourceManifest))
        {
            return Fail(
                "missing-manifest",
                "$",
                $"Package directory '{source}' has no {AddonInstallLayout.ExtensionManifestFileName}.");
        }

        // The manifest is read before the entry walk, so it needs the same type check: reading
        // a FIFO named extension.yaml would block forever.
        if (!fileTypeProbe.IsRegularFile(sourceManifest))
        {
            return Fail(
                "unsupported-entry",
                "$",
                $"Package manifest '{sourceManifest}' is not a regular file.");
        }

        string document;
        try
        {
            document = File.ReadAllText(sourceManifest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail(
                "unreadable-manifest",
                "$",
                $"Could not read '{sourceManifest}': {exception.Message}");
        }

        var validation = AddonManifestValidator.ValidateExtension(
            document,
            AddonInstallLayout.ExtensionManifestFileName);
        if (validation.Value is not { } manifest || validation.Diagnostics.Count > 0)
        {
            return new AddonInstallResult(false, validation.Value, null, null, validation.Diagnostics);
        }

        // The p3-005 parser guarantees a real Version and a non-empty id here.
        var manifestVersion = Version.Parse(manifest.Version!);
        if (expectedVersion is not null && manifestVersion != expectedVersion)
        {
            return Fail(
                "version-mismatch",
                "$.Version",
                $"{AddonInstallLayout.ExtensionManifestFileName}: manifest version {manifestVersion} does not match requested {expectedVersion}.",
                manifest);
        }

        if (!Layout.TryGetVersionDirectory(manifest.Id, manifest.Version, out var versionDirectory, out var layoutDiagnostic))
        {
            return new AddonInstallResult(false, manifest, null, null, [layoutDiagnostic!]);
        }

        var addonDirectory = Path.GetDirectoryName(versionDirectory!)!;
        if (Directory.Exists(addonDirectory))
        {
            return Fail(
                "duplicate-addon",
                "$.Id",
                $"Add-on '{manifest.Id}' is already installed at '{addonDirectory}'.",
                manifest);
        }

        List<SourceEntry>? entries;
        AddonDiagnostic? entryDiagnostic;
        try
        {
            (entries, entryDiagnostic) = ListEntries(source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail(
                "unreadable-source",
                "$",
                $"Could not enumerate '{source}': {exception.Message}",
                manifest);
        }

        if (entries is null)
        {
            return new AddonInstallResult(false, manifest, null, null, [entryDiagnostic!]);
        }

        var copies = new List<(string Source, string Target)>(entries.Count);
        foreach (var entry in entries)
        {
            var target = Path.Combine(versionDirectory!, entry.RelativePath);
            if (!AddonInstallLayout.IsWithin(versionDirectory!, target))
            {
                return Fail(
                    "unsafe-path",
                    "$",
                    $"Package entry '{entry.RelativePath}' escapes '{versionDirectory}'.",
                    manifest);
            }

            copies.Add((entry.SourcePath, target));
        }

        try
        {
            Directory.CreateDirectory(versionDirectory!);
            foreach (var (copySource, copyTarget) in copies)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copyTarget)!);
                File.Copy(copySource, copyTarget, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (Directory.Exists(addonDirectory))
                {
                    Directory.Delete(addonDirectory, recursive: true);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                return Fail(
                    "install-failed",
                    "$",
                    $"Copying into '{versionDirectory}' failed: {exception.Message}; rollback also failed: {cleanup.Message}",
                    manifest);
            }

            return Fail(
                "install-failed",
                "$",
                $"Copying into '{versionDirectory}' failed: {exception.Message}",
                manifest);
        }

        return new AddonInstallResult(
            true,
            manifest,
            versionDirectory,
            Path.Combine(versionDirectory!, AddonInstallLayout.ExtensionManifestFileName),
            Array.Empty<AddonDiagnostic>());
    }

    private (List<SourceEntry>? Entries, AddonDiagnostic? Diagnostic) ListEntries(string source)
    {
        var entries = new List<SourceEntry>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(source));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                var relativePath = Path.GetRelativePath(source, entry.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!AddonPathSafety.IsSafeRelativePath(relativePath))
                {
                    return (null, new AddonDiagnostic(
                        "unsafe-path",
                        "$",
                        $"Package entry '{relativePath}' is not a safe relative path."));
                }

                // A symlink can point outside the package even when its name looks harmless.
                if (entry.LinkTarget is not null)
                {
                    return (null, new AddonDiagnostic(
                        "symlink",
                        "$",
                        $"Package entry '{relativePath}' is a symbolic link."));
                }

                switch (entry)
                {
                    case DirectoryInfo childDirectory:
                        pending.Push(childDirectory);
                        break;

                    // Unix reports a FIFO, socket or device as a FileInfo, so the managed type
                    // alone cannot tell a copyable file from an entry that would block forever.
                    case FileInfo file when fileTypeProbe.IsRegularFile(file.FullName):
                        entries.Add(new SourceEntry(file.FullName, relativePath));
                        break;

                    default:
                        return (null, new AddonDiagnostic(
                            "unsupported-entry",
                            "$",
                            $"Package entry '{relativePath}' is not a regular file or directory."));
                }
            }
        }

        entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return (entries, null);
    }

    private static AddonInstallResult Fail(
        string code,
        string path,
        string message,
        BaseExtensionManifest? manifest = null) =>
        new(false, manifest, null, null, [new AddonDiagnostic(code, path, message)]);

    private sealed record SourceEntry(string SourcePath, string RelativePath);
}
