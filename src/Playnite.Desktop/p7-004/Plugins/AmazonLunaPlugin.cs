using Playnite.Core.Models;
using Playnite.Core.Plugins;
using Playnite.Desktop.Shell;

namespace Playnite.Desktop.Plugins;

/// <summary>
/// Built-in plugin providing Amazon Luna cloud-streaming source identity, browser handoff launch
/// planning, and declarative Chrome path settings.
/// </summary>
internal sealed class AmazonLunaPlugin :
    IPlaynitePlugin,
    ILibrarySourceCapability,
    ILaunchCapability,
    IPluginSettingsCapability
{
    internal const string PluginId = "playnite.builtin.amazon-luna";

    private static readonly PluginDescriptor DescriptorValue = new(
        PluginId,
        StoreCatalog.LunaPluginId,
        "Amazon Luna",
        new Version(1, 0, 0),
        PluginLoadKind.BuiltIn,
        PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen,
        PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch | PluginCapabilityKind.Settings,
        new PluginEntryPoints(),
        Description: "Cloud-streaming handoff via Google Chrome for official Amazon Luna game links.");

    private static readonly LibrarySourceInfo SourceInfo = new(
        StoreCatalog.LunaPluginId,
        "Amazon Luna",
        "Amazon Luna",
        DefaultStatusLabel: "Cloud streaming",
        SupportsSync: false,
        IsCloudStreaming: true);

    private static readonly IReadOnlyList<PluginSettingField> SettingsFields =
    [
        new PluginSettingField(
            "chromeExecutable",
            "Google Chrome executable",
            PluginSettingType.Path,
            Description: $"Optional absolute path to Google Chrome (overrides {LunaPlayRequest.ChromeExecutableVariable})."),
    ];

    private PluginContext? pluginContext;

    public PluginDescriptor Descriptor => DescriptorValue;

    public LibrarySourceInfo Source => SourceInfo;

    public Guid SourceId => StoreCatalog.LunaPluginId;

    public IReadOnlyList<PluginSettingField> Fields => SettingsFields;

    public PluginRegistration Setup(PluginContext context)
    {
        pluginContext = context;
        return new()
        {
            LibrarySources = [this],
            LaunchProviders = [this],
            Settings = this,
        };
    }

    internal string? ConfiguredChromeExecutable => pluginContext?.GetSetting("chromeExecutable");

    public PluginLaunchSpec DescribeLaunch(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var uri = LunaPlayRequest.ParseGameUrl(LunaPlayRequest.FindPlayAction(game)?.Path);
        return uri is not null
            ? new PluginLaunchSpec(
                PluginLaunchKind.BrowserHandoff,
                "Amazon Luna (Google Chrome)",
                Uri: uri.AbsoluteUri)
            : new PluginLaunchSpec(
                PluginLaunchKind.None,
                "Invalid Amazon Luna game link",
                Error: LunaPlayRequest.InvalidUrlMessage);
    }

    public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null) =>
        DescribeLaunch(game);
}
