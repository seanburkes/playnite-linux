using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Plugins;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Plugins;
using Playnite.Desktop.Shell;
using Playnite.Platform.Sessions;
using Playnite.Steam;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class PluginArchitectureIntegrationTests
{
    [Fact]
    public void Default_desktop_plugin_host_registers_luna_and_snes_builtins()
    {
        using var host = DesktopPluginHostFactory.CreateDefault();

        Assert.True(host.TryGetSource(StoreCatalog.LunaPluginId, out var lunaSource));
        Assert.Equal("Amazon Luna", lunaSource.DisplayName);
        Assert.True(lunaSource.IsCloudStreaming);
        Assert.Equal("Cloud streaming", lunaSource.DefaultStatusLabel);

        Assert.True(host.TryGetSource(SnesRomProfile.SourceId, out var snesSource));
        Assert.Equal("SNES", snesSource.DisplayName);
        Assert.False(snesSource.IsCloudStreaming);

        Assert.NotEmpty(host.GetSettingsSchema(AmazonLunaPlugin.PluginId));
        Assert.NotEmpty(host.GetSettingsSchema(SnesRomPlugin.PluginId));
    }

    [Fact]
    public async Task Disabling_builtin_plugins_removes_source_and_launch_capabilities_without_touching_database()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-plugin-disable-");
        try
        {
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            var romPath = Path.Combine(tempDir.FullName, "Chrono.sfc");
            await File.WriteAllBytesAsync(romPath, [], token);

            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "/game/fortnite", "Fortnite")
                {
                    PluginId = StoreCatalog.LunaPluginId,
                    GameActions = [new GameAction { Name = "Play", Type = GameActionType.URL, IsPlayAction = true, Path = LunaPlayRequest.FortniteUrl }],
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), romPath, "Chrono")
                {
                    PluginId = SnesRomProfile.SourceId,
                    IsInstalled = true,
                    Roms = [new GameRom("Chrono", romPath)],
                    GameActions = [SnesRomProfile.Standalone.CreateAction(null)],
                }));
                return true;
            }, token);

            using var host = DesktopPluginHostFactory.CreateDefault();
            var model = new LibraryViewModel(dbPath, host, ownsPlugins: false);
            try
            {
                await model.LoadAsync(token);

                var lunaRow = Assert.Single(model.Rows, r => r.Name == "Fortnite");
                Assert.True(lunaRow.CanPlay);
                Assert.Equal("Cloud streaming", lunaRow.StatusLabel);
                Assert.Equal("Amazon Luna (Google Chrome)", lunaRow.LaunchTargetDisplay);

                var snesRow = Assert.Single(model.Rows, r => r.Name == "Chrono");
                Assert.True(snesRow.CanPlay);
                Assert.Equal("SNES", snesRow.SourceDisplay);
                Assert.Equal("Snes9x", snesRow.LaunchTargetDisplay);

                Assert.True(host.Disable(AmazonLunaPlugin.PluginId));
                Assert.True(host.Disable(SnesRomPlugin.PluginId));
                await model.ReloadCurrentPageAsync(token);

                var disabledLuna = Assert.Single(model.Rows, r => r.Name == "Fortnite");
                Assert.False(disabledLuna.CanPlay);
                Assert.Equal("Not installed", disabledLuna.StatusLabel);

                var disabledSnes = Assert.Single(model.Rows, r => r.Name == "Chrono");
                Assert.False(disabledSnes.CanPlay);
                Assert.Equal("Not configured", disabledSnes.LaunchTargetDisplay);
            }
            finally
            {
                model.Dispose();
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Custom_plugin_drives_library_row_and_enforces_launch_safety_guards()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-plugin-custom-");
        try
        {
            var customSourceId = Guid.Parse("d4444444-4444-4444-4444-444444444444");
            var unsafeSourceId = Guid.Parse("d5555555-5555-5555-5555-555555555555");
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            var safeGameId = Guid.NewGuid();
            var unsafeGameId = Guid.NewGuid();
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(safeGameId, "custom-rom-1", "Custom Arcade")
                {
                    PluginId = customSourceId,
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(unsafeGameId, "unsafe-1", "Shell Game")
                {
                    PluginId = unsafeSourceId,
                    IsInstalled = true,
                    GameActions = [new GameAction { Type = GameActionType.File, IsPlayAction = true, Path = "/usr/bin/true" }],
                }));
                return true;
            }, token);

            using var host = DesktopPluginHostFactory.CreateDefault();
            Assert.True(host.RegisterBuiltIn(new CustomArcadePlugin(customSourceId)));
            Assert.True(host.RegisterBuiltIn(new UnsafeShellPlugin(unsafeSourceId)));

            var model = new LibraryViewModel(dbPath, host, ownsPlugins: false);
            try
            {
                await model.LoadAsync(token);

                var row = Assert.Single(model.Rows, r => r.Id == safeGameId);
                Assert.Equal("Arcade Core", row.SourceDisplay);
                Assert.Equal("Ready to play", row.StatusLabel);
                Assert.Equal("Arcade Runner", row.LaunchTargetDisplay);
                Assert.True(row.CanPlay);
                Assert.True(row.HasTrackedPlayAction);

                var storedSafe = await model.GetStoredGameAsync(safeGameId, token);
                Assert.NotNull(storedSafe);
                var plan = GameLaunchPlanner.TryBuildLaunchRequest(storedSafe!.Value, null, host);
                Assert.Null(plan.Error);
                Assert.NotNull(plan.Request);
                Assert.Equal("/usr/bin/true", plan.Request!.Executable);
                Assert.Equal(["--rom", "custom-rom-1"], plan.Request.Arguments);

                // Shell interpreter returned by plugin must be rejected by GameLaunchPlanner.
                var storedUnsafe = await model.GetStoredGameAsync(unsafeGameId, token);
                Assert.NotNull(storedUnsafe);
                var rejected = GameLaunchPlanner.TryBuildLaunchRequest(storedUnsafe!.Value, null, host);
                Assert.Null(rejected.Request);
                Assert.Contains("shell interpreter", rejected.Error, StringComparison.OrdinalIgnoreCase);

                // BrowserHandoff plugin cannot fall through to a File action.
                var lunaGame = new Game(Guid.NewGuid(), "/game/fortnite", "Fortnite")
                {
                    PluginId = StoreCatalog.LunaPluginId,
                    GameActions =
                    [
                        new GameAction { Type = GameActionType.URL, IsPlayAction = true, Path = LunaPlayRequest.FortniteUrl },
                        new GameAction { Type = GameActionType.File, IsPlayAction = true, Path = "/usr/bin/true" },
                    ],
                };
                var lunaTracked = GameLaunchPlanner.TryBuildLaunchRequest(lunaGame, null, host);
                Assert.Null(lunaTracked.Request);
                Assert.Contains("cannot run as a tracked process", lunaTracked.Error);
            }
            finally
            {
                model.Dispose();
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Custom_uri_handoff_plugin_and_session_events_work_through_desktop_view()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-plugin-ui-");
        try
        {
            var uriSourceId = Guid.Parse("d6666666-6666-6666-6666-666666666666");
            var trackedSourceId = Guid.Parse("d7777777-7777-7777-7777-777777777777");
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            var uriGameId = Guid.NewGuid();
            var failGameId = Guid.NewGuid();

            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(uriGameId, "uri-1", "AAA Uri Game")
                {
                    PluginId = uriSourceId,
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(failGameId, "missing-1", "ZZZ Missing Exe Game")
                {
                    PluginId = trackedSourceId,
                    IsInstalled = true,
                }));
                return true;
            }, token);

            await using var uiSession = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
            await uiSession.Dispatch(async () =>
            {
                using var host = DesktopPluginHostFactory.CreateDefault();
                var hookPlugin = new UriAndEventPlugin(uriSourceId);
                Assert.True(host.RegisterBuiltIn(hookPlugin));
                Assert.True(host.RegisterBuiltIn(new MissingExePlugin(trackedSourceId)));

                var opener = new FakeOpener();
                var model = new LibraryViewModel(dbPath, host, ownsPlugins: false);
                var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: false);
                var window = new Window { Width = 1200, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);

                    var list = view.FindControl<ListBox>("GameList")!;
                    var play = view.FindControl<Button>("PlayButton")!;
                    list.SelectedIndex = 0; // AAA Uri Game
                    await view.SelectionTask;
                    Assert.True(play.IsEnabled);

                    play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.PlayTask;
                    Assert.Equal(["customstore://launch/uri-1"], Assert.Single(opener.Requests).Arguments);

                    // Select missing-exe tracked plugin game: GameStarting AND GameStopped must both fire even on launch failure.
                    list.SelectedIndex = 1;
                    await view.SelectionTask;
                    Assert.True(play.IsEnabled);
                    play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.PlayTask;

                    Assert.Equal([PluginEvents.GameStarting, PluginEvents.GameStopped], hookPlugin.SessionEvents);
                }
                finally
                {
                    window.Close();
                    model.Dispose();
                }
            }, token);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private sealed class FakeOpener : ISteamUriOpener
    {
        public List<LaunchRequest> Requests { get; } = [];

        public Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(0);
        }
    }

    private sealed class CustomArcadePlugin(Guid sourceId)
        : IPlaynitePlugin, ILibrarySourceCapability, ILaunchCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            "test.custom-arcade",
            sourceId,
            "Arcade Core",
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Core | PluginSurface.Desktop,
            PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch,
            new PluginEntryPoints());

        public LibrarySourceInfo Source { get; } = new(
            sourceId,
            "Arcade Core",
            "Arcade",
            DefaultStatusLabel: "Ready to play");

        public Guid SourceId => sourceId;

        public PluginRegistration Setup(PluginContext context) => new()
        {
            LibrarySources = [this],
            LaunchProviders = [this],
        };

        public PluginLaunchSpec DescribeLaunch(Game game) =>
            new(PluginLaunchKind.TrackedProcess, "Arcade Runner");

        public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null) =>
            new(PluginLaunchKind.TrackedProcess, "Arcade Runner", "/usr/bin/true", ["--rom", game.GameId ?? string.Empty]);
    }

    private sealed class UnsafeShellPlugin(Guid sourceId)
        : IPlaynitePlugin, ILibrarySourceCapability, ILaunchCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            "test.unsafe-shell",
            sourceId,
            "Unsafe Shell",
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Core | PluginSurface.Desktop,
            PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch,
            new PluginEntryPoints());

        public LibrarySourceInfo Source { get; } = new(sourceId, "Unsafe Shell", "Unsafe Shell");
        public Guid SourceId => sourceId;

        public PluginRegistration Setup(PluginContext context) => new()
        {
            LibrarySources = [this],
            LaunchProviders = [this],
        };

        public PluginLaunchSpec DescribeLaunch(Game game) =>
            new(PluginLaunchKind.TrackedProcess, "Shell");

        public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null) =>
            new(PluginLaunchKind.TrackedProcess, "Shell", "/bin/sh", ["-c", "echo hi"]);
    }

    private sealed class MissingExePlugin(Guid sourceId)
        : IPlaynitePlugin, ILibrarySourceCapability, ILaunchCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            "test.missing-exe",
            sourceId,
            "Missing Exe",
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Core | PluginSurface.Desktop,
            PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch,
            new PluginEntryPoints());

        public LibrarySourceInfo Source { get; } = new(sourceId, "Missing Exe", "Missing Exe");
        public Guid SourceId => sourceId;

        public PluginRegistration Setup(PluginContext context) => new()
        {
            LibrarySources = [this],
            LaunchProviders = [this],
        };

        public PluginLaunchSpec DescribeLaunch(Game game) =>
            new(PluginLaunchKind.TrackedProcess, "Missing Runner");

        public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null) =>
            new(PluginLaunchKind.TrackedProcess, "Missing Runner", "/nonexistent/playnite-missing-plugin-exe");
    }

    private sealed class UriAndEventPlugin(Guid sourceId)
        : IPlaynitePlugin, ILibrarySourceCapability, ILaunchCapability, IEventHookCapability
    {
        public List<string> SessionEvents { get; } = [];

        public PluginDescriptor Descriptor { get; } = new(
            "test.uri-event",
            sourceId,
            "Custom Store",
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Core | PluginSurface.Desktop,
            PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch | PluginCapabilityKind.EventHooks,
            new PluginEntryPoints());

        public LibrarySourceInfo Source { get; } = new(sourceId, "Custom Store", "Custom Store");
        public Guid SourceId => sourceId;
        public IReadOnlyList<string> SubscribedEvents => [PluginEvents.GameStarting, PluginEvents.GameStopped];

        public PluginRegistration Setup(PluginContext context) => new()
        {
            LibrarySources = [this],
            LaunchProviders = [this],
            EventHooks = [this],
        };

        public PluginLaunchSpec DescribeLaunch(Game game) =>
            new(PluginLaunchKind.UriHandoff, "Custom Store URI", Uri: $"customstore://launch/{game.GameId}");

        public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null) =>
            DescribeLaunch(game);

        public Task OnEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
        {
            SessionEvents.Add(pluginEvent.Name);
            return Task.CompletedTask;
        }
    }
}
