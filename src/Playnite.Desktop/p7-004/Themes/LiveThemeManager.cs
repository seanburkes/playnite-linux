using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Playnite.Core;
using Playnite.Core.Plugins;
using Playnite.Core.Themes;
using Playnite.Desktop.Theme;

namespace Playnite.Desktop.Themes;

/// <summary>Outcome of applying or resetting a declarative desktop theme pack.</summary>
public sealed record ThemeSwitchResult(
    bool Applied,
    bool FellBackToCharcoal,
    string ActiveThemeId,
    string RequestedThemeId,
    ThemePackDocument ActiveDocument,
    IReadOnlyList<ThemePackDiagnostic> Diagnostics,
    string StatusMessage);

/// <summary>Event payload raised after a live theme switch or fallback completes.</summary>
public sealed class ThemeChangedEventArgs(ThemeSwitchResult result) : EventArgs
{
    public ThemeSwitchResult Result { get; } = result;
}

/// <summary>
/// Manages declarative theme pack discovery, live Avalonia resource/style swapping,
/// active theme persistence via <see cref="PluginHost"/>, and guaranteed safe Charcoal fallback.
/// </summary>
public sealed class LiveThemeManager
{
    private readonly Application? application;
    private readonly IReadOnlyList<string> themeRoots;
    private readonly IRegularFileProbe regularFileProbe;
    private readonly PluginHost? pluginHost;
    private readonly IReadOnlyList<string> extraPackDirectories;
    private readonly IThemeFileReader? fileReader;

    private ThemePackDiscoveryResult catalog;

    public LiveThemeManager(
        Application? application,
        IReadOnlyList<string> themeRoots,
        IRegularFileProbe regularFileProbe,
        PluginHost? pluginHost = null,
        IReadOnlyList<string>? extraPackDirectories = null,
        IThemeFileReader? fileReader = null)
    {
        ArgumentNullException.ThrowIfNull(themeRoots);
        ArgumentNullException.ThrowIfNull(regularFileProbe);

        this.application = application;
        this.themeRoots = themeRoots.ToArray();
        this.regularFileProbe = regularFileProbe;
        this.pluginHost = pluginHost;
        this.extraPackDirectories = extraPackDirectories?.ToArray() ?? [];
        this.fileReader = fileReader;
        ActiveDocument = ThemePackDefaults.BuiltInCharcoal;
        catalog = new ThemePackDiscoveryResult([], []);
        RefreshCatalog();
        LastResult = new ThemeSwitchResult(
            Applied: true,
            FellBackToCharcoal: false,
            ThemePackDefaults.DefaultDesktopThemeId,
            ThemePackDefaults.DefaultDesktopThemeId,
            ThemePackDefaults.BuiltInCharcoal,
            [],
            "Default Charcoal theme is active.");
    }

    /// <summary>Raised whenever the active theme changes or falls back to Charcoal.</summary>
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    /// <summary>Gets the latest discovered theme catalog (built-in Charcoal first).</summary>
    public ThemePackDiscoveryResult Catalog => catalog;

    /// <summary>Gets the currently active validated theme document.</summary>
    public ThemePackDocument ActiveDocument { get; private set; }

    /// <summary>Gets the result of the most recent theme switch or startup resolution.</summary>
    public ThemeSwitchResult LastResult { get; private set; }

    /// <summary>Rescans all configured theme roots and theme plugin directories.</summary>
    public ThemePackDiscoveryResult RefreshCatalog()
    {
        IReadOnlyList<PluginDescriptor>? themePlugins = null;
        if (pluginHost is { IsDisposed: false })
        {
            try
            {
                themePlugins = pluginHost.GetThemeDescriptors();
            }
            catch (ObjectDisposedException exception)
            {
                Console.Error.WriteLine($"Skipping theme plugin descriptor scan after host disposal: {exception.Message}");
            }
        }

        catalog = ThemePackCatalog.Discover(
            themeRoots,
            regularFileProbe,
            extraPackDirectories,
            fileReader,
            themePlugins);
        return catalog;
    }

    /// <summary>Loads a theme document for UI preview without applying it to the application.</summary>
    internal ThemePackValidationResult LoadPreviewDocument(ThemePackCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.Equals(entry.Id, ThemePackDefaults.DefaultDesktopThemeId, StringComparison.Ordinal))
        {
            return new ThemePackValidationResult(ThemePackDefaults.BuiltInCharcoal, []);
        }

