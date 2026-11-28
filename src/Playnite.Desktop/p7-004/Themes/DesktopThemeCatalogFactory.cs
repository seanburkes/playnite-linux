using System.Runtime.InteropServices;
using Avalonia;
using Playnite.Core;
using Playnite.Core.Plugins;
using Playnite.Core.Themes;
using Playnite.Platform;
using Playnite.Platform.Linux;

namespace Playnite.Desktop.Themes;

/// <summary>Resolves configured theme roots and builds <see cref="LiveThemeManager"/> instances.</summary>
internal static class DesktopThemeCatalogFactory
{
    internal const string ThemesDirectoryVariable = "PLAYNITE_THEMES_DIR";

    internal static IReadOnlyList<string> ResolveThemeRoots(string databasePath, string? externalThemesRoot = null)
    {
        var roots = new List<string>();
        AddIfValidAbsolute(roots, externalThemesRoot);

        var envDir = Environment.GetEnvironmentVariable(ThemesDirectoryVariable);
        AddIfValidAbsolute(roots, envDir);

        if (!string.IsNullOrWhiteSpace(databasePath)
            && !databasePath.Contains('\0')
            && Path.IsPathFullyQualified(databasePath))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrWhiteSpace(parent))
            {
                AddIfValidAbsolute(roots, Path.Combine(parent, "themes"));
            }
        }

        return roots;
    }

    internal static LiveThemeManager CreateForLibrary(
        Application? application,
        string databasePath,
        PluginHost pluginHost,
        IRegularFileProbe? regularFileProbe = null,
        string? externalThemesRoot = null,
        IThemeFileReader? fileReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(pluginHost);

        var probe = regularFileProbe ?? new LinuxRegularFileProbe();
        var resolvedReader = fileReader ?? CreateDefaultFileReader();
        var roots = ResolveThemeRoots(databasePath, externalThemesRoot);
        return new LiveThemeManager(application, roots, probe, pluginHost, fileReader: resolvedReader);
    }

    internal static IThemeFileReader? CreateDefaultFileReader() =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64
            ? new LinuxThemeFileReader()
            : null;

    private static void AddIfValidAbsolute(List<string> roots, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Contains('\0')
            || !Path.IsPathFullyQualified(candidate))
        {
            return;
        }

        var full = ThemePackParser.TrimRootPath(Path.GetFullPath(candidate));
        if (!roots.Contains(full, StringComparer.Ordinal))
        {
            roots.Add(full);
        }
    }

    internal sealed class LinuxThemeFileReader : IThemeFileReader
    {
        private readonly LinuxBoundedFileReader inner = new();

        public ThemeFileReadResult Read(string path, int maxBytes)
        {
            try
            {
                var result = inner.Read(path, maxBytes);
                return result.Status switch
                {
                    BoundedFileReadStatus.Ok => new ThemeFileReadResult(ThemeFileReadStatus.Ok, result.Bytes),
                    BoundedFileReadStatus.Missing => new ThemeFileReadResult(ThemeFileReadStatus.Missing, null, $"Theme file '{path}' is missing."),
                    BoundedFileReadStatus.NotRegularFile => new ThemeFileReadResult(
                        ThemeFileReadStatus.NotRegularFile,
                        null,
                        $"Theme file '{path}' is empty, symlinked, or not a regular file."),
                    BoundedFileReadStatus.TooLarge => new ThemeFileReadResult(
                        ThemeFileReadStatus.TooLarge,
                        null,
                        $"Theme file '{path}' exceeds the {maxBytes}-byte limit."),
                    _ => new ThemeFileReadResult(ThemeFileReadStatus.ReadFailed, null, $"Could not read '{path}'."),
                };
            }
            catch (IOException exception) when (exception.Message.Contains("errno 40", StringComparison.Ordinal)
                || exception.Message.Contains("errno 20", StringComparison.Ordinal))
            {
                return new ThemeFileReadResult(
                    ThemeFileReadStatus.NotRegularFile,
                    null,
                    $"Refusing symlinked or non-directory path segment at '{path}': {exception.Message}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
            {
                return new ThemeFileReadResult(
                    ThemeFileReadStatus.ReadFailed,
                    null,
                    $"Could not read '{path}': {exception.Message}");
            }
        }
    }
}
