using Playnite.Core;
using Playnite.Core.Plugins;
using Playnite.Desktop.Shell;
using Playnite.Platform.Linux;

namespace Playnite.Desktop.Plugins;

/// <summary>Creates configured <see cref="PluginHost"/> instances for Desktop and Fullscreen shells.</summary>
internal static class DesktopPluginHostFactory
{
    internal const string PluginsDirectoryVariable = "PLAYNITE_PLUGINS_DIR";

    private static readonly Lazy<PluginHost> SharedDefaultHost = new(() => CreateDefault());

    internal static PluginHost DefaultBuiltInHost => SharedDefaultHost.Value;

    internal static PluginHost CreateDefault(
        PluginSurface surface = PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen,
        TimeProvider? timeProvider = null,
        IPluginSettingsStore? settingsStore = null,
        ILibrarySteamSync? steamSync = null,
        ILibraryHeroicSync? heroicSync = null)
    {
        var host = new PluginHost(surface, timeProvider, settingsStore);
        host.RegisterBuiltIn(new SteamPlugin(steamSync));
        host.RegisterBuiltIn(new HeroicPlugin(heroicSync));
        host.RegisterBuiltIn(new AmazonLunaPlugin());
        host.RegisterBuiltIn(new SnesRomPlugin());
        return host;
    }

    internal static PluginHost CreateForLibrary(
        string databasePath,
        PluginSurface surface = PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen,
        TimeProvider? timeProvider = null,
        IPluginSettingsStore? settingsStore = null,
        IRegularFileProbe? regularFileProbe = null,
        string? externalPluginsRoot = null,
        ILibrarySteamSync? steamSync = null,
        ILibraryHeroicSync? heroicSync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var host = CreateDefault(surface, timeProvider, settingsStore, steamSync, heroicSync);
        var pluginsDir = ResolvePluginsDirectory(databasePath, externalPluginsRoot);
        if (pluginsDir is not null && Directory.Exists(pluginsDir))
        {
            host.DiscoverExternal(pluginsDir, regularFileProbe ?? new LinuxRegularFileProbe());
        }

        return host;
    }

    internal static string? ResolvePluginsDirectory(string databasePath, string? externalPluginsRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(externalPluginsRoot))
        {
            return Path.IsPathFullyQualified(externalPluginsRoot)
                ? Path.GetFullPath(externalPluginsRoot)
                : null;
        }

        var envDir = Environment.GetEnvironmentVariable(PluginsDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            // Require an absolute path to prevent CWD-hijacked assembly loading.
            return Path.IsPathFullyQualified(envDir) && !envDir.Contains('\0')
                ? Path.GetFullPath(envDir)
                : null;
        }

        if (!Path.IsPathFullyQualified(databasePath))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        return string.IsNullOrWhiteSpace(parent) ? null : Path.Combine(parent, "plugins");
    }
}