        if (!entry.IsValid)
        {
            return new ThemePackValidationResult(null, entry.Diagnostics);
        }

        return LoadFreshValidation(entry);
    }

    /// <summary>
    /// Applies the persisted theme identifier from <see cref="PluginHost"/> or falls back cleanly
    /// to Default Charcoal on missing, invalid, or corrupted packs.
    /// </summary>
    public ThemeSwitchResult ApplyPersistedOrDefault()
    {
        string? savedThemeId = null;
        if (pluginHost is { IsDisposed: false })
        {
            try
            {
                savedThemeId = pluginHost.GetActiveThemeId();
            }
            catch (ObjectDisposedException exception)
            {
                Console.Error.WriteLine($"Could not read persisted theme after host disposal: {exception.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(savedThemeId)
            || string.Equals(savedThemeId, ThemePackDefaults.DefaultDesktopThemeId, StringComparison.OrdinalIgnoreCase))
        {
            return ApplyCharcoalFallback(
                ThemePackDefaults.DefaultDesktopThemeId,
                [],
                isErrorFallback: false,
                persist: false,
                statusMessage: "Default Charcoal theme is active.");
        }

        return ApplyThemeById(savedThemeId, persist: false);
    }

    /// <summary>
    /// Re-validates the requested theme pack fresh from disk and applies it live, or safely falls
    /// back to Default Charcoal on any error without persisting the failing theme ID.
    /// </summary>
    public ThemeSwitchResult ApplyThemeById(string themeId, bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            var diag = new ThemePackDiagnostic(
                string.Empty,
                ThemePackParser.InvalidManifestCode,
                "$",
                "Theme identifier is required.");
            return ApplyCharcoalFallback(
                string.Empty,
                [diag],
                isErrorFallback: true,
                persist: false,
                statusMessage: "Invalid theme identifier; fell back to Default Charcoal.");
        }

        var trimmedId = themeId.Trim();
        if (string.Equals(trimmedId, ThemePackDefaults.DefaultDesktopThemeId, StringComparison.OrdinalIgnoreCase))
        {
            return ApplyCharcoalFallback(
                ThemePackDefaults.DefaultDesktopThemeId,
                [],
                isErrorFallback: false,
                persist: persist,
                statusMessage: "Default Charcoal theme applied.");
        }

        RefreshCatalog();
        var entry = catalog.Entries.FirstOrDefault(e => string.Equals(e.Id, trimmedId, StringComparison.Ordinal));
        if (entry is null)
        {
            var diag = new ThemePackDiagnostic(
                trimmedId,
                ThemePackParser.MissingResourceCode,
                "$",
                $"Theme '{trimmedId}' was not found in configured theme directories.");
            return ApplyCharcoalFallback(
                trimmedId,
                [diag],
                isErrorFallback: true,
                persist: false,
                statusMessage: $"Theme '{trimmedId}' was not found; fell back to Default Charcoal.");
        }

        return ApplyCatalogEntry(entry, persist);
    }

    /// <summary>
    /// Re-validates a specific <see cref="ThemePackCatalogEntry"/> (including duplicate/invalid rows)
    /// and applies it live, or falls back to Default Charcoal with that entry's diagnostics.
    /// </summary>
    public ThemeSwitchResult ApplyCatalogEntry(ThemePackCatalogEntry entry, bool persist = true)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.Equals(entry.Id, ThemePackDefaults.DefaultDesktopThemeId, StringComparison.Ordinal))
        {
            return ApplyCharcoalFallback(
                ThemePackDefaults.DefaultDesktopThemeId,
                [],
                isErrorFallback: false,
                persist: persist,
                statusMessage: "Default Charcoal theme applied.");
        }

        if (!entry.IsValid)
        {
            var diags = entry.Diagnostics.Count > 0
                ? entry.Diagnostics
                : [new ThemePackDiagnostic(entry.Id, ThemePackParser.InvalidManifestCode, "$", $"Theme '{entry.Id}' is invalid.")];
            var reason = diags[0].Message;
            return ApplyCharcoalFallback(
                entry.Id,
                diags,
                isErrorFallback: true,
                persist: false,
                statusMessage: $"Theme '{entry.DisplayName}' is invalid ({reason}); fell back to Default Charcoal.");
        }

        if (string.IsNullOrWhiteSpace(entry.RootDirectory))
        {
            var diag = new ThemePackDiagnostic(entry.Id, ThemePackParser.MissingResourceCode, "$", $"Theme '{entry.Id}' has no root directory.");
            return ApplyCharcoalFallback(
                entry.Id,
                [diag],
                isErrorFallback: true,
                persist: false,
                statusMessage: $"Theme '{entry.Id}' has no directory; fell back to Default Charcoal.");
        }

        // Re-validate fresh from disk to catch edits, deletion, or symlink replacement since discovery.
        var validation = LoadFreshValidation(entry);
        if (!validation.IsValid || validation.Document is null)
        {
            var reason = validation.Diagnostics.FirstOrDefault()?.Message ?? "validation failed";
            return ApplyCharcoalFallback(
                entry.Id,
                validation.Diagnostics,
                isErrorFallback: true,
                persist: false,
                statusMessage: $"Theme '{entry.Id}' failed validation ({reason}); fell back to Default Charcoal.");
        }

        return ApplyDocument(validation.Document, persist);
    }

    /// <summary>Resets the application live to the built-in Default Charcoal theme.</summary>
    public ThemeSwitchResult ResetToCharcoal(bool persist = true) =>
        ApplyCharcoalFallback(
            ThemePackDefaults.DefaultDesktopThemeId,
            [],
            isErrorFallback: false,
            persist: persist,
            statusMessage: "Reset to Default Charcoal theme.");

    /// <summary>Stages and atomically swaps resources and styles for a validated <see cref="ThemePackDocument"/>.</summary>
    public ThemeSwitchResult ApplyDocument(ThemePackDocument document, bool persist = false)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Descriptor.IsBuiltIn
            || string.Equals(document.Descriptor.Id, ThemePackDefaults.DefaultDesktopThemeId, StringComparison.OrdinalIgnoreCase))
        {
            return ApplyCharcoalFallback(
                ThemePackDefaults.DefaultDesktopThemeId,
                [],
                isErrorFallback: false,
                persist: persist,
                statusMessage: "Default Charcoal theme applied.");
        }

        try
        {
            if (application is not null)
            {
                var stagedDictionary = BuildResourceDictionary(document);
                var stagedStyles = BuildStylesOverlay(document);
                var targetVariant = document.Descriptor.BaseVariant == ThemeBaseVariant.Light
                    ? ThemeVariant.Light
                    : ThemeVariant.Dark;

                SwapApplicationLayers(application, stagedDictionary, stagedStyles, targetVariant);
            }

            var diagnostics = Array.Empty<ThemePackDiagnostic>();
            var statusMessage = $"Applied theme '{document.Descriptor.Name}' (v{document.Descriptor.Version}).";
            if (persist && pluginHost is { IsDisposed: false })
            {
                try
                {
                    if (!pluginHost.SetActiveThemeId(document.Descriptor.Id))
                    {
                        diagnostics =
                        [
                            new ThemePackDiagnostic(
                                document.Descriptor.Id,
                                "theme-state-persist-failed",
                                "$",
                                $"Theme '{document.Descriptor.Name}' applied live, but persisting selection failed."),
                        ];
                        statusMessage = diagnostics[0].Message;
                    }
                }
                catch (ObjectDisposedException exception)
                {
                    Console.Error.WriteLine($"Could not persist theme '{document.Descriptor.Id}': {exception.Message}");
                }
            }

            ActiveDocument = document;
            var result = new ThemeSwitchResult(
                Applied: true,
                FellBackToCharcoal: false,
                document.Descriptor.Id,
                document.Descriptor.Id,
                document,
                diagnostics,
                statusMessage);
            LastResult = result;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(result));
            return result;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Live theme apply failed for '{document.Descriptor.Id}': {exception.Message}");
            var diag = new ThemePackDiagnostic(
                document.Descriptor.Id,
                ThemePackParser.InvalidResourceCode,
                "$",
                $"Failed to apply theme '{document.Descriptor.Id}' to Avalonia: {exception.Message}");
            return ApplyCharcoalFallback(
                document.Descriptor.Id,
                [diag],
                isErrorFallback: true,
                persist: false,
                statusMessage: $"Could not apply '{document.Descriptor.Name}': {exception.Message}; fell back to Default Charcoal.");
        }
    }

    private ThemePackValidationResult LoadFreshValidation(ThemePackCatalogEntry entry)
    {
        var rootDir = ThemePackParser.TrimRootPath(Path.GetFullPath(entry.RootDirectory!));
        var manifestCandidate = Path.Combine(rootDir, ThemePackParser.ManifestFileName);
        if (!File.Exists(manifestCandidate) && !ThemePackParser.IsPathSymlinkOrReparsePoint(manifestCandidate)
            && pluginHost is { IsDisposed: false })
        {
            try
            {
                var matchingPlugin = pluginHost.GetThemeDescriptors().FirstOrDefault(d =>
                    string.Equals(d.Id, entry.Id, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(d.RootDirectory)
                    && string.Equals(ThemePackParser.TrimRootPath(Path.GetFullPath(d.RootDirectory!)), rootDir, StringComparison.Ordinal));
                if (matchingPlugin is not null)
                {
                    return ThemePackParser.LoadFromPluginDescriptor(matchingPlugin, regularFileProbe, fileReader);
                }
            }
            catch (ObjectDisposedException exception)
            {
                Console.Error.WriteLine($"Skipping plugin-backed theme load after disposal: {exception.Message}");
            }
        }

        return ThemePackParser.LoadFromDirectory(
            rootDir,
            regularFileProbe,
            FindContainingRoot(rootDir),
            fileReader);
    }

    private ThemeSwitchResult ApplyCharcoalFallback(
        string requestedId,
        IReadOnlyList<ThemePackDiagnostic> diagnostics,
        bool isErrorFallback,
        bool persist,
        string statusMessage)
    {
        if (application is not null)
        {
            var hasDarkTheme = HasBuiltInPlayniteDarkTheme(application);
            var fallbackDict = hasDarkTheme ? null : BuildResourceDictionary(ThemePackDefaults.BuiltInCharcoal);
            var fallbackStyles = hasDarkTheme ? null : BuildCharcoalWindowStylesFallback();
            SwapApplicationLayers(application, fallbackDict, fallbackStyles, ThemeVariant.Dark);
        }

        var finalDiagnostics = diagnostics.ToList();
        var finalMessage = statusMessage;
        if (!isErrorFallback && persist && pluginHost is { IsDisposed: false })
        {
            try
            {
                if (!pluginHost.SetActiveThemeId(null))
                {
                    var persistDiag = new ThemePackDiagnostic(
                        ThemePackDefaults.DefaultDesktopThemeId,
                        "theme-state-persist-failed",
                        "$",
                        "Reset to Default Charcoal live, but clearing persisted theme selection failed.");
                    finalDiagnostics.Add(persistDiag);
                    finalMessage = persistDiag.Message;
                }
            }
            catch (ObjectDisposedException exception)
            {
                Console.Error.WriteLine($"Could not clear persisted theme: {exception.Message}");
            }
        }

        ActiveDocument = ThemePackDefaults.BuiltInCharcoal;
        var result = new ThemeSwitchResult(
            Applied: !isErrorFallback,
            FellBackToCharcoal: isErrorFallback,
            ThemePackDefaults.DefaultDesktopThemeId,
            requestedId,
            ThemePackDefaults.BuiltInCharcoal,
            finalDiagnostics,
            finalMessage);
        LastResult = result;
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(result));
        return result;
    }

    private static void SwapApplicationLayers(
        Application app,
        ThemePackResourceDictionary? stagedDictionary,
        ThemePackStyles? stagedStyles,
        ThemeVariant targetVariant)
    {
        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (app.Resources.MergedDictionaries[i] is ThemePackResourceDictionary)
            {
                app.Resources.MergedDictionaries.RemoveAt(i);
            }
        }

        for (var i = app.Styles.Count - 1; i >= 0; i--)
        {
            if (app.Styles[i] is ThemePackStyles)
            {
                app.Styles.RemoveAt(i);
            }
        }

        if (stagedDictionary is not null)
        {
            app.Resources.MergedDictionaries.Add(stagedDictionary);
        }

        if (stagedStyles is not null)
        {
            app.Styles.Add(stagedStyles);
        }

        app.RequestedThemeVariant = targetVariant;
    }

    private static bool HasBuiltInPlayniteDarkTheme(Application app)
    {
        foreach (var style in app.Styles)
        {
            if (style is PlayniteDarkTheme)
            {
                return true;
            }
        }

        return false;
    }

    private static ThemePackResourceDictionary BuildResourceDictionary(ThemePackDocument document)
    {
        var dict = new ThemePackResourceDictionary();
        var effectiveColors = ThemePackDefaults.ResolveEffectiveColors(document);

        foreach (var (colorKey, rgba) in effectiveColors)
        {
            dict[colorKey] = ToAvaloniaColor(rgba);
        }

        foreach (var (brushKey, defaultColorKey) in ThemePackDefaults.BrushToDefaultColorKey)
        {
            var rgba = document.BrushColors.TryGetValue(brushKey, out var explicitColor)
                ? explicitColor
                : effectiveColors[defaultColorKey];
            dict[brushKey] = new SolidColorBrush(ToAvaloniaColor(rgba));
        }

        var gradient = ThemePackDefaults.ResolveWindowBackgroundGradient(document, effectiveColors);
        var gradientStops = new GradientStops();
        foreach (var stop in gradient.Stops)
        {
            gradientStops.Add(new GradientStop(ToAvaloniaColor(stop.Color), stop.Offset));
        }

        dict[ThemePackDefaults.WindowBackgroundBrushKey] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(gradient.StartX, gradient.StartY, RelativeUnit.Relative),
            EndPoint = new RelativePoint(gradient.EndX, gradient.EndY, RelativeUnit.Relative),
            GradientStops = gradientStops,
        };

        foreach (var (metricKey, rule) in ThemePackDefaults.MetricRules)
        {
            var value = document.Metrics.TryGetValue(metricKey, out var configured)
                ? configured
                : rule.DefaultValue;
            dict[metricKey] = metricKey is ThemePackDefaults.CardCornerRadiusKey or ThemePackDefaults.ControlCornerRadiusKey
                ? new CornerRadius(value)
                : value;
        }

        var fontFamilyText = string.IsNullOrWhiteSpace(document.FontFamily)
            ? ThemePackDefaults.DefaultFontFamily
            : document.FontFamily;
        dict[ThemePackDefaults.FontFamilyResourceKey] = FontFamily.Parse(fontFamilyText);

        return dict;
    }

    private static ThemePackStyles? BuildStylesOverlay(ThemePackDocument document)
    {
        var hasFontSize = document.Metrics.TryGetValue(ThemePackDefaults.WindowFontSizeKey, out var fontSize);
        var hasFontFamily = !string.IsNullOrWhiteSpace(document.FontFamily);
        if (!hasFontSize && !hasFontFamily)
        {
            return null;
        }

        var styles = new ThemePackStyles();
        var windowStyle = new Style(selector => selector.Is<Window>());
        if (hasFontSize)
        {
            windowStyle.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, fontSize));
        }

        if (hasFontFamily)
        {
            windowStyle.Setters.Add(new Setter(
                TemplatedControl.FontFamilyProperty,
                FontFamily.Parse(document.FontFamily!)));
        }

        styles.Add(windowStyle);
        return styles;
    }

    private static ThemePackStyles BuildCharcoalWindowStylesFallback()
    {
        var styles = new ThemePackStyles();
        var windowStyle = new Style(selector => selector.Is<Window>());
        windowStyle.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension(ThemePackDefaults.WindowBackgroundBrushKey)));
        windowStyle.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("PlayniteTextPrimaryBrush")));
        windowStyle.Setters.Add(new Setter(
            TemplatedControl.FontSizeProperty,
            ThemePackDefaults.MetricRules[ThemePackDefaults.WindowFontSizeKey].DefaultValue));
        windowStyle.Setters.Add(new Setter(
            TemplatedControl.FontFamilyProperty,
            FontFamily.Parse(ThemePackDefaults.DefaultFontFamily)));
        styles.Add(windowStyle);
        return styles;
    }

    private string? FindContainingRoot(string packDirectory)
    {
        var fullPack = ThemePackParser.TrimRootPath(Path.GetFullPath(packDirectory));
        foreach (var root in themeRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            {
                continue;
            }

            var fullRoot = ThemePackParser.TrimRootPath(Path.GetFullPath(root));
            var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            if (fullPack.StartsWith(prefix, StringComparison.Ordinal))
            {
                return fullRoot;
            }
        }

        return null;
    }

    private static Color ToAvaloniaColor(ThemeRgbaColor rgba) =>
        Color.FromArgb(rgba.A, rgba.R, rgba.G, rgba.B);

    private sealed class ThemePackResourceDictionary : ResourceDictionary;

    private sealed class ThemePackStyles : Styles;
}
