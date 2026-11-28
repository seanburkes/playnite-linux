using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Playnite.Core.Addons;
using Playnite.Core.Manifests;
using Playnite.Core.Plugins;
using Playnite.Core.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using Version = System.Version;

namespace Playnite.Core.Themes;

/// <summary>
/// Pure, bounded validator and parser for declarative theme packs (<c>theme.yaml</c>,
/// <c>tokens.yaml</c>, and allow-listed declarative <c>.axaml</c> resource/style files).
/// Never executes XAML or loads .NET assemblies.
/// </summary>
public static class ThemePackParser
{
    public const string ManifestFileName = "theme.yaml";
    public const string TokensFileName = "tokens.yaml";
    public const string AxamlFileName = "theme.axaml";
    public const int MaxManifestBytes = 256 * 1024;
    public const int MaxResourceFileBytes = 128 * 1024;
    public const int MaxResourceFiles = 16;
    public const int MaxTokensPerPack = 128;
    public const int MaxYamlDepth = 16;
    public const int MaxXmlDepth = 16;
    public const int MaxNameLength = 128;
    public const int MaxDescriptionLength = 512;
    public const int MaxFontFamilyLength = 256;

    public const string InvalidManifestCode = "invalid-manifest";
    public const string MissingApiVersionCode = "missing-api-version";
    public const string UnsupportedApiVersionCode = "unsupported-api-version";
    public const string UnknownPropertyCode = "unknown-property";
    public const string InvalidVariantCode = "invalid-variant";
    public const string InvalidTokenCode = "invalid-token";
    public const string InvalidResourceCode = "invalid-resource";
    public const string MissingResourceCode = "missing-resource";
    public const string UnsafePathCode = "unsafe-path";
    public const string ReadFailedCode = "read-failed";
    public const string SizeLimitCode = "size-limit";

    private const string AvaloniaXmlNamespace = "https://github.com/avaloniaui";
    private const string XamlXmlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> AllowedManifestKeys = new(StringComparer.Ordinal)
    {
        "Id",
        "Name",
        "Author",
        "Version",
        "ThemeApiVersion",
        "Mode",
        "ForceLTRLayout",
        "Links",
        "Description",
        "BaseVariant",
        "Resources",
        "Tokens",
        "Colors",
        "Metrics",
        "Typography",
    };

    private static readonly HashSet<string> AllowedTokensSectionKeys = new(StringComparer.Ordinal)
    {
        "BaseVariant",
        "Colors",
        "Metrics",
        "Typography",
    };

    private static readonly HashSet<string> AllowedTypographyKeys = new(StringComparer.Ordinal)
    {
        "FontFamily",
        "WindowFontSize",
        "fontFamily",
        "windowFontSize",
    };

    /// <summary>Loads and validates a declarative theme pack from a directory on disk.</summary>
    public static ThemePackValidationResult LoadFromDirectory(
        string packDirectory,
        IRegularFileProbe regularFileProbe,
        string? expectedRoot = null,
        IThemeFileReader? fileReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packDirectory);
        ArgumentNullException.ThrowIfNull(regularFileProbe);

        if (packDirectory.Contains('\0') || !Path.IsPathFullyQualified(packDirectory))
        {
            return Fail(string.Empty, UnsafePathCode, "$", $"Theme pack directory '{packDirectory}' must be a valid absolute path.");
        }

        var normalizedDir = TrimRootPath(Path.GetFullPath(packDirectory));
        if (expectedRoot is not null)
        {
            if (expectedRoot.Contains('\0') || !Path.IsPathFullyQualified(expectedRoot))
            {
                return Fail(string.Empty, UnsafePathCode, "$", $"Themes root '{expectedRoot}' must be a valid absolute path.");
            }

            var normalizedRoot = TrimRootPath(Path.GetFullPath(expectedRoot));
            if (IsSymlinkOrReparsePoint(new DirectoryInfo(normalizedRoot)))
            {
                return Fail(string.Empty, UnsafePathCode, "$", $"Refusing symlinked themes root '{normalizedRoot}'.");
            }

            if (!AddonInstallLayout.IsWithin(normalizedRoot, normalizedDir) || HasSymlinkSegment(normalizedRoot, normalizedDir))
            {
                return Fail(string.Empty, UnsafePathCode, "$", $"Theme pack '{normalizedDir}' escapes or traverses a symlink under '{normalizedRoot}'.");
            }
        }
        else if (HasSymlinkInFullPath(normalizedDir))
        {
            return Fail(string.Empty, UnsafePathCode, "$", $"Refusing symlinked theme pack directory '{normalizedDir}'.");
        }

        if (!Directory.Exists(normalizedDir))
        {
            return Fail(string.Empty, MissingResourceCode, "$", $"Theme pack directory '{normalizedDir}' does not exist.");
        }

        var manifestPath = Path.Combine(normalizedDir, ManifestFileName);
        if (!TryReadSafeText(normalizedDir, manifestPath, MaxManifestBytes, regularFileProbe, fileReader,
                out var manifestText, out var readCode, out var readMessage))
        {
            var effectiveCode = readCode == UnsafePathCode ? UnsafePathCode
                : readCode == SizeLimitCode ? SizeLimitCode
                : readCode == ReadFailedCode ? ReadFailedCode
                : MissingResourceCode;
            return Fail(string.Empty, effectiveCode, ManifestFileName, readMessage);
        }

        var manifestResult = ParseManifest(manifestText, normalizedDir, manifestPath, ManifestFileName);
        if (!manifestResult.IsValid || manifestResult.Document is not { } baseDoc)
        {
            return manifestResult;
        }

        var themeId = baseDoc.Descriptor.Id;
        var colors = new Dictionary<string, ThemeRgbaColor>(baseDoc.Colors, StringComparer.Ordinal);
        var brushColors = new Dictionary<string, ThemeRgbaColor>(baseDoc.BrushColors, StringComparer.Ordinal);
        var gradient = baseDoc.WindowBackgroundGradient;
        var metrics = new Dictionary<string, double>(baseDoc.Metrics, StringComparer.Ordinal);
        var fontFamily = baseDoc.FontFamily;
        var variant = baseDoc.Descriptor.BaseVariant;
        var diagnostics = new List<ThemePackDiagnostic>();
        var loadedResources = new List<string>();

        if (baseDoc.Descriptor.HasExplicitResources)
        {
            foreach (var relative in baseDoc.Descriptor.ResourceFiles)
            {
                var fullResourcePath = Path.GetFullPath(Path.Combine(
                    normalizedDir,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!AddonInstallLayout.IsWithin(normalizedDir, fullResourcePath))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        UnsafePathCode,
                        $"$.Resources[{relative}]",
                        $"Declared theme resource '{relative}' escapes the theme pack directory."));
                    continue;
                }

                if (!TryReadSafeText(normalizedDir, fullResourcePath, MaxResourceFileBytes, regularFileProbe, fileReader,
                        out var resourceContent, out var resCode, out var resMessage))
                {
                    var mappedCode = resCode == UnsafePathCode ? UnsafePathCode
                        : resCode == SizeLimitCode ? SizeLimitCode
                        : resCode == ReadFailedCode ? ReadFailedCode
                        : MissingResourceCode;
                    diagnostics.Add(new ThemePackDiagnostic(themeId, mappedCode, $"$.Resources[{relative}]", resMessage));
                    continue;
                }

