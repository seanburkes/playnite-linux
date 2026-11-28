using System.Net.Http;
using Playnite.Core.Models;
using Playnite.Core.Plugins;
using Playnite.Desktop.Shell;

namespace Playnite.Desktop.Plugins;

/// <summary>
/// Multi-source built-in Heroic integration providing Epic, GOG, and Amazon source identities,
/// <c>heroic://</c> URI launch plans, and bounded local Heroic SQLite sync.
/// </summary>
internal sealed class HeroicPlugin :
    IPlaynitePlugin,
    ILibrarySyncCapability,
    ICommandCapability,
    IPluginSettingsCapability
{
    internal const string PluginId = "playnite.builtin.heroic";
    internal const string SyncCommandId = "heroic.sync";

    private static readonly PluginDescriptor DescriptorValue = new(
        PluginId,
        Guid.Empty,
        "Heroic",
        new Version(1, 0, 0),
        PluginLoadKind.BuiltIn,
        PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Fullscreen,
        PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch | PluginCapabilityKind.LibrarySync | PluginCapabilityKind.Commands | PluginCapabilityKind.Settings,
        new PluginEntryPoints(),
        Description: "Heroic Games Launcher integration for Epic, GOG, and Amazon libraries.");

    private static readonly IReadOnlyList<PluginSettingField> SettingsFields =
    [
        new PluginSettingField(
            "heroicConfigDirectory",
            "Heroic config directory",
            PluginSettingType.Path,
            Description: "Optional path to Heroic's configuration directory."),
    ];

    private readonly ILibraryHeroicSync? syncRunner;
    private readonly Func<PluginSyncResult, CancellationToken, Task>? onSyncCompleted;
    private readonly HeroicStoreCapability[] stores;
    private ILibraryHeroicSync? cachedDefaultRunner;
    private PluginContext? pluginContext;

    internal HeroicPlugin(
        ILibraryHeroicSync? syncRunner = null,
        Func<PluginSyncResult, CancellationToken, Task>? onSyncCompleted = null)
    {
        this.syncRunner = syncRunner;
        this.onSyncCompleted = onSyncCompleted;
        stores =
        [
            new HeroicStoreCapability(StoreCatalog.EpicPluginId, "Epic"),
            new HeroicStoreCapability(StoreCatalog.GogPluginId, "GOG"),
            new HeroicStoreCapability(StoreCatalog.AmazonPluginId, "Amazon"),
        ];
    }

    public PluginDescriptor Descriptor => DescriptorValue;

    string ILibrarySyncCapability.PluginId => PluginId;

    public string DisplayName => "Heroic";

    public IReadOnlyList<PluginSettingField> Fields => SettingsFields;

    public IReadOnlyList<PluginCommandDescriptor> Commands { get; } =
    [
        new PluginCommandDescriptor(
            SyncCommandId,
            PluginId,
            "Sync Heroic Libraries",
            PluginCommandPlacement.LibraryToolbar),
    ];

    internal string? ConfiguredConfigDirectory => pluginContext?.GetSetting("heroicConfigDirectory");

    public PluginRegistration Setup(PluginContext context)
    {
        pluginContext = context;
        return new()
        {
            LibrarySources = stores,
            LaunchProviders = stores,
            SyncProviders = [this],
            CommandProviders = [this],
            Settings = this,
        };
    }

    public async Task<PluginCommandResult> ExecuteAsync(
        string commandId,
        PluginCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(commandId, SyncCommandId, StringComparison.Ordinal))
        {
            return new PluginCommandResult(false, $"Unknown Heroic command '{commandId}'.");
        }

        if (string.IsNullOrWhiteSpace(context.DatabasePath))
        {
            return new PluginCommandResult(false, "Library database path is required to sync Heroic.");
        }

        var result = await SyncAsync(context.DatabasePath, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && onSyncCompleted is not null)
        {
            await onSyncCompleted(result, CancellationToken.None).ConfigureAwait(false);
        }

        return new PluginCommandResult(result.Succeeded, result.Summary, RefreshLibrary: result.Succeeded);
    }

    public async Task<PluginSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken)
    {
        var runner = syncRunner ?? (cachedDefaultRunner ??= new HeroicLibrarySyncRunner());
        try
        {
            var result = await runner.SyncAsync(databasePath, cancellationToken).ConfigureAwait(false);
            return new PluginSyncResult(
                PluginId,
                Succeeded: result.Reconciled,
                Reconciled: result.Reconciled,
                Added: result.Added,
                Updated: result.Updated,
                Removed: result.Uninstalled,
                Unchanged: result.Unchanged,
                Skipped: result.Skipped,
                Summary: HeroicLibrarySyncRunner.FormatSummary(result),
                Issues: result.Issues,
                LinkedTitlesUpdated: result.LinkedTitlesUpdated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException)
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
                Summary: $"Heroic sync failed: {exception.Message}",
                Issues: [exception.Message]);
        }
    }

    private sealed class HeroicStoreCapability(Guid sourceId, string displayName)
        : ILibrarySourceCapability, ILaunchCapability
    {
        public LibrarySourceInfo Source { get; } = new(sourceId, displayName, displayName, SupportsSync: true);

        public Guid SourceId => sourceId;

        public PluginLaunchSpec DescribeLaunch(Game game)
        {
            ArgumentNullException.ThrowIfNull(game);
            if (game.IsInstalled && StoreCatalog.TryBuildHeroicLaunchUri(sourceId, game.GameId, out var uri))
            {
                return new PluginLaunchSpec(
                    PluginLaunchKind.UriHandoff,
                    $"Heroic ({displayName})",
                    Uri: uri);
            }

            return new PluginLaunchSpec(PluginLaunchKind.None, "Not configured", Error: null);
        }

        public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null) =>
            DescribeLaunch(game);
    }
}
