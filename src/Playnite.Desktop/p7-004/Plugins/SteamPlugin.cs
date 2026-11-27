using System.Globalization;
using Playnite.Core;
using Playnite.Core.Models;
using Playnite.Core.Plugins;
using Playnite.Desktop.Shell;
using Playnite.Platform.Linux;
using Playnite.Steam;

namespace Playnite.Desktop.Plugins;

/// <summary>
/// Built-in Steam integration providing library source identity, URI launch resolution,
/// local VDF/ACF SQLite sync, and declarative root-override settings.
/// </summary>
internal sealed class SteamPlugin(ILibrarySteamSync? syncRunner = null) :
    IPlaynitePlugin,
    ILibrarySourceCapability,
    ILaunchCapability,
    ILibrarySyncCapability,
    IPluginSettingsCapability
{
    internal const string PluginId = "playnite.builtin.steam";

    private static readonly PluginDescriptor DescriptorValue = new(
        PluginId,
        StoreCatalog.SteamPluginId,
        "Steam",
        new Version(1, 0, 0),
        PluginLoadKind.BuiltIn,
        PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen,
        PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch | PluginCapabilityKind.LibrarySync | PluginCapabilityKind.Settings,
        new PluginEntryPoints(),
        Description: "Local Steam root discovery, SQLite synchronization, and rungameid launch handoff.");

    private static readonly LibrarySourceInfo SourceInfo = new(
        StoreCatalog.SteamPluginId,
        "Steam",
        "Steam",
        SupportsSync: true);

    private static readonly IReadOnlyList<PluginSettingField> SettingsFields =
    [
        new PluginSettingField(
            "steamRootOverride",
            "Steam root directory",
            PluginSettingType.Path,
            Description: "Optional override for Steam root discovery (equivalent to STEAM_ROOT)."),
    ];

    private ILibrarySteamSync? cachedDefaultRunner;
    private string? cachedRootOverride;
    private PluginContext? pluginContext;

    public PluginDescriptor Descriptor => DescriptorValue;

    public LibrarySourceInfo Source => SourceInfo;

    public Guid SourceId => StoreCatalog.SteamPluginId;

    string ILibrarySyncCapability.PluginId => PluginId;

    public string DisplayName => "Steam";

    public IReadOnlyList<PluginSettingField> Fields => SettingsFields;

    internal string? ConfiguredRootOverride => pluginContext?.GetSetting("steamRootOverride");

    public PluginRegistration Setup(PluginContext context)
    {
        pluginContext = context;
        return new()
        {
            LibrarySources = [this],
            LaunchProviders = [this],
            SyncProviders = [this],
            Settings = this,
        };
    }

    public PluginLaunchSpec DescribeLaunch(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.IsInstalled
            && FlatpakSteamLauncher.TryParseAppId(game.GameId, out var appId)
            && appId != 0)
        {
            return new PluginLaunchSpec(
                PluginLaunchKind.UriHandoff,
                string.Create(CultureInfo.InvariantCulture, $"Steam (App {appId})"),
                Uri: string.Create(CultureInfo.InvariantCulture, $"steam://rungameid/{appId}"));
        }

        return new PluginLaunchSpec(PluginLaunchKind.None, "Not configured", Error: null);
    }

    public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null) =>
        DescribeLaunch(game);

    public async Task<PluginSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken)
    {
        var runner = GetOrCreateRunner();
        try
        {
            var result = await runner.SyncAsync(databasePath, cancellationToken).ConfigureAwait(false);
            return new PluginSyncResult(
                PluginId,
                Succeeded: result.Reconciled,
                Reconciled: result.Reconciled,
                Added: result.Added,
                Updated: result.Updated,
                Removed: result.Removed,
                Unchanged: result.Unchanged,
                Skipped: result.Skipped.Count,
                Summary: LibrarySteamSyncRunner.FormatSummary(result),
                Issues: result.Issues.Select(issue => issue.Reason).ToArray(),
                LinkedTitlesUpdated: result.LinkedTitlesUpdated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PluginSyncResult(
                PluginId,
                Succeeded: false,
                Reconciled: false,
                Added: 0,
                Updated: 0,
                Removed: 0,
                Unchanged: 0,
                Skipped: 0,
                Summary: $"Steam sync failed: {exception.Message}",
                Issues: [exception.Message]);
        }
    }

    private ILibrarySteamSync GetOrCreateRunner()
    {
        if (syncRunner is not null)
        {
            return syncRunner;
        }

        var rootOverride = ConfiguredRootOverride;
        if (cachedDefaultRunner is null || !string.Equals(cachedRootOverride, rootOverride, StringComparison.Ordinal))
        {
            cachedRootOverride = rootOverride;
            IEnvironment env = string.IsNullOrWhiteSpace(rootOverride)
                ? new LinuxEnvironment()
                : new SteamRootEnvironmentOverride(new LinuxEnvironment(), rootOverride);
            cachedDefaultRunner = new LibrarySteamSyncRunner(env);
        }

        return cachedDefaultRunner;
    }

    private sealed class SteamRootEnvironmentOverride(IEnvironment inner, string steamRoot) : IEnvironment
    {
        public string Home => inner.Home;

        public string CurrentDirectory => inner.CurrentDirectory;

        public string ProgramDirectory => inner.ProgramDirectory;

        public string? GetVariable(string name) =>
            string.Equals(name, "STEAM_ROOT", StringComparison.Ordinal) ? steamRoot : inner.GetVariable(name);
    }
}
