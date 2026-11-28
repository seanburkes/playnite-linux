using System.Globalization;
using Playnite.Core.Manifests;

namespace Playnite.Core.Themes;

/// <summary>Base Avalonia theme variant requested by a declarative theme pack.</summary>
public enum ThemeBaseVariant
{
    Dark = 0,
    Light = 1,
}

/// <summary>Immutable RGBA color parsed from a validated hex color token.</summary>
public readonly record struct ThemeRgbaColor(byte A, byte R, byte G, byte B)
{
    /// <summary>Formats the color as uppercase <c>#AARRGGBB</c>.</summary>
    public string ToHexString() =>
        string.Create(CultureInfo.InvariantCulture, $"#{A:X2}{R:X2}{G:X2}{B:X2}");

    /// <summary>
    /// Parses <c>#RGB</c>, <c>#ARGB</c>, <c>#RRGGBB</c>, or <c>#AARRGGBB</c> without whitespace or named colors.
    /// </summary>
    public static bool TryParse(string? text, out ThemeRgbaColor color)
    {
        color = default;
        if (string.IsNullOrEmpty(text) || text[0] != '#')
        {
            return false;
        }

        var span = text.AsSpan(1);
        switch (span.Length)
        {
            case 3:
                if (TryHexNibble(span[0], out var r3)
                    && TryHexNibble(span[1], out var g3)
                    && TryHexNibble(span[2], out var b3))
                {
                    color = new ThemeRgbaColor(255, (byte)(r3 * 17), (byte)(g3 * 17), (byte)(b3 * 17));
                    return true;
                }
                return false;
            case 4:
                if (TryHexNibble(span[0], out var a4)
                    && TryHexNibble(span[1], out var r4)
                    && TryHexNibble(span[2], out var g4)
                    && TryHexNibble(span[3], out var b4))
                {
                    color = new ThemeRgbaColor((byte)(a4 * 17), (byte)(r4 * 17), (byte)(g4 * 17), (byte)(b4 * 17));
                    return true;
                }
                return false;
            case 6:
                if (TryHexByte(span[..2], out var r6)
                    && TryHexByte(span[2..4], out var g6)
                    && TryHexByte(span[4..6], out var b6))
                {
                    color = new ThemeRgbaColor(255, r6, g6, b6);
                    return true;
                }
                return false;
            case 8:
                if (TryHexByte(span[..2], out var a8)
                    && TryHexByte(span[2..4], out var r8)
                    && TryHexByte(span[4..6], out var g8)
                    && TryHexByte(span[6..8], out var b8))
                {
                    color = new ThemeRgbaColor(a8, r8, g8, b8);
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool TryHexNibble(char c, out byte value)
    {
        switch (c)
        {
            case >= '0' and <= '9':
                value = (byte)(c - '0');
                return true;
            case >= 'a' and <= 'f':
                value = (byte)(10 + c - 'a');
                return true;
            case >= 'A' and <= 'F':
                value = (byte)(10 + c - 'A');
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool TryHexByte(ReadOnlySpan<char> pair, out byte value)
    {
        if (TryHexNibble(pair[0], out var hi) && TryHexNibble(pair[1], out var lo))
        {
            value = (byte)((hi << 4) | lo);
            return true;
        }

        value = 0;
        return false;
    }
}

/// <summary>One stop inside a declarative linear gradient brush.</summary>
public sealed record ThemeGradientStop(ThemeRgbaColor Color, double Offset);

/// <summary>Declarative linear gradient token for <c>PlayniteWindowBackgroundBrush</c>.</summary>
public sealed record ThemeLinearGradient(
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    IReadOnlyList<ThemeGradientStop> Stops);

/// <summary>Outcome of reading a theme pack file through an <see cref="IThemeFileReader"/>.</summary>
public enum ThemeFileReadStatus
{
    Ok = 0,
    Missing = 1,
    NotRegularFile = 2,
    TooLarge = 3,
    ReadFailed = 4,
}

/// <summary>Result of a bounded, symlink-safe read of a single theme pack file.</summary>
public sealed record ThemeFileReadResult(
    ThemeFileReadStatus Status,
    byte[]? Bytes,
    string? ErrorMessage = null);

/// <summary>Handle-bound file reader abstraction used by Core theme loading.</summary>
public interface IThemeFileReader
{
    ThemeFileReadResult Read(string path, int maxBytes);
}

/// <summary>Validation or loading diagnostic produced while reading a theme pack.</summary>
public sealed record ThemePackDiagnostic(string ThemeId, string Code, string Path, string Message);

/// <summary>Validated metadata from <c>theme.yaml</c>.</summary>
public sealed record ThemePackDescriptor(
    string Id,
    string Name,
    string? Author,
    Version Version,
    Version ThemeApiVersion,
    ApplicationMode Mode,
    ThemeBaseVariant BaseVariant,
    bool ForceLtrLayout,
    string? Description,
    bool IsBuiltIn,
    string? RootDirectory,
    string? ManifestPath,
    IReadOnlyList<string> ResourceFiles,
    bool HasExplicitResources = false);

/// <summary>Complete validated declarative theme pack ready for live Avalonia application.</summary>
public sealed record ThemePackDocument(
    ThemePackDescriptor Descriptor,
    IReadOnlyDictionary<string, ThemeRgbaColor> Colors,
    IReadOnlyDictionary<string, ThemeRgbaColor> BrushColors,
    ThemeLinearGradient? WindowBackgroundGradient,
    IReadOnlyDictionary<string, double> Metrics,
    string? FontFamily);

/// <summary>Result of validating one theme pack directory or manifest.</summary>
public sealed record ThemePackValidationResult(
    ThemePackDocument? Document,
    IReadOnlyList<ThemePackDiagnostic> Diagnostics)
{
    /// <summary>Gets whether the theme pack validated cleanly without diagnostics.</summary>
    public bool IsValid => Document is not null && Diagnostics.Count == 0;
}

/// <summary>Catalog entry for an available or invalid discovered theme pack.</summary>
public sealed record ThemePackCatalogEntry(
    string Id,
    string DisplayName,
    ThemePackDescriptor? Descriptor,
    bool IsValid,
    string? RootDirectory,
    IReadOnlyList<ThemePackDiagnostic> Diagnostics);

/// <summary>Result of scanning theme roots for declarative theme packs.</summary>
public sealed record ThemePackDiscoveryResult(
    IReadOnlyList<ThemePackCatalogEntry> Entries,
    IReadOnlyList<ThemePackDiagnostic> Diagnostics);

/// <summary>Bounds for a single declarative numeric layout/typography metric.</summary>
public readonly record struct ThemeMetricRule(string CanonicalKey, double Min, double Max, double DefaultValue);

/// <summary>Canonical Charcoal theme palette, resource keys, and safe validation bounds.</summary>
public static class ThemePackDefaults
{
    public const string DefaultDesktopThemeId = "Playnite_builtin_DefaultDesktop";
    public const string DefaultFullscreenThemeId = "Playnite_builtin_DefaultFullscreen";
    public const string DefaultThemeName = "Default Charcoal";
    public const string DefaultFontFamily = "Inter, Noto Sans, Liberation Sans, sans-serif";
    public const string WindowBackgroundBrushKey = "PlayniteWindowBackgroundBrush";
    public const string FontFamilyResourceKey = "PlayniteFontFamily";
    public const string WindowFontSizeKey = "WindowFontSize";
    public const string LibraryCardWidthKey = "LibraryCardWidth";
    public const string LibraryCardHeightKey = "LibraryCardHeight";
    public const string LibraryDetailsWidthKey = "LibraryDetailsWidth";
    public const string CardCornerRadiusKey = "CardCornerRadius";
    public const string ControlCornerRadiusKey = "ControlCornerRadius";

    public static Version SupportedApiVersion { get; } = new(2, 10, 0);

    public static bool IsSupportedApiVersion(Version? version)
    {
        if (version is null || version.Major != SupportedApiVersion.Major)
        {
            return false;
        }

        var minor = Math.Max(version.Minor, 0);
        var build = Math.Max(version.Build, 0);
        var revision = Math.Max(version.Revision, 0);
        if (minor != SupportedApiVersion.Minor)
        {
            return minor < SupportedApiVersion.Minor;
        }

        return build == 0 && revision == 0;
    }

    public static IReadOnlyDictionary<string, ThemeRgbaColor> CharcoalColors { get; } =
        new Dictionary<string, ThemeRgbaColor>(StringComparer.Ordinal)
        {
            ["PlayniteBlackColor"] = new(0xFF, 0x00, 0x00, 0x00),
            ["PlayniteWhiteColor"] = new(0xFF, 0xFF, 0xFF, 0xFF),
            ["PlayniteTextPrimaryColor"] = new(0xFF, 0xF5, 0xF5, 0xF5),
            ["PlayniteTextSecondaryColor"] = new(0xFF, 0xC4, 0xC4, 0xC4),
            ["PlayniteTextMutedColor"] = new(0xFF, 0xA6, 0xA6, 0xA6),
            ["PlayniteTextOnAccentColor"] = new(0xFF, 0x0A, 0x0A, 0x0A),
            ["PlayniteCanvasTopColor"] = new(0xFF, 0x1C, 0x1C, 0x1C),
            ["PlayniteCanvasBottomColor"] = new(0xFF, 0x09, 0x09, 0x09),
            ["PlayniteChromeColor"] = new(0xFF, 0x14, 0x14, 0x14),
            ["PlayniteSurfaceColor"] = new(0xFF, 0x21, 0x21, 0x21),
            ["PlayniteRailColor"] = new(0xFF, 0x10, 0x10, 0x10),
            ["PlayniteCardBackgroundColor"] = new(0xFF, 0x19, 0x19, 0x19),
            ["PlayniteInputBackgroundColor"] = new(0xFF, 0x0A, 0x0A, 0x0A),
            ["PlayniteRaisedHoverColor"] = new(0xFF, 0x2C, 0x2C, 0x2C),
            ["PlayniteAccentColor"] = new(0xFF, 0x55, 0xCD, 0xFF),
            ["PlayniteAccentHoverColor"] = new(0xFF, 0x7A, 0xD8, 0xFF),
            ["PlayniteAccentPressedColor"] = new(0xFF, 0x2A, 0xA7, 0xDC),
            ["PlayniteAccentMutedColor"] = new(0xFF, 0x18, 0x30, 0x3A),
            ["PlayniteHoverColor"] = new(0xFF, 0x24, 0x7B, 0xA0),
            ["PlayniteBorderColor"] = new(0xFF, 0x39, 0x39, 0x39),
            ["PlayniteControlBorderColor"] = new(0xFF, 0x77, 0x77, 0x77),
            ["PlayniteHighlightBorderColor"] = new(0x55, 0x55, 0xCD, 0xFF),
            ["PlaynitePositiveColor"] = new(0xFF, 0x78, 0xFF, 0xA0),
            ["PlayniteMixedColor"] = new(0xFF, 0xFF, 0xFC, 0xA1),
            ["PlayniteDangerColor"] = new(0xFF, 0xFF, 0x6B, 0x6B),
        };

    public static IReadOnlyDictionary<string, string> ColorToBrushMap { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PlayniteTextPrimaryColor"] = "PlayniteTextPrimaryBrush",
            ["PlayniteTextSecondaryColor"] = "PlayniteTextSecondaryBrush",
            ["PlayniteTextMutedColor"] = "PlayniteTextMutedBrush",
            ["PlayniteTextOnAccentColor"] = "PlayniteTextOnAccentBrush",
            ["PlayniteCanvasBottomColor"] = "PlayniteCanvasBrush",
            ["PlayniteChromeColor"] = "PlayniteChromeBrush",
            ["PlayniteSurfaceColor"] = "PlayniteSurfaceBrush",
            ["PlayniteRailColor"] = "PlayniteRailBrush",
            ["PlayniteCardBackgroundColor"] = "PlayniteCardBackgroundBrush",
            ["PlayniteInputBackgroundColor"] = "PlayniteInputBackgroundBrush",
            ["PlayniteRaisedHoverColor"] = "PlayniteRaisedHoverBrush",
            ["PlayniteAccentColor"] = "PlayniteAccentBrush",
            ["PlayniteAccentHoverColor"] = "PlayniteAccentHoverBrush",
            ["PlayniteAccentPressedColor"] = "PlayniteAccentPressedBrush",
            ["PlayniteAccentMutedColor"] = "PlayniteAccentMutedBrush",
            ["PlayniteBorderColor"] = "PlayniteBorderBrush",
            ["PlayniteControlBorderColor"] = "PlayniteControlBorderBrush",
            ["PlayniteHighlightBorderColor"] = "PlayniteHighlightBorderBrush",
            ["PlaynitePositiveColor"] = "PlaynitePositiveBrush",
            ["PlayniteMixedColor"] = "PlayniteMixedBrush",
            ["PlayniteDangerColor"] = "PlayniteDangerBrush",
        };

    public static IReadOnlyDictionary<string, string> BrushToDefaultColorKey { get; } =
        BuildBrushToDefaultColorKey();

    public static IReadOnlyDictionary<string, string> ColorAliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["black"] = "PlayniteBlackColor",
            ["white"] = "PlayniteWhiteColor",
            ["textPrimary"] = "PlayniteTextPrimaryColor",
            ["textSecondary"] = "PlayniteTextSecondaryColor",
            ["textMuted"] = "PlayniteTextMutedColor",
            ["textOnAccent"] = "PlayniteTextOnAccentColor",
            ["canvasTop"] = "PlayniteCanvasTopColor",
            ["canvasBottom"] = "PlayniteCanvasBottomColor",
            ["chrome"] = "PlayniteChromeColor",
            ["surface"] = "PlayniteSurfaceColor",
            ["rail"] = "PlayniteRailColor",
            ["cardBackground"] = "PlayniteCardBackgroundColor",
            ["inputBackground"] = "PlayniteInputBackgroundColor",
            ["raisedHover"] = "PlayniteRaisedHoverColor",
            ["accent"] = "PlayniteAccentColor",
            ["accentHover"] = "PlayniteAccentHoverColor",
            ["accentPressed"] = "PlayniteAccentPressedColor",
            ["accentMuted"] = "PlayniteAccentMutedColor",
            ["hover"] = "PlayniteHoverColor",
            ["border"] = "PlayniteBorderColor",
            ["controlBorder"] = "PlayniteControlBorderColor",
            ["highlightBorder"] = "PlayniteHighlightBorderColor",
            ["positive"] = "PlaynitePositiveColor",
            ["mixed"] = "PlayniteMixedColor",
            ["danger"] = "PlayniteDangerColor",
        };

    public static IReadOnlyDictionary<string, ThemeMetricRule> MetricRules { get; } =
        new Dictionary<string, ThemeMetricRule>(StringComparer.Ordinal)
        {
            [WindowFontSizeKey] = new(WindowFontSizeKey, 8.0, 48.0, 13.0),
            [LibraryCardWidthKey] = new(LibraryCardWidthKey, 120.0, 480.0, 220.0),
            [LibraryCardHeightKey] = new(LibraryCardHeightKey, 80.0, 360.0, 132.0),
            [LibraryDetailsWidthKey] = new(LibraryDetailsWidthKey, 220.0, 640.0, 320.0),
            [CardCornerRadiusKey] = new(CardCornerRadiusKey, 0.0, 24.0, 6.0),
            [ControlCornerRadiusKey] = new(ControlCornerRadiusKey, 0.0, 16.0, 3.0),
        };

    public static IReadOnlyDictionary<string, string> MetricAliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fontSize"] = WindowFontSizeKey,
            ["cardWidth"] = LibraryCardWidthKey,
            ["cardHeight"] = LibraryCardHeightKey,
            ["detailsWidth"] = LibraryDetailsWidthKey,
            ["cardCornerRadius"] = CardCornerRadiusKey,
            ["controlCornerRadius"] = ControlCornerRadiusKey,
        };

    public static ThemePackDocument BuiltInCharcoal { get; } = CreateBuiltInCharcoal();

    /// <summary>Resolves a canonical color token name or semantic alias.</summary>
    public static bool TryResolveColorKey(string? rawKey, out string canonicalKey)
    {
        canonicalKey = string.Empty;
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return false;
        }

        var trimmed = rawKey.Trim();
        if (CharcoalColors.ContainsKey(trimmed))
        {
            canonicalKey = trimmed;
            return true;
        }

        return ColorAliases.TryGetValue(trimmed, out canonicalKey!);
    }

    /// <summary>Resolves a canonical metric token name or semantic alias.</summary>
    public static bool TryResolveMetricRule(string? rawKey, out ThemeMetricRule rule)
    {
        rule = default;
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return false;
        }

        var trimmed = rawKey.Trim();
        if (MetricRules.TryGetValue(trimmed, out rule))
        {
            return true;
        }

        return MetricAliases.TryGetValue(trimmed, out var canonical)
            && MetricRules.TryGetValue(canonical, out rule);
    }

    /// <summary>Returns the full 25-color effective palette with Charcoal defaults underneath pack overrides.</summary>
    public static IReadOnlyDictionary<string, ThemeRgbaColor> ResolveEffectiveColors(ThemePackDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var resolved = new Dictionary<string, ThemeRgbaColor>(CharcoalColors, StringComparer.Ordinal);
        foreach (var (key, color) in document.Colors)
        {
            resolved[key] = color;
        }

        return resolved;
    }

    /// <summary>Builds the default <c>PlayniteWindowBackgroundBrush</c> linear gradient from resolved colors.</summary>
    public static ThemeLinearGradient ResolveWindowBackgroundGradient(
        ThemePackDocument document,
        IReadOnlyDictionary<string, ThemeRgbaColor> effectiveColors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(effectiveColors);
        if (document.WindowBackgroundGradient is { } explicitGradient)
        {
            return explicitGradient;
        }

        var top = effectiveColors.TryGetValue("PlayniteCanvasTopColor", out var cTop)
            ? cTop
            : CharcoalColors["PlayniteCanvasTopColor"];
        var bottom = effectiveColors.TryGetValue("PlayniteCanvasBottomColor", out var cBottom)
            ? cBottom
            : CharcoalColors["PlayniteCanvasBottomColor"];
        return new ThemeLinearGradient(
            0.5,
            0.0,
            0.4,
            1.0,
            [new ThemeGradientStop(top, 0.0), new ThemeGradientStop(bottom, 0.45)]);
    }

    private static IReadOnlyDictionary<string, string> BuildBrushToDefaultColorKey()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (colorKey, brushKey) in ColorToBrushMap)
        {
            map[brushKey] = colorKey;
        }

        map["TextControlBackground"] = "PlayniteInputBackgroundColor";
        map["TextControlBackgroundPointerOver"] = "PlayniteChromeColor";
        map["TextControlBackgroundFocused"] = "PlayniteInputBackgroundColor";
        map["TextControlBorderBrush"] = "PlayniteControlBorderColor";
        map["TextControlBorderBrushPointerOver"] = "PlayniteAccentColor";
        map["TextControlBorderBrushFocused"] = "PlayniteAccentColor";
        map["TextControlForeground"] = "PlayniteTextPrimaryColor";
        map["TextControlPlaceholderForeground"] = "PlayniteTextMutedColor";
        return map;
    }

    private static ThemePackDocument CreateBuiltInCharcoal()
    {
        var descriptor = new ThemePackDescriptor(
            DefaultDesktopThemeId,
            DefaultThemeName,
            "Playnite",
            new Version(1, 0, 0),
            SupportedApiVersion,
            ApplicationMode.Desktop,
            ThemeBaseVariant.Dark,
            ForceLtrLayout: false,
            "Built-in Playnite Charcoal dark desktop theme.",
            IsBuiltIn: true,
            RootDirectory: null,
            ManifestPath: null,
            ResourceFiles: []);

        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, rule) in MetricRules)
        {
            metrics[key] = rule.DefaultValue;
        }

        return new ThemePackDocument(
            descriptor,
            new Dictionary<string, ThemeRgbaColor>(CharcoalColors, StringComparer.Ordinal),
            new Dictionary<string, ThemeRgbaColor>(StringComparer.Ordinal),
            new ThemeLinearGradient(
                0.5,
                0.0,
                0.4,
                1.0,
                [
                    new ThemeGradientStop(CharcoalColors["PlayniteCanvasTopColor"], 0.0),
                    new ThemeGradientStop(CharcoalColors["PlayniteCanvasBottomColor"], 0.45),
                ]),
            metrics,
            DefaultFontFamily);
    }
}
