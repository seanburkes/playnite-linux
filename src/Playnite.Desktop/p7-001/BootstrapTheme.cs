using Avalonia;
using Avalonia.Markup.Xaml.Styling;

namespace Playnite.Desktop.Bootstrap;

/// <summary>What happened when the startup theme was applied.</summary>
public sealed record ThemeLoadResult(string Name, string Diagnostic, bool Applied);

/// <summary>
/// Adds an optional on-disk theme to the application styles. The Fluent base theme installed by
/// App.Initialize stays active; a missing or unreadable theme leaves it unchanged. Themes are
/// an app-local concern and never enter Core or the SDK UI surface.
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
            return new ThemeLoadResult("Default", diagnostic, true);
        }

        var fullPath = Path.GetFullPath(themePath);
        if (!File.Exists(fullPath))
        {
            var diagnostic = $"theme: {fullPath} is missing; built-in Fluent remains active.";
            logger.Error(diagnostic);
            return new ThemeLoadResult("Default", diagnostic, false);
        }

        try
        {
            var include = new StyleInclude(new Uri("avares://Playnite.Desktop/"))
            {
                Source = new Uri(fullPath, UriKind.Absolute)
            };
            application.Styles.Add(include);
            var name = Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? "Custom";
            var diagnostic = $"theme: loaded {name} from {fullPath}";
            logger.Info(diagnostic);
            return new ThemeLoadResult(name, diagnostic, true);
        }
        catch (Exception exception) when (exception is IOException or UriFormatException
            or ArgumentException or InvalidOperationException)
        {
            var diagnostic = $"theme: could not load {fullPath}: {exception.Message}; built-in Fluent remains active.";
            logger.Error(diagnostic);
            return new ThemeLoadResult("Default", diagnostic, false);
        }
    }
}
