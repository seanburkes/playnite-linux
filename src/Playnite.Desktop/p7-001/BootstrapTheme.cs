using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace Playnite.Desktop.Bootstrap;

/// <summary>
/// What happened when the startup theme was applied. <see cref="StyleApplied"/> is true only when
/// the parsed on-disk override was added to the application's live Styles collection; a fallback
/// to the built-in Fluent theme reports false.
/// </summary>
public sealed record ThemeLoadResult(
    string Name,
    string Diagnostic,
    bool Applied,
    string Source,
    bool StyleApplied);

/// <summary>
/// Applies a functional override from an on-disk theme to the application styles. Avalonia 12's
/// StyleInclude only loads compiled XAML resources, so arbitrary theme.axaml files cannot be
/// loaded at runtime; v1 instead reads a constrained, Avalonia-shaped subset (ThemeFile) and adds
/// a real Avalonia Style with observable setters. The Fluent base theme installed by
/// App.Initialize stays active; a missing or invalid theme leaves it unchanged. Themes are an
/// app-local concern and never enter Core or the SDK UI surface.
/// </summary>
public static class BootstrapTheme
{
    public static ThemeLoadResult Apply(Application application, string? themePath, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(themePath))
        {
            const string diagnostic = "theme: built-in Fluent (Playnite.Desktop.App)";
            logger.Info(diagnostic);
            return new ThemeLoadResult("Default", diagnostic, true, "built-in", false);
        }

        var fullPath = Path.GetFullPath(themePath);
        if (!File.Exists(fullPath))
        {
            var diagnostic = $"theme: {fullPath} is missing; built-in Fluent remains active.";
            logger.Error(diagnostic);
            return new ThemeLoadResult("Default", diagnostic, false, fullPath, false);
        }

        try
        {
            var theme = ThemeFile.Read(fullPath);
            var style = new Style(selector => selector.OfType<Window>());
            style.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, theme.FontSize));
            application.Styles.Add(style);
            var name = Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? "Custom";
            var diagnostic = $"theme: loaded {name} from {fullPath} (FontSize={theme.FontSize})";
            logger.Info(diagnostic);
            return new ThemeLoadResult(name, diagnostic, true, fullPath, application.Styles.Contains(style));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or FormatException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            var diagnostic = $"theme: could not load {fullPath}: {exception.Message}; built-in Fluent remains active.";
            logger.Error(diagnostic);
            return new ThemeLoadResult("Default", diagnostic, false, fullPath, false);
        }
    }
}
