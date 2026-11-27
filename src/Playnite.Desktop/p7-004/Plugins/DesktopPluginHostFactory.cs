using Playnite.Core.Plugins;

namespace Playnite.Desktop.Plugins;

/// <summary>Creates configured <see cref="PluginHost"/> instances for Desktop and Fullscreen shells.</summary>
internal static class DesktopPluginHostFactory
{
    private static readonly Lazy<PluginHost> SharedDefaultHost = new(() => CreateDefault());

    internal static PluginHost DefaultBuiltInHost => SharedDefaultHost.Value;

    internal static PluginHost CreateDefault(
        PluginSurface surface = PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen,
        TimeProvider? timeProvider = null,
        IPluginSettingsStore? settingsStore = null)
    {
        var host = new PluginHost(surface, timeProvider, settingsStore);
        host.RegisterBuiltIn(new AmazonLunaPlugin());
        host.RegisterBuiltIn(new SnesRomPlugin());
        return host;
    }
}
