namespace Playnite.Core.Addons;

/// <summary>
/// Deterministic directory model for installed extensions. The layout under the extensions root
/// is <c>&lt;id&gt;/&lt;version&gt;/...</c>: the id is one validated directory name and the
/// version is the normalised <see cref="Version"/> string, so the same add-on id and version
/// always resolve to the same directory and different versions cannot overwrite each other.
/// The root is the v10 <c>Extensions</c> user-data directory. v10 installs there as
/// <c>&lt;id&gt;/extension.yaml</c> and discovers manifests recursively, so both that layout and
/// the extra version segment stay visible to <see cref="GetInstalled"/>.
/// </summary>
public sealed class AddonInstallLayout
{
    /// <summary>The extension manifest file name used by v10 and p6-009.</summary>
    public const string ExtensionManifestFileName = "extension.yaml";

    /// <summary>
    /// The maximum number of directory levels <see cref="GetInstalled"/> descends below the
    /// extensions root. v10's enumerator has no bound; this model adds one so a hostile tree
    /// cannot make discovery unbounded. Both supported layouts sit at depth one or two.
    /// </summary>
    public const int MaxDiscoveryDepth = 8;

    /// <summary>Creates a layout rooted at one extensions directory.</summary>
    /// <param name="extensionsRoot">Directory that contains all installed extension directories.</param>
    public AddonInstallLayout(string extensionsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionsRoot);
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extensionsRoot));
    }

    /// <summary>Gets the absolute extensions root. It is not created by the constructor.</summary>
    public string RootPath { get; }

    /// <summary>
    /// Resolves the directory that owns every version of one add-on.
    /// </summary>
    public bool TryGetAddonDirectory(
        string? addonId,
        out string? directory,
        out AddonDiagnostic? diagnostic)
    {
        if (!AddonPathSafety.IsSafeSegment(addonId))
        {
            directory = null;
            diagnostic = new AddonDiagnostic(
                AddonManifestValidator.UnsafeIdCode,
                "$.Id",
                $"Id '{addonId}' must be one safe directory name.");
            return false;
        }

        directory = Path.Combine(RootPath, addonId!);
        if (!IsWithin(RootPath, directory))
        {
            directory = null;
            diagnostic = new AddonDiagnostic(
                AddonManifestValidator.UnsafeIdCode,
                "$.Id",
                $"Id '{addonId}' escapes the extensions root.");
            return false;
        }

        diagnostic = null;
        return true;
    }

    /// <summary>
    /// Resolves the stable install directory for one add-on id and version.
    /// </summary>
    public bool TryGetVersionDirectory(
        string? addonId,
        string? version,
        out string? directory,
        out AddonDiagnostic? diagnostic)
    {
        if (!TryGetAddonDirectory(addonId, out var addonDirectory, out diagnostic))
        {
            directory = null;
            return false;
        }

        if (!Version.TryParse(version, out var parsed))
        {
            directory = null;
            diagnostic = new AddonDiagnostic(
                "unsafe-version",
                "$.Version",
                $"Version '{version}' must be a System.Version string.");
            return false;
        }

        var versionName = parsed.ToString();
        directory = Path.Combine(addonDirectory!, versionName);
        if (!AddonPathSafety.IsSafeSegment(versionName) || !IsWithin(addonDirectory!, directory))
        {
            directory = null;
            diagnostic = new AddonDiagnostic(
                "unsafe-version",
                "$.Version",
                $"Version '{version}' does not form a safe directory name.");
            return false;
        }

        diagnostic = null;
        return true;
    }

    /// <summary>
    /// Enumerates installed extensions by the same recursive manifest rule v10 discovery uses.
    /// An <c>extension.yaml</c> at any depth up to <see cref="MaxDiscoveryDepth"/> below the
    /// root identifies the add-on whose id is the first path segment; its version and validity
    /// come from the parsed manifest. Symlinked directories and manifests are never followed, a
    /// directory that cannot be read is skipped, and a manifest that does not validate is
    /// skipped. Results are ordered by manifest path.
    /// </summary>
    public IReadOnlyList<InstalledAddon> GetInstalled()
    {
        var installed = new List<InstalledAddon>();
        if (!Directory.Exists(RootPath))
        {
            return installed;
        }

        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(RootPath), 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            FileInfo[] manifests;
            DirectoryInfo[] children;
            try
            {
                manifests = directory.GetFiles(ExtensionManifestFileName, SearchOption.TopDirectoryOnly);
                children = depth < MaxDiscoveryDepth ? directory.GetDirectories() : [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // v10's SafeFileEnumerator skips a directory it cannot read; discovery mirrors it.
                continue;
            }

            foreach (var manifest in manifests)
            {
                AddInstalledManifest(installed, manifest);
            }

            foreach (var child in children)
            {
                if (child.LinkTarget is null)
                {
                    pending.Push((child, depth + 1));
                }
            }
        }

        installed.Sort((left, right) => StringComparer.Ordinal.Compare(left.ManifestPath, right.ManifestPath));
        return installed;
    }

    /// <summary>Gets whether a candidate path resolves inside its expected directory.</summary>
    internal static bool IsWithin(string directory, string candidate)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.Ordinal);
    }

    private void AddInstalledManifest(List<InstalledAddon> installed, FileInfo manifest)
    {
        if (manifest.LinkTarget is not null || manifest.DirectoryName is null)
        {
            return;
        }

        // The id is the directory that owns the add-on, not a value re-derived from the
        // manifest: uninstall deletes exactly that directory, and v10 names it with the
        // sanitised manifest id. A manifest directly in the root has no owning directory.
        var relativeDirectory = Path.GetRelativePath(RootPath, manifest.DirectoryName);
        var segments = relativeDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || !AddonPathSafety.IsSafeSegment(segments[0]))
        {
            return;
        }

        string document;
        try
        {
            document = File.ReadAllText(manifest.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var validation = AddonManifestValidator.ValidateExtension(document, manifest.Name);
        if (validation.Value is not { } extension || validation.Diagnostics.Count > 0)
        {
            return;
        }

        if (!Version.TryParse(extension.Version, out var version))
        {
            return;
        }

        installed.Add(new InstalledAddon(segments[0], version, manifest.DirectoryName, manifest.FullName));
    }
}

/// <summary>One installed extension found under the extensions root.</summary>
/// <param name="AddonId">Installed add-on id, taken from the directory that owns the manifest.</param>
/// <param name="Version">Version declared by the installed manifest.</param>
/// <param name="Directory">Directory that contains the manifest.</param>
/// <param name="ManifestPath">Absolute path to <c>extension.yaml</c>.</param>
public sealed record InstalledAddon(string AddonId, Version Version, string Directory, string ManifestPath);
