namespace Playnite.Desktop.Bootstrap;

/// <summary>Pure theme path resolution: a theme name never escapes its themes root.</summary>
public static class ThemePaths
{
    public const string ThemeFileName = "theme.axaml";

    public static bool TryResolve(string themesRoot, string themeName, out string path, out string error)
    {
        path = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(themesRoot))
        {
            error = "The themes root is not configured.";
            return false;
        }

        if (!IsSafeName(themeName))
        {
            error = $"Theme name '{themeName}' is not a simple directory name.";
            return false;
        }

        path = Path.Combine(Path.GetFullPath(themesRoot), themeName, ThemeFileName);
        return true;
    }

    private static bool IsSafeName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 64
        && name[0] != '.'
        && name[^1] != '.'
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