                LoadResourceText(
                    resourceContent,
                    relative,
                    themeId,
                    colors,
                    brushColors,
                    ref gradient,
                    metrics,
                    ref fontFamily,
                    ref variant,
                    diagnostics);
                loadedResources.Add(relative);
            }
        }
        else
        {
            foreach (var conventional in new[] { TokensFileName, AxamlFileName })
            {
                var candidatePath = Path.Combine(normalizedDir, conventional);
                if (IsPathSymlinkOrReparsePoint(candidatePath))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        UnsafePathCode,
                        conventional,
                        $"Refusing symlinked theme resource '{conventional}'."));
                    continue;
                }

                if (!File.Exists(candidatePath))
                {
                    continue;
                }

                if (!TryReadSafeText(normalizedDir, candidatePath, MaxResourceFileBytes, regularFileProbe, fileReader,
                        out var resourceContent, out var resCode, out var resMessage))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        resCode == MissingResourceCode ? InvalidResourceCode : resCode,
                        conventional,
                        resMessage));
                    continue;
                }

                LoadResourceText(
                    resourceContent,
                    conventional,
                    themeId,
                    colors,
                    brushColors,
                    ref gradient,
                    metrics,
                    ref fontFamily,
                    ref variant,
                    diagnostics);
                loadedResources.Add(conventional);
            }
        }

        if (CountTotalTokens(colors, brushColors, gradient, metrics, fontFamily) > MaxTokensPerPack)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidTokenCode,
                "$",
                $"Theme pack exceeds the maximum of {MaxTokensPerPack} style tokens."));
        }

        if (diagnostics.Count > 0)
        {
            return new ThemePackValidationResult(null, diagnostics);
        }

        var descriptor = baseDoc.Descriptor with
        {
            BaseVariant = variant,
            ResourceFiles = loadedResources,
        };
        return new ThemePackValidationResult(
            new ThemePackDocument(descriptor, colors, brushColors, gradient, metrics, fontFamily),
            []);
    }

    /// <summary>
    /// Loads a <see cref="PluginSurface.Theme"/> plugin package from either its <c>theme.yaml</c>
    /// or its validated <c>plugin.json</c> <c>entryPoints.theme</c> resource file.
    /// </summary>
    public static ThemePackValidationResult LoadFromPluginDescriptor(
        PluginDescriptor pluginDescriptor,
        IRegularFileProbe regularFileProbe,
        IThemeFileReader? fileReader = null)
    {
        ArgumentNullException.ThrowIfNull(pluginDescriptor);
        ArgumentNullException.ThrowIfNull(regularFileProbe);

        var id = pluginDescriptor.Id;
        if (string.IsNullOrWhiteSpace(pluginDescriptor.RootDirectory)
            || pluginDescriptor.RootDirectory.Contains('\0')
            || !Path.IsPathFullyQualified(pluginDescriptor.RootDirectory))
        {
            return Fail(id, UnsafePathCode, "$", $"Theme plugin '{id}' must have an absolute root directory.");
        }

        var normalizedDir = TrimRootPath(Path.GetFullPath(pluginDescriptor.RootDirectory));
        if (HasSymlinkInFullPath(normalizedDir))
        {
            return Fail(id, UnsafePathCode, "$", $"Refusing symlinked theme plugin directory '{normalizedDir}'.");
        }

        if (!Directory.Exists(normalizedDir))
        {
            return Fail(id, MissingResourceCode, "$", $"Theme plugin directory '{normalizedDir}' does not exist.");
        }

        var manifestCandidate = Path.Combine(normalizedDir, ManifestFileName);
        if (File.Exists(manifestCandidate) || IsPathSymlinkOrReparsePoint(manifestCandidate))
        {
            return LoadFromDirectory(normalizedDir, regularFileProbe, expectedRoot: null, fileReader);
        }

        var relativeEntry = pluginDescriptor.EntryPoints.Theme;
        if (!AddonPathSafety.IsSafeRelativePath(relativeEntry))
        {
            return Fail(id, UnsafePathCode, "$.entryPoints.theme",
                $"Theme plugin '{id}' entry point '{relativeEntry}' must be a safe relative path.");
        }

        var ext = Path.GetExtension(relativeEntry!);
        if (!string.Equals(ext, ".axaml", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ext, ".yaml", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ext, ".yml", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(id, InvalidResourceCode, "$.entryPoints.theme",
                $"Theme plugin '{id}' entry point '{relativeEntry}' must be .axaml, .yaml, or .yml.");
        }

        var fullEntryPath = Path.GetFullPath(Path.Combine(
            normalizedDir,
            relativeEntry!.Replace('/', Path.DirectorySeparatorChar)));
        if (!AddonInstallLayout.IsWithin(normalizedDir, fullEntryPath))
        {
            return Fail(id, UnsafePathCode, "$.entryPoints.theme",
                $"Theme plugin '{id}' entry point '{relativeEntry}' escapes plugin directory.");
        }

        if (!TryReadSafeText(normalizedDir, fullEntryPath, MaxResourceFileBytes, regularFileProbe, fileReader,
                out var content, out var readCode, out var readMessage))
        {
            return Fail(id, readCode, relativeEntry!, readMessage);
        }

        var colors = new Dictionary<string, ThemeRgbaColor>(StringComparer.Ordinal);
        var brushColors = new Dictionary<string, ThemeRgbaColor>(StringComparer.Ordinal);
        ThemeLinearGradient? gradient = null;
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        string? fontFamily = null;
        var variant = ThemeBaseVariant.Dark;
        var diagnostics = new List<ThemePackDiagnostic>();

        LoadResourceText(
            content,
            relativeEntry!,
            id,
            colors,
            brushColors,
            ref gradient,
            metrics,
            ref fontFamily,
            ref variant,
            diagnostics);

        if (diagnostics.Count > 0)
        {
            return new ThemePackValidationResult(null, diagnostics);
        }

        var descriptor = new ThemePackDescriptor(
            id,
            string.IsNullOrWhiteSpace(pluginDescriptor.Name) ? id : pluginDescriptor.Name,
            Author: null,
            pluginDescriptor.Version,
            ThemePackDefaults.SupportedApiVersion,
            ApplicationMode.Desktop,
            variant,
            ForceLtrLayout: false,
            pluginDescriptor.Description,
            IsBuiltIn: false,
            RootDirectory: normalizedDir,
            ManifestPath: null,
            ResourceFiles: [relativeEntry!],
            HasExplicitResources: true);

        return new ThemePackValidationResult(
            new ThemePackDocument(descriptor, colors, brushColors, gradient, metrics, fontFamily),
            []);
    }

    /// <summary>Parses and validates a <c>theme.yaml</c> document without reading external files.</summary>
    public static ThemePackValidationResult ParseManifest(
        string yaml,
        string rootDirectory,
        string? manifestPath = null,
        string sourceName = ManifestFileName)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        if (yaml.Contains('\0'))
        {
            return Fail(string.Empty, InvalidManifestCode, "$", $"{sourceName}: null bytes are not allowed.");
        }

        var normalized = SerializationLimits.WithoutByteOrderMark(yaml);
        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        if (byteCount > MaxManifestBytes)
        {
            return Fail(string.Empty, SizeLimitCode, "$",
                $"{sourceName}: manifest is {byteCount} bytes, which exceeds the {MaxManifestBytes}-byte limit.");
        }

        // Run the iterative YAML structural guard (depth <= 16, no anchors/aliases/tags, no duplicates)
        // BEFORE AddonManifestValidator/YamlDotNet deserialization to prevent alias-expansion DoS.
        var yamlDiagnostics = new List<ThemePackDiagnostic>();
        if (!TryLoadYamlMapping(normalized, string.Empty, sourceName, yamlDiagnostics, out var rootMap))
        {
            return new ThemePackValidationResult(null, yamlDiagnostics);
        }

        var diagnostics = new List<ThemePackDiagnostic>();
        var validation = AddonManifestValidator.ValidateTheme(normalized, sourceName);
        if (validation.Diagnostics.Count > 0)
        {
            foreach (var diag in validation.Diagnostics)
            {
                diagnostics.Add(new ThemePackDiagnostic(validation.Value?.Id ?? string.Empty, diag.Code, diag.Path, diag.Message));
            }
        }

        var manifest = validation.Value;
        var themeId = manifest?.Id ?? string.Empty;

        if (manifest is not null)
        {
            if (IsReservedOrBuiltInId(themeId))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    AddonManifestValidator.UnsafeIdCode,
                    "$.Id",
                    $"{sourceName}: theme id '{themeId}' is reserved."));
            }

            if (manifest.Author is not null
                && (manifest.Author.Any(char.IsControl) || manifest.Author.Trim().Length > MaxNameLength))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidManifestCode,
                    "$.Author",
                    $"{sourceName}: $.Author is invalid or exceeds {MaxNameLength} characters."));
            }

            if (manifest.Mode != ApplicationMode.Desktop)
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidManifestCode,
                    "$.Mode",
                    $"{sourceName}: only Mode=Desktop theme packs are supported by Desktop live switching."));
            }

            if (manifest.Links is { Count: > 16 })
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidManifestCode,
                    "$.Links",
                    $"{sourceName}: $.Links exceeds 16 entries."));
            }
        }

        var displayName = manifest is not null ? NormalizeDisplayText(manifest.Name, MaxNameLength) : null;
        if (manifest is not null && displayName is null)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidManifestCode,
                "$.Name",
                $"{sourceName}: $.Name is required and cannot contain control characters."));
        }

        Version version = new(0, 0);
        if (manifest is not null && (manifest.Version is null
            || !string.Equals(manifest.Version, manifest.Version.Trim(), StringComparison.Ordinal)
            || !Version.TryParse(manifest.Version, out version!)))
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidManifestCode,
                "$.Version",
                $"{sourceName}: $.Version '{manifest.Version}' must be a valid version."));
            version = new Version(0, 0);
        }

        Version? apiVersion = null;
        if (manifest is not null)
        {
            if (string.IsNullOrWhiteSpace(manifest.ThemeApiVersion))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    MissingApiVersionCode,
                    "$.ThemeApiVersion",
                    $"{sourceName}: $.ThemeApiVersion is required (supported: 2.x <= {ThemePackDefaults.SupportedApiVersion})."));
            }
            else if (!string.Equals(manifest.ThemeApiVersion, manifest.ThemeApiVersion.Trim(), StringComparison.Ordinal)
                || !Version.TryParse(manifest.ThemeApiVersion, out apiVersion))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidManifestCode,
                    "$.ThemeApiVersion",
                    $"{sourceName}: $.ThemeApiVersion '{manifest.ThemeApiVersion}' is not a valid version."));
            }
            else if (!ThemePackDefaults.IsSupportedApiVersion(apiVersion))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    UnsupportedApiVersionCode,
                    "$.ThemeApiVersion",
                    $"{sourceName}: ThemeApiVersion {apiVersion} is incompatible with supported API {ThemePackDefaults.SupportedApiVersion}."));
            }
        }

        foreach (var pair in rootMap!.Children)
        {
            var key = ((YamlScalarNode)pair.Key).Value!;
            if (!AllowedManifestKeys.Contains(key))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    UnknownPropertyCode,
                    $"$.{key}",
                    $"{sourceName}: unknown property '{key}'."));
            }
        }

        string? description = null;
        if (TryGetMappingValue(rootMap, "Description", out var descNode) && !IsNullScalar(descNode))
        {
            if (descNode is not YamlScalarNode descScalar || descScalar.Value is null || descScalar.Value.Any(char.IsControl)
                || descScalar.Value.Trim().Length > MaxDescriptionLength)
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidManifestCode,
                    "$.Description",
                    $"{sourceName}: $.Description must be a plain string up to {MaxDescriptionLength} characters."));
            }
            else
            {
                description = descScalar.Value.Trim();
            }
        }

        ThemeBaseVariant? explicitVariant = null;
        if (TryGetMappingValue(rootMap, "BaseVariant", out var variantNode))
        {
            ParseVariantNode(variantNode, themeId, sourceName, "$.BaseVariant", ref explicitVariant, diagnostics);
        }

        var resources = new List<string>();
        var hasExplicitResources = TryGetMappingValue(rootMap, "Resources", out var resourcesNode) && !IsNullScalar(resourcesNode);
        if (hasExplicitResources)
        {
            ParseResourcesNode(resourcesNode, themeId, sourceName, resources, diagnostics);
        }

        var colors = new Dictionary<string, ThemeRgbaColor>(StringComparer.Ordinal);
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        string? fontFamily = null;
        var seenColors = new HashSet<string>(StringComparer.Ordinal);
        var seenMetrics = new HashSet<string>(StringComparer.Ordinal);
        var seenFontFamily = false;

        if (TryGetMappingValue(rootMap, "Tokens", out var tokensNode) && !IsNullScalar(tokensNode))
        {
            if (tokensNode is YamlMappingNode tokensMap)
            {
                ParseTokenMapping(
                    tokensMap,
                    themeId,
                    sourceName,
                    "$.Tokens",
                    colors,
                    metrics,
                    ref fontFamily,
                    ref explicitVariant,
                    seenColors,
                    seenMetrics,
                    ref seenFontFamily,
                    diagnostics);
            }
            else
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    "$.Tokens",
                    $"{sourceName}: $.Tokens must be a YAML mapping."));
            }
        }

        ParseTopLevelTokenSections(
            rootMap,
            themeId,
            sourceName,
            "$",
            colors,
            metrics,
            ref fontFamily,
            seenColors,
            seenMetrics,
            ref seenFontFamily,
            diagnostics);

        if (CountTotalTokens(colors, null, null, metrics, fontFamily) > MaxTokensPerPack)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidTokenCode,
                "$",
                $"{sourceName}: theme exceeds the maximum of {MaxTokensPerPack} style tokens."));
        }

        if (diagnostics.Count > 0 || manifest is null || displayName is null || apiVersion is null)
        {
            return new ThemePackValidationResult(null, diagnostics);
        }

        var descriptor = new ThemePackDescriptor(
            themeId,
            displayName,
            manifest.Author?.Trim(),
            version,
            apiVersion,
            manifest.Mode,
            explicitVariant ?? ThemeBaseVariant.Dark,
            manifest.ForceLTRLayout,
            description,
            IsBuiltIn: false,
            RootDirectory: Path.GetFullPath(rootDirectory),
            ManifestPath: manifestPath is not null ? Path.GetFullPath(manifestPath) : null,
            ResourceFiles: resources,
            HasExplicitResources: hasExplicitResources);

        return new ThemePackValidationResult(
            new ThemePackDocument(
                descriptor,
                colors,
                new Dictionary<string, ThemeRgbaColor>(StringComparer.Ordinal),
                WindowBackgroundGradient: null,
                metrics,
                fontFamily),
            []);
    }

    /// <summary>Parses a standalone <c>tokens.yaml</c> document.</summary>
    public static ThemePackValidationResult ParseTokensYaml(
        string yaml,
        string themeId = "tokens",
        string sourceName = TokensFileName)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (yaml.Contains('\0'))
        {
            return Fail(themeId, InvalidTokenCode, "$", $"{sourceName}: null bytes are not allowed.");
        }

        var normalized = SerializationLimits.WithoutByteOrderMark(yaml);
        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        if (byteCount > MaxResourceFileBytes)
        {
            return Fail(themeId, SizeLimitCode, "$",
                $"{sourceName}: file is {byteCount} bytes, which exceeds the {MaxResourceFileBytes}-byte limit.");
        }

        var diagnostics = new List<ThemePackDiagnostic>();
        if (!TryLoadYamlMapping(normalized, themeId, sourceName, diagnostics, out var rootMap))
        {
            return new ThemePackValidationResult(null, diagnostics);
        }

        var colors = new Dictionary<string, ThemeRgbaColor>(StringComparer.Ordinal);
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        string? fontFamily = null;
        ThemeBaseVariant? baseVariant = null;
        var seenColors = new HashSet<string>(StringComparer.Ordinal);
        var seenMetrics = new HashSet<string>(StringComparer.Ordinal);
        var seenFontFamily = false;

        ParseTokenMapping(
            rootMap!,
            themeId,
            sourceName,
            "$",
            colors,
            metrics,
            ref fontFamily,
            ref baseVariant,
            seenColors,
            seenMetrics,
            ref seenFontFamily,
            diagnostics);

        if (diagnostics.Count > 0)
        {
            return new ThemePackValidationResult(null, diagnostics);
        }

        var stubDescriptor = new ThemePackDescriptor(
            themeId,
            themeId,
            null,
            new Version(1, 0),
            ThemePackDefaults.SupportedApiVersion,
            ApplicationMode.Desktop,
            baseVariant ?? ThemeBaseVariant.Dark,
            ForceLtrLayout: false,
            null,
            IsBuiltIn: false,
            null,
            null,
            [],
            HasExplicitResources: baseVariant.HasValue);
        return new ThemePackValidationResult(
            new ThemePackDocument(
                stubDescriptor,
                colors,
                new Dictionary<string, ThemeRgbaColor>(StringComparer.Ordinal),
                null,
                metrics,
                fontFamily),
            []);
    }

    /// <summary>
    /// Parses a strictly allow-listed declarative <c>.axaml</c> resource dictionary or styles file
    /// using <see cref="XmlReader"/> with DTD disabled and zero runtime XAML instantiation.
    /// </summary>
    public static ThemePackValidationResult ParseDeclarativeAxaml(
        string axaml,
        string themeId = "theme",
        IReadOnlyDictionary<string, ThemeRgbaColor>? baseColors = null,
        string sourceName = AxamlFileName)
    {
        ArgumentNullException.ThrowIfNull(axaml);

        if (axaml.Contains('\0'))
        {
            return Fail(themeId, InvalidResourceCode, "$", $"{sourceName}: null bytes are not allowed.");
        }

        var normalized = SerializationLimits.WithoutByteOrderMark(axaml);
        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        if (byteCount > MaxResourceFileBytes)
        {
            return Fail(themeId, SizeLimitCode, "$",
                $"{sourceName}: file is {byteCount} bytes, which exceeds the {MaxResourceFileBytes}-byte limit.");
        }

        XDocument document;
        try
        {
            using var stringReader = new StringReader(normalized);
            using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = MaxResourceFileBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = false,
                CloseInput = false,
            });
            document = XDocument.Load(xmlReader, LoadOptions.None);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or ArgumentException)
        {
            return Fail(themeId, InvalidResourceCode, "$", $"{sourceName}: invalid XML ({exception.Message}).");
        }

        if (document.Nodes().Any(node => node.NodeType is XmlNodeType.ProcessingInstruction or XmlNodeType.DocumentType)
            || document.DescendantNodes().Any(node => node.NodeType is XmlNodeType.ProcessingInstruction or XmlNodeType.EntityReference))
        {
            return Fail(themeId, InvalidResourceCode, "$", $"{sourceName}: processing instructions and entities are forbidden.");
        }

        var root = document.Root;
        if (root is null || root.Name.NamespaceName != AvaloniaXmlNamespace)
        {
            return Fail(themeId, InvalidResourceCode, "$",
                $"{sourceName}: root element must belong to '{AvaloniaXmlNamespace}'.");
        }

        if (MaxElementDepth(root, 1) > MaxXmlDepth)
        {
            return Fail(themeId, InvalidResourceCode, "$",
                $"{sourceName}: XML nesting exceeds maximum depth of {MaxXmlDepth}.");
        }

        var diagnostics = new List<ThemePackDiagnostic>();
        var colors = new Dictionary<string, ThemeRgbaColor>(StringComparer.Ordinal);
        var brushColors = new Dictionary<string, ThemeRgbaColor>(StringComparer.Ordinal);
        ThemeLinearGradient? gradient = null;
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        string? fontFamily = null;
        var seenResourceKeys = new HashSet<string>(StringComparer.Ordinal);
        var effectiveLookup = new Dictionary<string, ThemeRgbaColor>(
            baseColors ?? ThemePackDefaults.CharcoalColors,
            StringComparer.Ordinal);

        if (root.Name.LocalName == "ResourceDictionary")
        {
            ValidateRootAttributes(root, allowClass: false, themeId, sourceName, diagnostics);
            ParseResourceElements(
                root.Elements(),
                themeId,
                sourceName,
                "$.ResourceDictionary",
                seenResourceKeys,
                effectiveLookup,
                colors,
                brushColors,
                ref gradient,
                metrics,
                ref fontFamily,
                diagnostics);
        }
        else if (root.Name.LocalName == "Styles")
        {
            ValidateRootAttributes(root, allowClass: true, themeId, sourceName, diagnostics);
            foreach (var child in root.Elements())
            {
                if (child.Name.NamespaceName != AvaloniaXmlNamespace)
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidResourceCode,
                        $"$.Styles.{child.Name.LocalName}",
                        $"{sourceName}: unexpected namespace '{child.Name.NamespaceName}' on <{child.Name.LocalName}>."));
                    continue;
                }

                if (child.Name.LocalName == "Styles.Resources")
                {
                    if (child.HasAttributes)
                    {
                        diagnostics.Add(new ThemePackDiagnostic(
                            themeId,
                            InvalidResourceCode,
                            "$.Styles.Resources",
                            $"{sourceName}: <Styles.Resources> cannot declare attributes."));
                    }

                    ParseResourceElements(
                        child.Elements(),
                        themeId,
                        sourceName,
                        "$.Styles.Resources",
                        seenResourceKeys,
                        effectiveLookup,
                        colors,
                        brushColors,
                        ref gradient,
                        metrics,
                        ref fontFamily,
                        diagnostics);
                }
                else if (child.Name.LocalName == "Style")
                {
                    ParseStyleElement(child, themeId, sourceName, metrics, ref fontFamily, diagnostics);
                }
                else
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidResourceCode,
                        $"$.Styles.{child.Name.LocalName}",
                        $"{sourceName}: unsupported element <{child.Name.LocalName}> in declarative theme."));
                }
            }
        }
        else
        {
            return Fail(themeId, InvalidResourceCode, "$",
                $"{sourceName}: expected <ResourceDictionary> or <Styles> root element.");
        }

        if (CountTotalTokens(colors, brushColors, gradient, metrics, fontFamily) > MaxTokensPerPack)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidTokenCode,
                "$",
                $"{sourceName}: file exceeds the maximum of {MaxTokensPerPack} style tokens."));
        }

        if (diagnostics.Count > 0)
        {
            return new ThemePackValidationResult(null, diagnostics);
        }

        var descriptor = new ThemePackDescriptor(
            themeId,
            themeId,
            null,
            new Version(1, 0),
            ThemePackDefaults.SupportedApiVersion,
            ApplicationMode.Desktop,
            ThemeBaseVariant.Dark,
            ForceLtrLayout: false,
            null,
            IsBuiltIn: false,
            null,
            null,
            []);
        return new ThemePackValidationResult(
            new ThemePackDocument(descriptor, colors, brushColors, gradient, metrics, fontFamily),
            []);
    }

    private static void LoadResourceText(
        string content,
        string relativeName,
        string themeId,
        Dictionary<string, ThemeRgbaColor> colors,
        Dictionary<string, ThemeRgbaColor> brushColors,
        ref ThemeLinearGradient? gradient,
        Dictionary<string, double> metrics,
        ref string? fontFamily,
        ref ThemeBaseVariant baseVariant,
        List<ThemePackDiagnostic> diagnostics)
    {
        var extension = Path.GetExtension(relativeName);
        if (string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseTokensYaml(content, themeId, relativeName);
            if (!parsed.IsValid || parsed.Document is not { } doc)
            {
                diagnostics.AddRange(parsed.Diagnostics);
                return;
            }

            foreach (var (key, color) in doc.Colors)
            {
                colors[key] = color;
            }

            foreach (var (key, value) in doc.Metrics)
            {
                metrics[key] = value;
            }

            if (doc.FontFamily is not null)
            {
                fontFamily = doc.FontFamily;
            }

            if (doc.Descriptor.HasExplicitResources)
            {
                baseVariant = doc.Descriptor.BaseVariant;
            }

            return;
        }

        if (string.Equals(extension, ".axaml", StringComparison.OrdinalIgnoreCase))
        {
            var baseLookup = new Dictionary<string, ThemeRgbaColor>(ThemePackDefaults.CharcoalColors, StringComparer.Ordinal);
            foreach (var (key, color) in colors)
            {
                baseLookup[key] = color;
            }

            var parsed = ParseDeclarativeAxaml(content, themeId, baseLookup, relativeName);
            if (!parsed.IsValid || parsed.Document is not { } doc)
            {
                diagnostics.AddRange(parsed.Diagnostics);
                return;
            }

            foreach (var (key, color) in doc.Colors)
            {
                colors[key] = color;
            }

            foreach (var (key, brushColor) in doc.BrushColors)
            {
                brushColors[key] = brushColor;
            }

            if (doc.WindowBackgroundGradient is not null)
            {
                gradient = doc.WindowBackgroundGradient;
            }

            foreach (var (key, value) in doc.Metrics)
            {
                metrics[key] = value;
            }

            if (doc.FontFamily is not null)
            {
                fontFamily = doc.FontFamily;
            }

            return;
        }

        diagnostics.Add(new ThemePackDiagnostic(
            themeId,
            InvalidResourceCode,
            relativeName,
            $"Unsupported theme resource extension '{extension}'. Allowed: .axaml, .yaml, .yml."));
    }

    private static bool TryLoadYamlMapping(
        string yaml,
        string themeId,
        string sourceName,
        List<ThemePackDiagnostic> diagnostics,
        out YamlMappingNode? rootMap)
    {
        rootMap = null;
        try
        {
            SerializationLimits.CheckSize(FormatNames.Yaml, yaml, MaxManifestBytes);
            YamlDepthGuard.Validate(yaml);

            // Reject anchors, aliases, custom tags, and excessive nesting iteratively before building a tree.
            var parser = new Parser(new StringReader(yaml));
            var depth = 0;
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case MappingStart mappingStart:
                        depth++;
                        if (depth > MaxYamlDepth)
                        {
                            diagnostics.Add(new ThemePackDiagnostic(
                                themeId,
                                InvalidManifestCode,
                                "$",
                                $"{sourceName}: YAML nesting exceeds max depth of {MaxYamlDepth}."));
                            return false;
                        }

                        if (!mappingStart.Anchor.IsEmpty || (!mappingStart.Tag.IsEmpty && !mappingStart.Tag.Value.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal)))
                        {
                            diagnostics.Add(new ThemePackDiagnostic(
                                themeId,
                                InvalidManifestCode,
                                "$",
                                $"{sourceName}: YAML anchors and custom tags are not allowed."));
                            return false;
                        }

                        break;

                    case SequenceStart sequenceStart:
                        depth++;
                        if (depth > MaxYamlDepth)
                        {
                            diagnostics.Add(new ThemePackDiagnostic(
                                themeId,
                                InvalidManifestCode,
                                "$",
                                $"{sourceName}: YAML nesting exceeds max depth of {MaxYamlDepth}."));
                            return false;
                        }

                        if (!sequenceStart.Anchor.IsEmpty || (!sequenceStart.Tag.IsEmpty && !sequenceStart.Tag.Value.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal)))
                        {
                            diagnostics.Add(new ThemePackDiagnostic(
                                themeId,
                                InvalidManifestCode,
                                "$",
                                $"{sourceName}: YAML anchors and custom tags are not allowed."));
                            return false;
                        }

                        break;

                    case MappingEnd:
                    case SequenceEnd:
                        depth--;
                        break;

                    case Scalar scalarEvent:
                        if (!scalarEvent.Anchor.IsEmpty || (!scalarEvent.Tag.IsEmpty && !scalarEvent.Tag.Value.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal)))
                        {
                            diagnostics.Add(new ThemePackDiagnostic(
                                themeId,
                                InvalidManifestCode,
                                "$",
                                $"{sourceName}: YAML anchors and custom tags are not allowed."));
                            return false;
                        }

                        break;

                    case AnchorAlias:
                        diagnostics.Add(new ThemePackDiagnostic(
                            themeId,
                            InvalidManifestCode,
                            "$",
                            $"{sourceName}: YAML aliases are not allowed."));
                        return false;
                }
            }

            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map)
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidManifestCode,
                    "$",
                    $"{sourceName}: expected a single YAML mapping document."));
                return false;
            }

            ValidateSafeYamlNode(map, themeId, sourceName, "$", 0, diagnostics);
            if (diagnostics.Count > 0)
            {
                return false;
            }

            rootMap = map;
            return true;
        }
        catch (Exception exception) when (exception is YamlException or SerializationException)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidManifestCode,
                "$",
                $"{sourceName}: {exception.Message}"));
            return false;
        }
    }

    private static void ValidateSafeYamlNode(
        YamlNode node,
        string themeId,
        string sourceName,
        string path,
        int depth,
        List<ThemePackDiagnostic> diagnostics)
    {
        if (depth > MaxYamlDepth)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidManifestCode,
                path,
                $"{sourceName}: YAML nesting at '{path}' exceeds max depth of {MaxYamlDepth}."));
            return;
        }

        if (node.NodeType == YamlNodeType.Alias)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidManifestCode,
                path,
                $"{sourceName}: YAML aliases are not allowed."));
            return;
        }

        if (!node.Anchor.IsEmpty)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidManifestCode,
                path,
                $"{sourceName}: YAML anchors are not allowed."));
            return;
        }

        if (!node.Tag.IsEmpty && !node.Tag.Value.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal))
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidManifestCode,
                path,
                $"{sourceName}: custom YAML tag '{node.Tag}' at '{path}' is not allowed."));
            return;
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in mapping.Children)
                {
                    if (pair.Key is not YamlScalarNode keyScalar || string.IsNullOrEmpty(keyScalar.Value))
                    {
                        diagnostics.Add(new ThemePackDiagnostic(
                            themeId,
                            InvalidManifestCode,
                            path,
                            $"{sourceName}: expected non-empty scalar key at '{path}'."));
                        continue;
                    }

                    if (!seenKeys.Add(keyScalar.Value))
                    {
                        diagnostics.Add(new ThemePackDiagnostic(
                            themeId,
                            InvalidManifestCode,
                            $"{path}.{keyScalar.Value}",
                            $"{sourceName}: duplicate key '{keyScalar.Value}'."));
                        continue;
                    }

                    ValidateSafeYamlNode(pair.Value, themeId, sourceName, $"{path}.{keyScalar.Value}", depth + 1, diagnostics);
                }
                break;

            case YamlSequenceNode sequence:
                for (var i = 0; i < sequence.Children.Count; i++)
                {
                    ValidateSafeYamlNode(sequence.Children[i], themeId, sourceName, $"{path}[{i}]", depth + 1, diagnostics);
                }
                break;
        }
    }

    private static void ParseVariantNode(
        YamlNode variantNode,
        string themeId,
        string sourceName,
        string path,
        ref ThemeBaseVariant? variant,
        List<ThemePackDiagnostic> diagnostics)
    {
        if (IsNullScalar(variantNode))
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidVariantCode,
                path,
                $"{sourceName}: {path} cannot be null."));
            return;
        }

        if (variantNode is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidVariantCode,
                path,
                $"{sourceName}: {path} must be 'Dark' or 'Light'."));
            return;
        }

        var trimmed = scalar.Value.Trim();
        if (string.Equals(trimmed, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            variant = ThemeBaseVariant.Dark;
        }
        else if (string.Equals(trimmed, "Light", StringComparison.OrdinalIgnoreCase))
        {
            variant = ThemeBaseVariant.Light;
        }
        else
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidVariantCode,
                path,
                $"{sourceName}: unsupported BaseVariant '{trimmed}'. Expected 'Dark' or 'Light'."));
        }
    }

    private static void ParseResourcesNode(
        YamlNode resourcesNode,
        string themeId,
        string sourceName,
        List<string> resources,
        List<ThemePackDiagnostic> diagnostics)
    {
        if (resourcesNode is not YamlSequenceNode sequence)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidResourceCode,
                "$.Resources",
                $"{sourceName}: $.Resources must be a list of relative file paths."));
            return;
        }

        if (sequence.Children.Count > MaxResourceFiles)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidResourceCode,
                "$.Resources",
                $"{sourceName}: $.Resources exceeds {MaxResourceFiles} entries."));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sequence.Children.Count; i++)
        {
            var path = $"$.Resources[{i}]";
            if (sequence.Children[i] is not YamlScalarNode scalar || !AddonPathSafety.IsSafeRelativePath(scalar.Value))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    UnsafePathCode,
                    path,
                    $"{sourceName}: resource path at {path} must be a relative path inside the theme pack directory."));
                continue;
            }

            var relative = scalar.Value!;
            var extension = Path.GetExtension(relative);
            if (!string.Equals(extension, ".axaml", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidResourceCode,
                    path,
                    $"{sourceName}: resource '{relative}' has unsupported extension '{extension}'."));
                continue;
            }

            if (!seen.Add(relative))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidResourceCode,
                    path,
                    $"{sourceName}: duplicate resource path '{relative}'."));
                continue;
            }

            resources.Add(relative);
        }
    }

    private static void ParseTokenMapping(
        YamlMappingNode mapping,
        string themeId,
        string sourceName,
        string basePath,
        Dictionary<string, ThemeRgbaColor> colors,
        Dictionary<string, double> metrics,
        ref string? fontFamily,
        ref ThemeBaseVariant? baseVariant,
        HashSet<string> seenColors,
        HashSet<string> seenMetrics,
        ref bool seenFontFamily,
        List<ThemePackDiagnostic> diagnostics)
    {
        foreach (var pair in mapping.Children)
        {
            var key = ((YamlScalarNode)pair.Key).Value!;
            if (!AllowedTokensSectionKeys.Contains(key))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    UnknownPropertyCode,
                    $"{basePath}.{key}",
                    $"{sourceName}: unknown token section '{key}'."));
            }
        }

        if (TryGetMappingValue(mapping, "BaseVariant", out var variantNode))
        {
            ParseVariantNode(variantNode, themeId, sourceName, $"{basePath}.BaseVariant", ref baseVariant, diagnostics);
        }

        ParseTopLevelTokenSections(
            mapping,
            themeId,
            sourceName,
            basePath,
            colors,
            metrics,
            ref fontFamily,
            seenColors,
            seenMetrics,
            ref seenFontFamily,
            diagnostics);
    }

    private static void ParseTopLevelTokenSections(
        YamlMappingNode mapping,
        string themeId,
        string sourceName,
        string basePath,
        Dictionary<string, ThemeRgbaColor> colors,
        Dictionary<string, double> metrics,
        ref string? fontFamily,
        HashSet<string> seenColors,
        HashSet<string> seenMetrics,
        ref bool seenFontFamily,
        List<ThemePackDiagnostic> diagnostics)
    {
        if (TryGetMappingValue(mapping, "Colors", out var colorsNode) && !IsNullScalar(colorsNode))
        {
            if (colorsNode is YamlMappingNode colorsMap)
            {
                ParseColorsSection(colorsMap, themeId, sourceName, $"{basePath}.Colors", colors, seenColors, diagnostics);
            }
            else
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    $"{basePath}.Colors",
                    $"{sourceName}: {basePath}.Colors must be a mapping of color token names to hex values."));
            }
        }

        if (TryGetMappingValue(mapping, "Metrics", out var metricsNode) && !IsNullScalar(metricsNode))
        {
            if (metricsNode is YamlMappingNode metricsMap)
            {
                ParseMetricsSection(metricsMap, themeId, sourceName, $"{basePath}.Metrics", metrics, seenMetrics, diagnostics);
            }
            else
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    $"{basePath}.Metrics",
                    $"{sourceName}: {basePath}.Metrics must be a mapping of metric names to numbers."));
            }
        }

        if (TryGetMappingValue(mapping, "Typography", out var typoNode) && !IsNullScalar(typoNode))
        {
            if (typoNode is YamlMappingNode typoMap)
            {
                ParseTypographySection(
                    typoMap,
                    themeId,
                    sourceName,
                    $"{basePath}.Typography",
                    metrics,
                    ref fontFamily,
                    seenMetrics,
                    ref seenFontFamily,
                    diagnostics);
            }
            else
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    $"{basePath}.Typography",
                    $"{sourceName}: {basePath}.Typography must be a mapping."));
            }
        }
    }

    private static void ParseColorsSection(
        YamlMappingNode colorsMap,
        string themeId,
        string sourceName,
        string basePath,
        Dictionary<string, ThemeRgbaColor> colors,
        HashSet<string> seenCanonical,
        List<ThemePackDiagnostic> diagnostics)
    {
        foreach (var pair in colorsMap.Children)
        {
            var rawKey = ((YamlScalarNode)pair.Key).Value!;
            var tokenPath = $"{basePath}.{rawKey}";
            if (!ThemePackDefaults.TryResolveColorKey(rawKey, out var canonicalKey))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    tokenPath,
                    $"{sourceName}: unknown color token '{rawKey}'."));
                continue;
            }

            if (!seenCanonical.Add(canonicalKey))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    tokenPath,
                    $"{sourceName}: duplicate color token '{canonicalKey}' via '{rawKey}'."));
                continue;
            }

            if (pair.Value is not YamlScalarNode scalar || IsNullScalar(scalar)
                || !ThemeRgbaColor.TryParse(scalar.Value, out var parsed))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    tokenPath,
                    $"{sourceName}: invalid hex color '{(pair.Value as YamlScalarNode)?.Value}' for '{canonicalKey}'."));
                continue;
            }

            colors[canonicalKey] = parsed;
        }
    }

    private static void ParseMetricsSection(
        YamlMappingNode metricsMap,
        string themeId,
        string sourceName,
        string basePath,
        Dictionary<string, double> metrics,
        HashSet<string> seenCanonical,
        List<ThemePackDiagnostic> diagnostics)
    {
        foreach (var pair in metricsMap.Children)
        {
            var rawKey = ((YamlScalarNode)pair.Key).Value!;
            var tokenPath = $"{basePath}.{rawKey}";
            if (!ThemePackDefaults.TryResolveMetricRule(rawKey, out var rule))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    tokenPath,
                    $"{sourceName}: unknown metric token '{rawKey}'."));
                continue;
            }

            if (!seenCanonical.Add(rule.CanonicalKey))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    tokenPath,
                    $"{sourceName}: duplicate metric token '{rule.CanonicalKey}' via '{rawKey}'."));
                continue;
            }

            if (pair.Value is not YamlScalarNode scalar || IsNullScalar(scalar)
                || !TryParseBoundedDouble(scalar.Value, rule.Min, rule.Max, out var parsed))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    tokenPath,
                    $"{sourceName}: metric '{rule.CanonicalKey}' must be a finite number in [{rule.Min}, {rule.Max}]."));
                continue;
            }

            metrics[rule.CanonicalKey] = parsed;
        }
    }

    private static void ParseTypographySection(
        YamlMappingNode typoMap,
        string themeId,
        string sourceName,
        string basePath,
        Dictionary<string, double> metrics,
        ref string? fontFamily,
        HashSet<string> seenMetrics,
        ref bool seenFontFamily,
        List<ThemePackDiagnostic> diagnostics)
    {
        foreach (var pair in typoMap.Children)
        {
            var rawKey = ((YamlScalarNode)pair.Key).Value!;
            var tokenPath = $"{basePath}.{rawKey}";
            if (!AllowedTypographyKeys.Contains(rawKey))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    UnknownPropertyCode,
                    tokenPath,
                    $"{sourceName}: unknown typography property '{rawKey}'."));
                continue;
            }

            if (string.Equals(rawKey, "FontFamily", StringComparison.OrdinalIgnoreCase))
            {
                if (seenFontFamily)
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: duplicate FontFamily declaration '{rawKey}'."));
                    continue;
                }

                seenFontFamily = true;
                if (pair.Value is not YamlScalarNode scalar || IsNullScalar(scalar)
                    || !TryValidateFontFamily(scalar.Value, out var validatedFont))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: FontFamily must be a safe non-empty font list up to {MaxFontFamilyLength} characters."));
                    continue;
                }

                fontFamily = validatedFont;
            }
            else
            {
                if (!seenMetrics.Add(ThemePackDefaults.WindowFontSizeKey))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: duplicate WindowFontSize declaration '{rawKey}'."));
                    continue;
                }

                var rule = ThemePackDefaults.MetricRules[ThemePackDefaults.WindowFontSizeKey];
                if (pair.Value is not YamlScalarNode scalar || IsNullScalar(scalar)
                    || !TryParseBoundedDouble(scalar.Value, rule.Min, rule.Max, out var size))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: WindowFontSize must be in [{rule.Min}, {rule.Max}]."));
                    continue;
                }

                metrics[ThemePackDefaults.WindowFontSizeKey] = size;
            }
        }
    }

    private static void ValidateRootAttributes(
        XElement root,
        bool allowClass,
        string themeId,
        string sourceName,
        List<ThemePackDiagnostic> diagnostics)
    {
        foreach (var attr in root.Attributes())
        {
            if (attr.IsNamespaceDeclaration)
            {
                continue;
            }

            if (allowClass && attr.Name == XName.Get("Class", XamlXmlNamespace))
            {
                continue;
            }

            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidResourceCode,
                "$",
                $"{sourceName}: unsupported root attribute '{attr.Name}'."));
        }
    }

    private static void ParseResourceElements(
        IEnumerable<XElement> elements,
        string themeId,
        string sourceName,
        string basePath,
        HashSet<string> seenResourceKeys,
        Dictionary<string, ThemeRgbaColor> effectiveLookup,
        Dictionary<string, ThemeRgbaColor> colors,
        Dictionary<string, ThemeRgbaColor> brushColors,
        ref ThemeLinearGradient? gradient,
        Dictionary<string, double> metrics,
        ref string? fontFamily,
        List<ThemePackDiagnostic> diagnostics)
    {
        var keyAttrName = XName.Get("Key", XamlXmlNamespace);
        foreach (var element in elements)
        {
            var localName = element.Name.LocalName;
            var ns = element.Name.NamespaceName;
            var rawKey = (string?)element.Attribute(keyAttrName);
            var tokenPath = string.IsNullOrWhiteSpace(rawKey)
                ? $"{basePath}.{localName}"
                : $"{basePath}.{rawKey}";

            if (string.IsNullOrWhiteSpace(rawKey) || !string.Equals(rawKey, rawKey.Trim(), StringComparison.Ordinal))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidResourceCode,
                    tokenPath,
                    $"{sourceName}: resource element <{localName}> must declare a trimmed x:Key."));
                continue;
            }

            if (!seenResourceKeys.Add(rawKey))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidResourceCode,
                    tokenPath,
                    $"{sourceName}: duplicate x:Key '{rawKey}'."));
                continue;
            }

            if (ns == AvaloniaXmlNamespace && localName == "Color")
            {
                if (!HasOnlyAllowedAttributes(element, keyAttrName) || element.HasElements)
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidResourceCode,
                        tokenPath,
                        $"{sourceName}: <Color x:Key=\"{rawKey}\"> cannot declare child elements or extra attributes."));
                    continue;
                }

                if (!ThemePackDefaults.CharcoalColors.ContainsKey(rawKey))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: unknown color key '{rawKey}'."));
                    continue;
                }

                if (!ThemeRgbaColor.TryParse(element.Value, out var parsedColor))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: invalid hex color '{element.Value}' for '{rawKey}'."));
                    continue;
                }

                colors[rawKey] = parsedColor;
                effectiveLookup[rawKey] = parsedColor;
            }
            else if (ns == AvaloniaXmlNamespace && localName == "SolidColorBrush")
            {
                if (!HasOnlyAllowedAttributes(element, keyAttrName, "Color") || element.HasElements)
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidResourceCode,
                        tokenPath,
                        $"{sourceName}: <SolidColorBrush x:Key=\"{rawKey}\"> only supports x:Key and Color attributes."));
                    continue;
                }

                if (!ThemePackDefaults.BrushToDefaultColorKey.ContainsKey(rawKey))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: unknown brush key '{rawKey}'."));
                    continue;
                }

                var rawBrushColor = (string?)element.Attribute("Color");
                if (!TryResolveColorOrStaticResource(rawBrushColor, effectiveLookup, out var parsedBrushColor))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: invalid Color '{rawBrushColor}' for brush '{rawKey}'."));
                    continue;
                }

                brushColors[rawKey] = parsedBrushColor;
            }
            else if (ns == AvaloniaXmlNamespace && localName == "LinearGradientBrush")
            {
                if (!string.Equals(rawKey, ThemePackDefaults.WindowBackgroundBrushKey, StringComparison.Ordinal))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: LinearGradientBrush is only supported for '{ThemePackDefaults.WindowBackgroundBrushKey}'."));
                    continue;
                }

                if (TryParseGradientBrush(element, keyAttrName, effectiveLookup, themeId, sourceName, tokenPath, diagnostics, out var parsedGradient))
                {
                    gradient = parsedGradient;
                }
            }
            else if (ns == XamlXmlNamespace && localName == "Double")
            {
                if (!HasOnlyAllowedAttributes(element, keyAttrName) || element.HasElements)
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidResourceCode,
                        tokenPath,
                        $"{sourceName}: <x:Double x:Key=\"{rawKey}\"> cannot declare child elements or extra attributes."));
                    continue;
                }

                if (!ThemePackDefaults.MetricRules.TryGetValue(rawKey, out var rule))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: unknown numeric resource key '{rawKey}'."));
                    continue;
                }

                if (!TryParseBoundedDouble(element.Value, rule.Min, rule.Max, out var numericValue))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: resource '{rawKey}' must be in [{rule.Min}, {rule.Max}]."));
                    continue;
                }

                metrics[rule.CanonicalKey] = numericValue;
            }
            else if (ns == AvaloniaXmlNamespace && localName == "FontFamily")
            {
                if (!HasOnlyAllowedAttributes(element, keyAttrName) || element.HasElements)
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidResourceCode,
                        tokenPath,
                        $"{sourceName}: <FontFamily x:Key=\"{rawKey}\"> cannot declare child elements or extra attributes."));
                    continue;
                }

                if (!string.Equals(rawKey, ThemePackDefaults.FontFamilyResourceKey, StringComparison.Ordinal))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: FontFamily is only supported for '{ThemePackDefaults.FontFamilyResourceKey}'."));
                    continue;
                }

                if (!TryValidateFontFamily(element.Value, out var parsedFont))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        tokenPath,
                        $"{sourceName}: invalid FontFamily '{element.Value}'."));
                    continue;
                }

                fontFamily = parsedFont;
            }
            else
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidResourceCode,
                    tokenPath,
                    $"{sourceName}: unsupported resource element <{localName}>."));
            }
        }
    }

    private static bool TryParseGradientBrush(
        XElement element,
        XName keyAttrName,
        IReadOnlyDictionary<string, ThemeRgbaColor> effectiveLookup,
        string themeId,
        string sourceName,
        string tokenPath,
        List<ThemePackDiagnostic> diagnostics,
        out ThemeLinearGradient? gradient)
    {
        gradient = null;
        if (!HasOnlyAllowedAttributes(element, keyAttrName, "StartPoint", "EndPoint"))
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidResourceCode,
                tokenPath,
                $"{sourceName}: <LinearGradientBrush> only supports x:Key, StartPoint, and EndPoint."));
            return false;
        }

        var startRaw = (string?)element.Attribute("StartPoint") ?? "50%,0%";
        var endRaw = (string?)element.Attribute("EndPoint") ?? "40%,100%";
        if (!TryParsePoint(startRaw, out var sx, out var sy) || !TryParsePoint(endRaw, out var ex, out var ey))
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidTokenCode,
                tokenPath,
                $"{sourceName}: invalid StartPoint/EndPoint in LinearGradientBrush."));
            return false;
        }

        var stops = new List<ThemeGradientStop>();
        foreach (var child in element.Elements())
        {
            if (child.Name.NamespaceName != AvaloniaXmlNamespace || child.Name.LocalName != "GradientStop" || child.HasElements)
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidResourceCode,
                    tokenPath,
                    $"{sourceName}: <LinearGradientBrush> may only contain <GradientStop> elements."));
                return false;
            }

            if (!HasOnlyAllowedAttributes(child, null, "Color", "Offset"))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidResourceCode,
                    tokenPath,
                    $"{sourceName}: <GradientStop> only supports Color and Offset attributes."));
                return false;
            }

            var stopColorRaw = (string?)child.Attribute("Color");
            var stopOffsetRaw = (string?)child.Attribute("Offset");
            if (!TryResolveColorOrStaticResource(stopColorRaw, effectiveLookup, out var stopColor)
                || !TryParseBoundedDouble(stopOffsetRaw, 0.0, 1.0, out var offset))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidTokenCode,
                    tokenPath,
                    $"{sourceName}: invalid <GradientStop Color=\"{stopColorRaw}\" Offset=\"{stopOffsetRaw}\">."));
                return false;
            }

            stops.Add(new ThemeGradientStop(stopColor, offset));
        }

        if (stops.Count < 2 || stops.Count > 4)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidTokenCode,
                tokenPath,
                $"{sourceName}: LinearGradientBrush requires 2 to 4 GradientStop elements."));
            return false;
        }

        gradient = new ThemeLinearGradient(sx, sy, ex, ey, stops);
        return true;
    }

    private static void ParseStyleElement(
        XElement styleElement,
        string themeId,
        string sourceName,
        Dictionary<string, double> metrics,
        ref string? fontFamily,
        List<ThemePackDiagnostic> diagnostics)
    {
        if (!HasOnlyAllowedAttributes(styleElement, null, "Selector"))
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidResourceCode,
                "$.Styles.Style",
                $"{sourceName}: <Style> only supports the Selector attribute."));
            return;
        }

        var selector = (string?)styleElement.Attribute("Selector");
        if (!string.Equals(selector, "Window", StringComparison.Ordinal)
            && !string.Equals(selector, ":is(Window)", StringComparison.Ordinal))
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidResourceCode,
                "$.Styles.Style",
                $"{sourceName}: unsupported style selector '{selector ?? "<missing>"}'."));
            return;
        }

        var setters = styleElement.Elements().ToArray();
        if (setters.Length == 0)
        {
            diagnostics.Add(new ThemePackDiagnostic(
                themeId,
                InvalidResourceCode,
                "$.Styles.Style",
                $"{sourceName}: <Style Selector=\"{selector}\"> must contain at least one Setter."));
            return;
        }

        foreach (var setter in setters)
        {
            if (setter.Name.NamespaceName != AvaloniaXmlNamespace || setter.Name.LocalName != "Setter" || setter.HasElements)
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidResourceCode,
                    "$.Styles.Style",
                    $"{sourceName}: <Style> may only contain <Setter> elements."));
                continue;
            }

            if (!HasOnlyAllowedAttributes(setter, null, "Property", "Value"))
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidResourceCode,
                    "$.Styles.Style.Setter",
                    $"{sourceName}: <Setter> only supports Property and Value attributes."));
                continue;
            }

            var property = (string?)setter.Attribute("Property");
            var value = (string?)setter.Attribute("Value");
            if (string.Equals(property, "FontSize", StringComparison.Ordinal))
            {
                var rule = ThemePackDefaults.MetricRules[ThemePackDefaults.WindowFontSizeKey];
                if (!TryParseBoundedDouble(value, rule.Min, rule.Max, out var fontSize))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        "$.Styles.Style.FontSize",
                        $"{sourceName}: FontSize '{value ?? "<missing>"}' must be in [{rule.Min}, {rule.Max}]."));
                    continue;
                }

                metrics[ThemePackDefaults.WindowFontSizeKey] = fontSize;
            }
            else if (string.Equals(property, "FontFamily", StringComparison.Ordinal))
            {
                if (!TryValidateFontFamily(value, out var parsedFont))
                {
                    diagnostics.Add(new ThemePackDiagnostic(
                        themeId,
                        InvalidTokenCode,
                        "$.Styles.Style.FontFamily",
                        $"{sourceName}: invalid FontFamily '{value ?? "<missing>"}'."));
                    continue;
                }

                fontFamily = parsedFont;
            }
            else
            {
                diagnostics.Add(new ThemePackDiagnostic(
                    themeId,
                    InvalidResourceCode,
                    "$.Styles.Style.Setter",
                    $"{sourceName}: unsupported style property '{property ?? "<missing>"}'."));
            }
        }
    }

    private static bool TryResolveColorOrStaticResource(
        string? raw,
        IReadOnlyDictionary<string, ThemeRgbaColor> effectiveLookup,
        out ThemeRgbaColor color)
    {
        color = default;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        if (ThemeRgbaColor.TryParse(raw, out color))
        {
            return true;
        }

        const string prefix = "{StaticResource ";
        if (raw.StartsWith(prefix, StringComparison.Ordinal) && raw.EndsWith('}'))
        {
            var resourceKey = raw[prefix.Length..^1].Trim();
            return effectiveLookup.TryGetValue(resourceKey, out color);
        }

        return false;
    }

    private static bool TryParsePoint(string raw, out double x, out double y)
    {
        x = 0;
        y = 0;
        var parts = raw.Split(',');
        if (parts.Length != 2)
        {
            return false;
        }

        return TryParsePointCoord(parts[0], out x) && TryParsePointCoord(parts[1], out y);
    }

    private static bool TryParsePointCoord(string raw, out double coord)
    {
        coord = 0;
        var trimmed = raw.Trim();
        if (trimmed.EndsWith('%'))
        {
            if (!double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
                || !double.IsFinite(pct) || pct < 0 || pct > 100)
            {
                return false;
            }

            coord = pct / 100.0;
            return true;
        }

        return TryParseBoundedDouble(trimmed, 0.0, 1.0, out coord);
    }

    private static bool TryParseBoundedDouble(string? raw, double min, double max, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw) || !string.Equals(raw, raw.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        return double.IsFinite(value) && value >= min && value <= max;
    }

    private static bool TryValidateFontFamily(string? raw, out string fontFamily)
    {
        fontFamily = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxFontFamilyLength
            || trimmed.Any(char.IsControl)
            || trimmed.IndexOfAny(['{', '}', '#', '?', '%', ':', '/', '\\']) >= 0
            || trimmed.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = trimmed.Split(',');
        if (segments.Length is 0 or > 8)
        {
            return false;
        }

        var normalized = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var part = segments[i].Trim();
            if (part.Length == 0)
            {
                return false;
            }

            var unquoted = part;
            if ((part.StartsWith('\'') && part.EndsWith('\'')) || (part.StartsWith('"') && part.EndsWith('"')))
            {
                if (part.Length < 3)
                {
                    return false;
                }

                unquoted = part[1..^1].Trim();
                if (unquoted.Length == 0)
                {
                    return false;
                }
            }

            if (unquoted.IndexOfAny(['\'', '"']) >= 0)
            {
                return false;
            }

            var hasLetterOrDigit = false;
            foreach (var ch in unquoted)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    hasLetterOrDigit = true;
                    continue;
                }

                if (ch is ' ' or '-' or '_' or '.')
                {
                    continue;
                }

                return false;
            }

            if (!hasLetterOrDigit)
            {
                return false;
            }

            normalized[i] = part;
        }

        fontFamily = string.Join(", ", normalized);
        return true;
    }

    private static bool HasOnlyAllowedAttributes(XElement element, XName? requiredOrAllowedNsAttr, params string[] allowedLocalNames)
    {
        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration)
            {
                continue;
            }

            if (requiredOrAllowedNsAttr is not null && attr.Name == requiredOrAllowedNsAttr)
            {
                continue;
            }

            if (string.IsNullOrEmpty(attr.Name.NamespaceName)
                && allowedLocalNames.Contains(attr.Name.LocalName, StringComparer.Ordinal))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static int MaxElementDepth(XElement element, int currentDepth)
    {
        if (currentDepth > MaxXmlDepth)
        {
            return currentDepth;
        }

        var max = currentDepth;
        foreach (var child in element.Elements())
        {
            var childDepth = MaxElementDepth(child, currentDepth + 1);
            if (childDepth > max)
            {
                max = childDepth;
            }

            if (max > MaxXmlDepth)
            {
                return max;
            }
        }

        return max;
    }

    private static bool TryGetMappingValue(YamlMappingNode mapping, string key, out YamlNode value)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private static bool IsNullScalar(YamlNode node)
    {
        if (node is not YamlScalarNode scalar)
        {
            return false;
        }

        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
        {
            return false;
        }

        return string.IsNullOrEmpty(scalar.Value)
            || scalar.Value == "~"
            || scalar.Value.Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReservedOrBuiltInId(string id) =>
        string.Equals(id, PluginHost.HostStatePluginId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, ThemePackDefaults.DefaultDesktopThemeId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, ThemePackDefaults.DefaultFullscreenThemeId, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeDisplayText(string? rawText, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var trimmed = rawText.Trim();
        if (trimmed.Length > maxLength || trimmed.Any(char.IsControl))
        {
            return null;
        }

        return trimmed;
    }

    private static int CountTotalTokens(
        IReadOnlyDictionary<string, ThemeRgbaColor> colors,
        IReadOnlyDictionary<string, ThemeRgbaColor>? brushColors,
        ThemeLinearGradient? gradient,
        IReadOnlyDictionary<string, double> metrics,
        string? fontFamily) =>
        colors.Count
        + (brushColors?.Count ?? 0)
        + (gradient is null ? 0 : 1)
        + metrics.Count
        + (fontFamily is null ? 0 : 1);

    private static ThemePackValidationResult Fail(string themeId, string code, string path, string message) =>
        new(null, [new ThemePackDiagnostic(themeId, code, path, message)]);

    public static string TrimRootPath(string fullPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        return trimmed.Length == 0 ? Path.DirectorySeparatorChar.ToString() : trimmed;
    }

    internal static bool HasSymlinkInFullPath(string fullPath)
    {
        var normalized = TrimRootPath(Path.GetFullPath(fullPath));
        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, normalized);
        if (relative == ".")
        {
            return IsSymlinkOrReparsePoint(new DirectoryInfo(normalized));
        }

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            var isLast = i == segments.Length - 1;
            FileSystemInfo info = isLast && File.Exists(current)
                ? new FileInfo(current)
                : new DirectoryInfo(current);
            if (IsSymlinkOrReparsePoint(info))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasSymlinkSegment(string baseDirectory, string fullTargetPath)
    {
        var normalizedBase = TrimRootPath(Path.GetFullPath(baseDirectory));
        var normalizedTarget = Path.GetFullPath(fullTargetPath);
        if (!AddonInstallLayout.IsWithin(normalizedBase, normalizedTarget))
        {
            return true;
        }

        var relative = Path.GetRelativePath(normalizedBase, normalizedTarget);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = normalizedBase;

        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            var isLast = i == segments.Length - 1;
            FileSystemInfo info = isLast && File.Exists(current)
                ? new FileInfo(current)
                : new DirectoryInfo(current);
            if (IsSymlinkOrReparsePoint(info))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsPathSymlinkOrReparsePoint(string path)
    {
        var fileInfo = new FileInfo(path);
        if (IsSymlinkOrReparsePoint(fileInfo))
        {
            return true;
        }

        return IsSymlinkOrReparsePoint(new DirectoryInfo(path));
    }

    internal static bool IsSymlinkOrReparsePoint(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0);

    private static bool TryReadSafeText(
        string baseDirectory,
        string targetPath,
        int maxBytes,
        IRegularFileProbe regularFileProbe,
        IThemeFileReader? fileReader,
        out string content,
        out string errorCode,
        out string errorMessage)
    {
        if (fileReader is not null)
        {
            var read = fileReader.Read(targetPath, maxBytes);
            switch (read.Status)
            {
                case ThemeFileReadStatus.Ok:
                    content = Encoding.UTF8.GetString(read.Bytes ?? []);
                    errorCode = string.Empty;
                    errorMessage = string.Empty;
                    return true;
                case ThemeFileReadStatus.Missing:
                    content = string.Empty;
                    errorCode = MissingResourceCode;
                    errorMessage = read.ErrorMessage ?? $"Theme file '{targetPath}' is missing.";
                    return false;
                case ThemeFileReadStatus.NotRegularFile:
                    content = string.Empty;
                    errorCode = UnsafePathCode;
                    errorMessage = read.ErrorMessage ?? $"Refusing symlinked or non-regular theme file '{targetPath}'.";
                    return false;
                case ThemeFileReadStatus.TooLarge:
                    content = string.Empty;
                    errorCode = SizeLimitCode;
                    errorMessage = read.ErrorMessage ?? $"Theme file '{targetPath}' exceeds the {maxBytes}-byte limit.";
                    return false;
                default:
                    content = string.Empty;
                    errorCode = ReadFailedCode;
                    errorMessage = read.ErrorMessage ?? $"Could not read theme file '{targetPath}'.";
                    return false;
            }
        }

        if (HasSymlinkSegment(baseDirectory, targetPath))
        {
            content = string.Empty;
            errorCode = UnsafePathCode;
            errorMessage = $"Refusing symlinked theme file '{targetPath}'.";
            return false;
        }

        if (!File.Exists(targetPath) || !regularFileProbe.IsRegularFile(targetPath))
        {
            content = string.Empty;
            errorCode = MissingResourceCode;
            errorMessage = $"Theme file '{targetPath}' is missing or not a regular file.";
            return false;
        }

        return TryReadBoundedText(targetPath, maxBytes, out content, out errorCode, out errorMessage);
    }

    private static bool TryReadBoundedText(
        string path,
        int maxBytes,
        out string content,
        out string errorCode,
        out string errorMessage)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[maxBytes + 1];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead > maxBytes)
            {
                content = string.Empty;
                errorCode = SizeLimitCode;
                errorMessage = $"Theme file '{path}' exceeds the {maxBytes}-byte limit.";
                return false;
            }

            content = Encoding.UTF8.GetString(buffer, 0, totalRead);
            errorCode = string.Empty;
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            content = string.Empty;
            errorCode = ReadFailedCode;
            errorMessage = $"Could not read '{path}': {exception.Message}";
            return false;
        }
    }
}
