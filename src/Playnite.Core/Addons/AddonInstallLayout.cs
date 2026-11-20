namespace Playnite.Core.Addons;

/// <summary>
/// Deterministic directory model for installed add-ons. The layout under the extensions root
/// is <c>&lt;id&gt;/&lt;version&gt;/...</c>: the id is one validated directory name and the
/// version is the normalised <see cref="Version"/> string, so the same add-on id and version
/// always resolve to the same directory and different versions cannot overwrite each other.
/// The root is the v10 <c>Extensions</c> user-data directory; v10 discovery walks it
/// recursively, so the extra version segment stays discoverable.
/// </summary>
public sealed class AddonInstallLayout
{
    /// <summary>The extension manifest file name used by v10 and p6-009.</summary>
    public const string ExtensionManifestFileName = "extension.yaml";

    /// <summary>The theme manifest file name used by v10.</summary>
    public const string ThemeManifestFileName = "theme.yaml";

    /// <summary>Creates a layout rooted at one extensions directory.</summary>
    /// <param name="extensionsRoot">Directory that contains all installed add-on directories.</param>
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
    /// Enumerates installed add-ons by the same recursive manifest rule v10 discovery uses.
    /// Directories that do not contain a valid id, version and manifest are skipped.
    /// </summary>
    public IReadOnlyList<InstalledAddon> GetInstalled()
    {
        var installed = new List<InstalledAddon>();
        if (!Directory.Exists(RootPath))
        {
            return installed;
        }

        foreach (var addonDirectory in Directory.EnumerateDirectories(RootPath).Order(StringComparer.Ordinal))
        {
            var addonId = Path.GetFileName(addonDirectory);
            if (!AddonPathSafety.IsSafeSegment(addonId)
                || new DirectoryInfo(addonDirectory).LinkTarget is not null)
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(addonDirectory).Order(StringComparer.Ordinal))
            {
                if (new DirectoryInfo(versionDirectory).LinkTarget is not null)
                {
                    continue;
                }

                var versionName = Path.GetFileName(versionDirectory);
                if (!Version.TryParse(versionName, out var version))
                {
                    continue;
                }

                var manifestPath = Path.Combine(versionDirectory, ExtensionManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    manifestPath = Path.Combine(versionDirectory, ThemeManifestFileName);
                }

                if (File.Exists(manifestPath))
                {
                    installed.Add(new InstalledAddon(addonId, version, versionDirectory, manifestPath));
                }
            }
        }

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
}

/// <summary>One add-on found under the extensions root.</summary>
/// <param name="AddonId">Installed add-on id.</param>
/// <param name="Version">Installed manifest version.</param>
/// <param name="Directory">Stable version directory.</param>
/// <param name="ManifestPath">Absolute path to <c>extension.yaml</c> or <c>theme.yaml</c>.</param>
public sealed record InstalledAddon(string AddonId, Version Version, string Directory, string ManifestPath);
