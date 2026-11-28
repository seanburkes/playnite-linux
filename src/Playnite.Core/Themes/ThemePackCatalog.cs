using Playnite.Core.Addons;
using Playnite.Core.Plugins;

namespace Playnite.Core.Themes;

/// <summary>
/// Discovers and validates declarative theme packs across configured theme roots while keeping
/// the built-in Charcoal theme always first and never allowing a broken pack to crash discovery.
/// </summary>
public static class ThemePackCatalog
{
    public const int MaxDiscoveredPacks = 128;

    /// <summary>
    /// Scans <paramref name="roots"/> (including v10 <c>Desktop/</c> subdirectories) plus any explicit
    /// <paramref name="extraPackDirectories"/> and <paramref name="themePlugins"/>, returning the built-in
    /// Charcoal entry followed by discovered valid and invalid packs.
    /// </summary>
    public static ThemePackDiscoveryResult Discover(
        IReadOnlyList<string> roots,
        IRegularFileProbe regularFileProbe,
        IReadOnlyList<string>? extraPackDirectories = null,
        IThemeFileReader? fileReader = null,
        IReadOnlyList<PluginDescriptor>? themePlugins = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(regularFileProbe);

        var entries = new List<ThemePackCatalogEntry>
        {
            CreateBuiltInCharcoalEntry(),
        };
        var diagnostics = new List<ThemePackDiagnostic>();
        var seenValidIds = new HashSet<string>(StringComparer.Ordinal)
        {
            ThemePackDefaults.DefaultDesktopThemeId,
        };
        var scannedDirs = new HashSet<string>(StringComparer.Ordinal);
        var validPacks = new List<ThemePackCatalogEntry>();
        var invalidPacks = new List<ThemePackCatalogEntry>();
        var scannedCount = 0;
        var limitReported = false;

        foreach (var root in roots)
        {
            if (scannedCount >= MaxDiscoveredPacks)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(root) || root.Contains('\0') || !Path.IsPathFullyQualified(root))
            {
                continue;
            }

            var fullRoot = ThemePackParser.TrimRootPath(Path.GetFullPath(root));
            if (!Directory.Exists(fullRoot))
            {
                continue;
            }

            if (ThemePackParser.IsSymlinkOrReparsePoint(new DirectoryInfo(fullRoot)))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    string.Empty,
                    ThemePackParser.UnsafePathCode,
                    fullRoot,
                    $"Refusing symlinked themes root '{fullRoot}'."));
                continue;
            }

            foreach (var candidate in EnumerateCandidateDirectories(fullRoot, diagnostics))
            {
                if (scannedCount >= MaxDiscoveredPacks)
                {
                    if (!limitReported)
                    {
                        limitReported = true;
                        diagnostics.Add(new ThemePackDiagnostic(
                            string.Empty,
                            "discovery-limit",
                            fullRoot,
                            $"Theme discovery stopped at the {MaxDiscoveredPacks}-pack limit."));
                    }
                    break;
                }

                if (!scannedDirs.Add(candidate.PackDirectory))
                {
                    continue;
                }

                scannedCount++;
                var validation = ThemePackParser.LoadFromDirectory(
                    candidate.PackDirectory,
                    regularFileProbe,
                    candidate.ExpectedRoot,
                    fileReader);
                RecordPackResult(
                    validation,
                    candidate.PackDirectory,
                    fallbackName: null,
                    seenValidIds,
                    validPacks,
                    invalidPacks,
                    diagnostics);
            }
        }

        if (themePlugins is not null)
        {
            foreach (var plugin in themePlugins)
            {
                if (scannedCount >= MaxDiscoveredPacks)
                {
                    if (!limitReported)
                    {
                        limitReported = true;
                        diagnostics.Add(new ThemePackDiagnostic(
                            plugin.Id,
                            "discovery-limit",
                            plugin.RootDirectory ?? "$",
                            $"Theme discovery stopped at the {MaxDiscoveredPacks}-pack limit."));
                    }
                    break;
                }

                if (string.IsNullOrWhiteSpace(plugin.RootDirectory) || !Path.IsPathFullyQualified(plugin.RootDirectory))
                {
                    continue;
                }

                var fullPluginDir = ThemePackParser.TrimRootPath(Path.GetFullPath(plugin.RootDirectory));
                if (!scannedDirs.Add(fullPluginDir))
                {
                    continue;
                }

                scannedCount++;
                var validation = ThemePackParser.LoadFromPluginDescriptor(plugin, regularFileProbe, fileReader);
                RecordPackResult(
                    validation,
                    fullPluginDir,
                    fallbackName: plugin.Name,
                    seenValidIds,
                    validPacks,
                    invalidPacks,
                    diagnostics);
            }
        }

        if (extraPackDirectories is not null)
        {
            foreach (var extraDir in extraPackDirectories)
            {
                if (scannedCount >= MaxDiscoveredPacks)
                {
                    if (!limitReported)
                    {
                        limitReported = true;
                        diagnostics.Add(new ThemePackDiagnostic(
                            string.Empty,
                            "discovery-limit",
                            extraDir,
                            $"Theme discovery stopped at the {MaxDiscoveredPacks}-pack limit."));
                    }
                    break;
                }

                if (string.IsNullOrWhiteSpace(extraDir) || extraDir.Contains('\0') || !Path.IsPathFullyQualified(extraDir))
                {
                    continue;
                }

                var fullExtra = ThemePackParser.TrimRootPath(Path.GetFullPath(extraDir));
                var manifestCandidate = Path.Combine(fullExtra, ThemePackParser.ManifestFileName);
                if (!Directory.Exists(fullExtra)
                    || (!File.Exists(manifestCandidate) && !ThemePackParser.IsPathSymlinkOrReparsePoint(manifestCandidate)))
                {
                    continue;
                }

                if (!scannedDirs.Add(fullExtra))
                {
                    continue;
                }

                scannedCount++;
                var validation = ThemePackParser.LoadFromDirectory(
                    fullExtra,
                    regularFileProbe,
                    expectedRoot: null,
                    fileReader);
                RecordPackResult(
                    validation,
                    fullExtra,
                    fallbackName: null,
                    seenValidIds,
                    validPacks,
                    invalidPacks,
                    diagnostics);
            }
        }

        validPacks.Sort((left, right) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(left.Id, right.Id);
        });
        invalidPacks.Sort((left, right) =>
        {
            var byId = StringComparer.Ordinal.Compare(left.Id, right.Id);
            return byId != 0 ? byId : StringComparer.Ordinal.Compare(left.RootDirectory, right.RootDirectory);
        });

        entries.AddRange(validPacks);
        entries.AddRange(invalidPacks);
        return new ThemePackDiscoveryResult(entries, diagnostics);
    }

    private static ThemePackCatalogEntry CreateBuiltInCharcoalEntry() =>
        new(
            ThemePackDefaults.DefaultDesktopThemeId,
            ThemePackDefaults.DefaultThemeName,
            ThemePackDefaults.BuiltInCharcoal.Descriptor,
            IsValid: true,
            RootDirectory: null,
            Diagnostics: []);

    private static void RecordPackResult(
        ThemePackValidationResult result,
        string packDirectory,
        string? fallbackName,
        HashSet<string> seenValidIds,
        List<ThemePackCatalogEntry> validPacks,
        List<ThemePackCatalogEntry> invalidPacks,
        List<ThemePackDiagnostic> diagnostics)
    {
        if (result.IsValid && result.Document is { } document)
        {
            if (!seenValidIds.Add(document.Descriptor.Id))
            {
                var duplicate = new ThemePackDiagnostic(
                    document.Descriptor.Id,
                    "duplicate-theme-id",
                    packDirectory,
                    $"Duplicate theme id '{document.Descriptor.Id}' at '{packDirectory}' was ignored.");
                diagnostics.Add(duplicate);
                invalidPacks.Add(new ThemePackCatalogEntry(
                    document.Descriptor.Id,
                    $"{document.Descriptor.Name} (Duplicate)",
                    document.Descriptor,
                    IsValid: false,
                    packDirectory,
                    [duplicate]));
                return;
            }

            validPacks.Add(new ThemePackCatalogEntry(
                document.Descriptor.Id,
                document.Descriptor.Name,
                document.Descriptor,
                IsValid: true,
                packDirectory,
                []));
            return;
        }

        var fallbackId = Path.GetFileName(packDirectory);
        if (string.IsNullOrWhiteSpace(fallbackId))
        {
            fallbackId = packDirectory;
        }

        var diagId = result.Diagnostics.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.ThemeId))?.ThemeId ?? fallbackId;
        var displayName = string.IsNullOrWhiteSpace(fallbackName) ? $"{fallbackId} (Invalid)" : $"{fallbackName} (Invalid)";
        diagnostics.AddRange(result.Diagnostics);
        invalidPacks.Add(new ThemePackCatalogEntry(
            diagId,
            displayName,
            Descriptor: null,
            IsValid: false,
            packDirectory,
            result.Diagnostics));
    }

    private static IEnumerable<(string PackDirectory, string ExpectedRoot)> EnumerateCandidateDirectories(
        string fullRoot,
        List<ThemePackDiagnostic> diagnostics)
    {
        var candidates = new List<(string PackDirectory, string ExpectedRoot)>();
        var scanRoots = new List<string> { fullRoot };
        var desktopSub = Path.Combine(fullRoot, "Desktop");
        if (Directory.Exists(desktopSub))
        {
            if (ThemePackParser.IsSymlinkOrReparsePoint(new DirectoryInfo(desktopSub)))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    string.Empty,
                    ThemePackParser.UnsafePathCode,
                    desktopSub,
                    $"Refusing symlinked themes directory '{desktopSub}'."));
            }
            else
            {
                scanRoots.Add(desktopSub);
            }
        }

        foreach (var scanRoot in scanRoots)
        {
            DirectoryInfo[] children;
            try
            {
                children = new DirectoryInfo(scanRoot).GetDirectories()
                    .OrderBy(d => d.Name, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    string.Empty,
                    ThemePackParser.ReadFailedCode,
                    scanRoot,
                    $"Could not scan themes directory '{scanRoot}': {exception.Message}"));
                continue;
            }

            foreach (var child in children)
            {
                if (string.Equals(scanRoot, fullRoot, StringComparison.Ordinal)
                    && string.Equals(child.Name, "Desktop", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ThemePackParser.IsSymlinkOrReparsePoint(child))
                {
                    var symlinkManifest = Path.Combine(child.FullName, ThemePackParser.ManifestFileName);
                    if (File.Exists(symlinkManifest))
                    {
                        diagnostics.Add(new ThemePackDiagnostic(
                            child.Name,
                            ThemePackParser.UnsafePathCode,
                            child.FullName,
                            $"Refusing symlinked theme pack directory '{child.FullName}'."));
                    }

                    continue;
                }

                var manifestCandidate = Path.Combine(child.FullName, ThemePackParser.ManifestFileName);
                if (File.Exists(manifestCandidate) || ThemePackParser.IsPathSymlinkOrReparsePoint(manifestCandidate))
                {
                    candidates.Add((child.FullName, fullRoot));
                }
            }
        }

        return candidates;
    }
}
