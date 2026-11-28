using System.Net.Http;
using Playnite.Core;
using Playnite.Core.Addons;
using Playnite.Core.Plugins;
using Playnite.Desktop.Shell;
using Playnite.Platform.Linux;

namespace Playnite.Desktop.Plugins;

/// <summary>Creates configured <see cref="PluginHost"/> instances for Desktop and Fullscreen shells.</summary>
internal static class DesktopPluginHostFactory
{
    internal const string PluginsDirectoryVariable = "PLAYNITE_PLUGINS_DIR";

    private static readonly Lazy<PluginHost> SharedDefaultHost = new(() => CreateDefault());
    private static readonly Lock PackageStartupGate = new();
    private static readonly HashSet<string> PreparedPackageRoots = new(StringComparer.Ordinal);

    internal static PluginHost DefaultBuiltInHost => SharedDefaultHost.Value;

    internal static AddonInstaller? CreateAddonInstaller(string databasePath) =>
        ResolvePluginsDirectory(databasePath) is { } root
            ? new AddonInstaller(new AddonInstallLayout(root), new LinuxRegularFileProbe())
            : null;

    internal static PluginHost CreateDefault(
        PluginSurface surface = PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen | PluginSurface.Theme,
        TimeProvider? timeProvider = null,
        IPluginSettingsStore? settingsStore = null,
        ILibrarySteamSync? steamSync = null,
        ILibraryHeroicSync? heroicSync = null,
        HttpClient? protonDbClient = null)
    {
        var host = new PluginHost(surface, timeProvider, settingsStore);
        Task PublishCommandSyncAsync(PluginSyncResult result, CancellationToken ct) =>
            host.IsDisposed ? Task.CompletedTask : host.PublishSyncResultAsync(result, ct);

        host.RegisterBuiltIn(new SteamPlugin(steamSync, PublishCommandSyncAsync));
        host.RegisterBuiltIn(new HeroicPlugin(heroicSync, PublishCommandSyncAsync));
        host.RegisterBuiltIn(new AmazonLunaPlugin());
        host.RegisterBuiltIn(new SnesRomPlugin());
        host.RegisterBuiltIn(new ProtonDbPlugin(protonDbClient));
        return host;
    }

    internal static PluginHost CreateForLibrary(
        string databasePath,
        PluginSurface surface = PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen | PluginSurface.Theme,
        TimeProvider? timeProvider = null,
        IPluginSettingsStore? settingsStore = null,
        IRegularFileProbe? regularFileProbe = null,
        string? externalPluginsRoot = null,
        ILibrarySteamSync? steamSync = null,
        ILibraryHeroicSync? heroicSync = null,
        HttpClient? protonDbClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var effectiveProbe = regularFileProbe ?? new LinuxRegularFileProbe();
        var settingsDir = ResolveSettingsDirectory(databasePath);
        PluginHost? createdHost = null;
        var resolvedSettings = settingsStore ?? (settingsDir is not null
            ? new FilePluginSettingsStore(
                settingsDir,
                (pluginId, msg) => createdHost?.RecordDiagnostic(pluginId, "settings-store-error", msg),
                effectiveProbe)
            : new InMemoryPluginSettingsStore());

        var host = new PluginHost(surface, timeProvider, resolvedSettings);
        createdHost = host;
        Task PublishCommandSyncAsync(PluginSyncResult result, CancellationToken ct) =>
            host.IsDisposed ? Task.CompletedTask : host.PublishSyncResultAsync(result, ct);

        host.RegisterBuiltIn(new SteamPlugin(steamSync, PublishCommandSyncAsync));
        host.RegisterBuiltIn(new HeroicPlugin(heroicSync, PublishCommandSyncAsync));
        host.RegisterBuiltIn(new AmazonLunaPlugin());
        host.RegisterBuiltIn(new SnesRomPlugin());
        host.RegisterBuiltIn(new ProtonDbPlugin(protonDbClient, settingsDir, effectiveProbe));

        var pluginsDir = ResolvePluginsDirectory(databasePath, externalPluginsRoot);
        if (pluginsDir is not null && Directory.Exists(pluginsDir))
        {
            lock (PackageStartupGate)
            {
                if (PreparedPackageRoots.Add(pluginsDir))
                {
                    var installer = new AddonInstaller(new AddonInstallLayout(pluginsDir), effectiveProbe);
                    try
                    {
                        installer.CleanRetiredPackages((id, message) => host.RecordDiagnostic(id, "package-cleanup-failed", message));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        host.RecordDiagnostic(string.Empty, "package-cleanup-failed", exception.Message);
                    }
                }
            }
            host.DiscoverExternal(pluginsDir, effectiveProbe);
        }

        return host;
    }

    private static string? ResolveSettingsDirectory(string databasePath)
    {
        if (!Path.IsPathFullyQualified(databasePath) || databasePath.Contains('\0'))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        return string.IsNullOrWhiteSpace(parent) ? null : Path.Combine(parent, "plugin-settings");
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
