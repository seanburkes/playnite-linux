using Playnite.Core.Models;
using Playnite.Core.Plugins;
using Playnite.Desktop.Shell;

namespace Playnite.Desktop.Plugins;

/// <summary>
/// Built-in plugin providing SNES ROM source identity, tracked Snes9x/RetroArch launch planning,
/// and declarative emulator settings.
/// </summary>
internal sealed class SnesRomPlugin :
    IPlaynitePlugin,
    ILibrarySourceCapability,
    ILaunchCapability,
    IPluginSettingsCapability
{
    internal const string PluginId = "playnite.builtin.snes-roms";

    private static readonly PluginDescriptor DescriptorValue = new(
        PluginId,
        SnesRomProfile.SourceId,
        "SNES",
        new Version(1, 0, 0),
        PluginLoadKind.BuiltIn,
        PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen,
        PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch | PluginCapabilityKind.Settings,
        new PluginEntryPoints(),
        Description: "SNES ROM folder source with standalone Snes9x and RetroArch launch profiles.");

    private static readonly LibrarySourceInfo SourceInfo = new(
        SnesRomProfile.SourceId,
        "SNES",
        "SNES");

    private static readonly IReadOnlyList<PluginSettingField> SettingsFields =
    [
        new PluginSettingField(
            "defaultProfile",
            "Default emulator profile",
            PluginSettingType.Choice,
            DefaultValue: SnesRomProfile.Standalone.Name,
            Choices: [SnesRomProfile.Standalone.Name, SnesRomProfile.RetroArch.Name],
            Description: "Emulator profile selected when scanning a new SNES ROM folder."),
        new PluginSettingField(
            "retroArchCorePath",
            "RetroArch Snes9x core",
            PluginSettingType.Path,
            Description: "Path to snes9x_libretro.so when launching through RetroArch."),
    ];

    private PluginContext? pluginContext;

    public PluginDescriptor Descriptor => DescriptorValue;

    public LibrarySourceInfo Source => SourceInfo;

    public Guid SourceId => SnesRomProfile.SourceId;

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

    internal string? ConfiguredCorePath => pluginContext?.GetSetting("retroArchCorePath");

    public PluginLaunchSpec DescribeLaunch(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var emulator = SnesRomProfile.FindAction(game);
        if (emulator is not null)
        {
            return new PluginLaunchSpec(PluginLaunchKind.TrackedProcess, emulator.Name ?? "SNES");
        }

        return GameLaunchPlanner.FindFilePlayAction(game) is not null
            ? new PluginLaunchSpec(PluginLaunchKind.None, "Not configured", Error: null)
            : new PluginLaunchSpec(PluginLaunchKind.None, "Not configured", Error: "No SNES emulator play action configured.");
    }

    public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        var emulator = SnesRomProfile.FindAction(game);
        if (emulator is null)
        {
            return GameLaunchPlanner.FindFilePlayAction(game) is not null
                ? new PluginLaunchSpec(PluginLaunchKind.None, "Not configured", Error: null)
                : new PluginLaunchSpec(PluginLaunchKind.None, "Not configured", Error: "No SNES emulator play action configured.");
        }

        var plan = SnesRomProfile.Plan(game, emulator, toolResolver);
        if (plan.Request is null)
        {
            return new PluginLaunchSpec(
                PluginLaunchKind.None,
                emulator.Name ?? "SNES",
                Error: plan.Error ?? "Could not resolve SNES launch plan.");
        }

        return new PluginLaunchSpec(
            PluginLaunchKind.TrackedProcess,
            emulator.Name ?? "SNES",
            Executable: plan.Request.Executable,
            Arguments: plan.Request.Arguments,
            WorkingDirectory: plan.Request.WorkingDirectory);
    }
}
