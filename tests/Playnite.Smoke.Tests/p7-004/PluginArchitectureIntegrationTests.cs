using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Plugins;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Plugins;
using Playnite.Desktop.Shell;
using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Sessions;
using Playnite.Steam;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class PluginArchitectureIntegrationTests
{
    [Fact]
    public void Default_desktop_plugin_host_registers_steam_heroic_luna_and_snes_builtins()
    {
        using var host = DesktopPluginHostFactory.CreateDefault();

        Assert.True(host.TryGetSource(StoreCatalog.SteamPluginId, out var steamSource));
        Assert.Equal("Steam", steamSource.DisplayName);
        Assert.True(steamSource.SupportsSync);
        Assert.True(host.TryGetSyncProvider(SteamPlugin.PluginId, out _));

        Assert.True(host.TryGetSource(StoreCatalog.EpicPluginId, out var epicSource));
        Assert.Equal("Epic", epicSource.DisplayName);
        Assert.True(host.TryGetSource(StoreCatalog.GogPluginId, out var gogSource));
        Assert.Equal("GOG", gogSource.DisplayName);
        Assert.True(host.TryGetSource(StoreCatalog.AmazonPluginId, out var amazonSource));
        Assert.Equal("Amazon", amazonSource.DisplayName);
        Assert.True(host.TryGetSyncProvider(HeroicPlugin.PluginId, out _));

        Assert.True(host.TryGetSource(StoreCatalog.LunaPluginId, out var lunaSource));
        Assert.Equal("Amazon Luna", lunaSource.DisplayName);
        Assert.True(lunaSource.IsCloudStreaming);
        Assert.Equal("Cloud streaming", lunaSource.DefaultStatusLabel);

        Assert.True(host.TryGetSource(SnesRomProfile.SourceId, out var snesSource));
        Assert.Equal("SNES", snesSource.DisplayName);
        Assert.False(snesSource.IsCloudStreaming);

        Assert.NotEmpty(host.GetSettingsSchema(SteamPlugin.PluginId));
        Assert.NotEmpty(host.GetSettingsSchema(HeroicPlugin.PluginId));
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

            var overrideGameId = Guid.NewGuid();

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
                await session.Games.AddAsync(new StoredGame(new Game(overrideGameId, "uri-override-1", "ZZZ2 Uri File Override")
                {
                    PluginId = uriSourceId,
                    IsInstalled = true,
                    GameActions =
                    [
                        new GameAction
                        {
                            Name = "Custom Runner",
                            Type = GameActionType.File,
                            IsPlayAction = true,
                            Path = "/usr/bin/true",
                        },
                    ],
                }));
                return true;
            }, token);

            var identity = new ProcessIdentity(88124, 99999, 88124);
            await using var sleepInhibition = new SessionSleepInhibition(new NoopSleepInhibitionService());
            await using var fakeLauncher = new FakeTestProcessLauncher(identity);
            await using var sessionManager = new DesktopGameSessionManager(
                fakeLauncher,
                new FakeExitedProcessObserver(identity),
                sessionDelay: null,
                TimeProvider.System,
                sleepInhibition,
                new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(10), ExitConfirmationSamples = 1 });

            await WithHeadlessUiAsync(async () =>
            {
                using var host = DesktopPluginHostFactory.CreateDefault();
                var hookPlugin = new UriAndEventPlugin(uriSourceId);
                Assert.True(host.RegisterBuiltIn(hookPlugin));
                Assert.True(host.RegisterBuiltIn(new MissingExePlugin(trackedSourceId)));

                var opener = new FakeOpener();
                var model = new LibraryViewModel(dbPath, host, ownsPlugins: false);
                var view = new LibraryGridView(
                    new SteamPlayRequest(opener),
                    fullscreenPreview: false,
                    steamSync: null,
                    artworkLoaderFactory: null,
                    sessionManager: sessionManager);
                var window = new Window { Width = 1200, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);

                    var list = view.FindControl<ListBox>("GameList")!;
                    var play = view.FindControl<Button>("PlayButton")!;
                    var detailsLaunchTarget = view.FindControl<TextBlock>("DetailsLaunchTarget")!;
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

                    // UriHandoff game with a custom File PlayAction override must run the tracked session instead of opener.
                    hookPlugin.SessionEvents.Clear();
                    list.SelectedIndex = 2;
                    await view.SelectionTask;
                    Assert.True(play.IsEnabled);
                    Assert.Contains("/usr/bin/true", detailsLaunchTarget.Text, StringComparison.Ordinal);
                    play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.PlayTask;
                    Assert.Single(opener.Requests);
                    Assert.Contains(fakeLauncher.LaunchedRequests, req => req.Executable == "/usr/bin/true");
                    Assert.Equal([PluginEvents.GameStarting, PluginEvents.GameStopped], hookPlugin.SessionEvents);
                }
                finally
                {
                    window.Close();
                    await view.UnloadTask;
                    model.Dispose();
                }
            }, token);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Disabling_steam_or_heroic_blocks_play_and_sync_and_dynamic_source_combo_filters_custom_plugins()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-plugin-iter2-");
        try
        {
            var customSourceId = Guid.Parse("d8888888-8888-8888-8888-888888888888");
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "220", "Half-Life 2")
                {
                    PluginId = StoreCatalog.SteamPluginId,
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "Celeste", "Celeste")
                {
                    PluginId = StoreCatalog.EpicPluginId,
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "arcade-1", "Retro Blaster")
                {
                    PluginId = customSourceId,
                    IsInstalled = true,
                }));
                return true;
            }, token);

            await WithHeadlessUiAsync(async () =>
            {
                var syncEvents = new List<string>();
                using var host = DesktopPluginHostFactory.CreateDefault(
                    steamSync: new StubSteamSync(),
                    heroicSync: new StubHeroicSync());
                Assert.True(host.RegisterBuiltIn(new CustomArcadePlugin(customSourceId, supportsSync: true)));
                Assert.True(host.RegisterBuiltIn(new SyncEventRecorderPlugin(syncEvents)));

                var model = new LibraryViewModel(dbPath, host, ownsPlugins: false);
                var view = new LibraryGridView(new SteamPlayRequest(new FakeOpener()), fullscreenPreview: false);
                var window = new Window { Width = 1200, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);

                    Assert.Equal(3, model.Rows.Count);
                    Assert.True(Assert.Single(model.Rows, r => r.Name == "Half-Life 2").CanPlay);
                    Assert.True(Assert.Single(model.Rows, r => r.Name == "Celeste").CanPlay);

                    // Dynamic SourceCombo keeps canonical indices 0..7 pinned and appends custom/sync plugins at 8+.
                    var sourceCombo = view.FindControl<ComboBox>("SourceCombo")!;
                    var options = sourceCombo.Items.Cast<LibrarySourceFilterOption>().ToArray();
                    Assert.True(options.Length >= 9);
                    Assert.Equal("All stores", options[0].Label);
                    Assert.Equal("Epic", options[2].Label);
                    Assert.Equal("Custom", options[5].Label);
                    Assert.Equal("Amazon Luna", options[6].Label);
                    Assert.Equal("SNES", options[7].Label);
                    var customIndex = Array.FindIndex(options, o => o.PluginId == customSourceId);
                    Assert.True(customIndex >= 8);

                    sourceCombo.SelectedIndex = customIndex;
                    await view.QueryTask;
                    var filtered = Assert.Single(model.Rows);
                    Assert.Equal("Retro Blaster", filtered.Name);
                    Assert.True(model.HasActiveFilters);
                    Assert.Contains("Source: Arcade", model.QueryStatus, StringComparison.Ordinal);

                    // Run built-in Steam and Heroic plugin sync through PluginHost and verify LibrarySynced events + summaries.
                    view.FindControl<Button>("SyncSteamButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.SyncTask;
                    Assert.Equal([PluginEvents.LibrarySynced], syncEvents);
                    Assert.Contains("Steam sync complete: 1 added", view.FindControl<TextBlock>("SyncStatus")!.Text);

                    view.FindControl<Button>("SyncHeroicButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.SyncTask;
                    Assert.Equal(2, syncEvents.Count);
                    Assert.Equal(PluginEvents.LibrarySynced, syncEvents[1]);
                    Assert.Contains("Heroic sync complete: 1 added", view.FindControl<TextBlock>("SyncStatus")!.Text);

                    // Disabling an actively selected custom source plugin resets SourceFilter/SourceCombo back to All.
                    Assert.True(host.Disable("test.custom-arcade"));
                    await model.ReloadCurrentPageAsync(token);
                    view.Apply(model);
                    Assert.False(model.HasActiveFilters);
                    Assert.Null(model.SourcePluginFilterId);
                    Assert.Equal(0, sourceCombo.SelectedIndex);
                    Assert.Equal(3, model.Rows.Count);

                    // Disabling SteamPlugin & HeroicPlugin blocks Play and sync without touching SQLite.
                    Assert.True(host.Disable(SteamPlugin.PluginId));
                    Assert.True(host.Disable(HeroicPlugin.PluginId));
                    Assert.False(host.TryGetSyncProvider(SteamPlugin.PluginId, out _));
                    Assert.False(host.TryGetSyncProvider(HeroicPlugin.PluginId, out _));

                    await model.ResetFiltersAsync(token);
                    Assert.False(Assert.Single(model.Rows, r => r.Name == "Half-Life 2").CanPlay);
                    Assert.False(Assert.Single(model.Rows, r => r.Name == "Celeste").CanPlay);
                }
                finally
                {
                    window.Close();
                    await view.UnloadTask;
                    model.Dispose();
                }
            }, token);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Startup_external_discovery_finds_plugins_in_library_directory_lazily()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-startup-plugins-");
        try
        {
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");

            // Relative external roots / env var paths must be rejected to prevent CWD-relative hijacking.
            Assert.Null(DesktopPluginHostFactory.ResolvePluginsDirectory(dbPath, "relative/plugins"));
            var originalEnv = Environment.GetEnvironmentVariable(DesktopPluginHostFactory.PluginsDirectoryVariable);
            try
            {
                Environment.SetEnvironmentVariable(DesktopPluginHostFactory.PluginsDirectoryVariable, "relative-plugins-env");
                Assert.Null(DesktopPluginHostFactory.ResolvePluginsDirectory(dbPath));
            }
            finally
            {
                Environment.SetEnvironmentVariable(DesktopPluginHostFactory.PluginsDirectoryVariable, originalEnv);
            }

            var pluginDir = Path.Combine(tempDir.FullName, "plugins", "smoke-ext");
            Directory.CreateDirectory(pluginDir);
            var copiedDll = "SmokeExternalSample.dll";
            File.Copy(typeof(SmokeExternalPlugin).Assembly.Location, Path.Combine(pluginDir, copiedDll));
            File.WriteAllText(
                Path.Combine(pluginDir, "plugin.json"),
                $$"""
                {
                  "id": "smoke.external",
                  "sourceId": "{{SmokeExternalPlugin.ExternalSourceId:D}}",
                  "name": "Smoke External",
                  "version": "1.0.0",
                  "surfaces": ["desktop"],
                  "capabilities": ["librarySource", "launch"],
                  "entryPoints": {
                    "desktop": "{{copiedDll}}",
                    "entryType": "{{typeof(SmokeExternalPlugin).FullName}}"
                  }
                }
                """);

            using (var host = DesktopPluginHostFactory.CreateForLibrary(dbPath))
            {
                var recordBefore = Assert.Single(host.GetRecords(), r => r.Descriptor.Id == "smoke.external");
                Assert.False(recordBefore.IsMaterialized);
                Assert.Equal(PluginLifecycleState.Registered, recordBefore.State);

                Assert.True(host.TryGetSource(SmokeExternalPlugin.ExternalSourceId, out var source));
                Assert.Equal("Smoke External Source", source.DisplayName);
                var recordAfter = Assert.Single(host.GetRecords(), r => r.Descriptor.Id == "smoke.external");
                Assert.True(recordAfter.IsMaterialized);
                Assert.Equal(PluginLifecycleState.Active, recordAfter.State);
            }

            // LibraryViewModel(dbPath) + SourceCombo population must NOT materialize external librarySource plugins on startup.
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            Assert.NotNull(database);
            await WithHeadlessUiAsync(async () =>
            {
                var model = new LibraryViewModel(dbPath);
                var view = new LibraryGridView(new SteamPlayRequest(new FakeOpener()), fullscreenPreview: false);
                var window = new Window { Width = 1200, Height = 720, Content = view };
                try
                {
                    var initialRecord = Assert.Single(model.Plugins.GetRecords(), r => r.Descriptor.Id == "smoke.external");
                    Assert.False(initialRecord.IsMaterialized);
                    Assert.Equal(PluginLifecycleState.Registered, initialRecord.State);

                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);

                    var options = view.FindControl<ComboBox>("SourceCombo")!
                        .Items
                        .Cast<LibrarySourceFilterOption>()
                        .ToArray();
                    var externalIndex = Array.FindIndex(options, o => o.PluginId == SmokeExternalPlugin.ExternalSourceId);
                    Assert.True(externalIndex >= 8);

                    var afterUiRecord = Assert.Single(model.Plugins.GetRecords(), r => r.Descriptor.Id == "smoke.external");
                    Assert.False(afterUiRecord.IsMaterialized);
                    Assert.Equal(PluginLifecycleState.Registered, afterUiRecord.State);

                    Assert.True(model.Plugins.TryGetSource(SmokeExternalPlugin.ExternalSourceId, out var loadedSource));
                    Assert.Equal("Smoke External Source", loadedSource.DisplayName);
                    Assert.True(Assert.Single(model.Plugins.GetRecords(), r => r.Descriptor.Id == "smoke.external").IsMaterialized);
                }
                finally
                {
                    window.Close();
                    await view.UnloadTask;
                    model.Dispose();
                }
            }, token);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static async Task WithHeadlessUiAsync(Func<Task> action, CancellationToken token)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
        await session.Dispatch(() =>
        {
            var frame = new DispatcherFrame();
            var task = action();
            if (!task.IsCompleted)
            {
                using var registration = token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
                _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.UIThread.PushFrame(frame);
            }

            task.GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
        }, token);
    }

    private sealed class NoopSleepInhibitionService : ISleepInhibitionService
    {
        public Task<SleepInhibitionLease> InhibitAsync(
            SleepInhibitionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SleepInhibitionLease.NotHeld(
                DesktopCapability.Unavailable(DesktopFacility.SleepInhibition, "Not needed in smoke test.")));
    }

    private sealed class FakeTestProcessLauncher(ProcessIdentity identity) : IProcessLauncher
    {
        public List<LaunchRequest> LaunchedRequests { get; } = [];

        public Task<IGameProcess> StartAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            LaunchedRequests.Add(request);
            if (!File.Exists(request.Executable))
            {
                throw new FileNotFoundException($"Missing executable: {request.Executable}", request.Executable);
            }

            return Task.FromResult<IGameProcess>(new FakeGameProcess(identity, DateTimeOffset.UtcNow));
        }

        public Task<bool> IsRunningAsync(IGameProcess process, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task StopAsync(IGameProcess process, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(IGameProcess process, IReadOnlyCollection<ProcessIdentity> trackedProcesses, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class FakeGameProcess(ProcessIdentity identity, DateTimeOffset startedAt) : IGameProcess
        {
            public ProcessIdentity Identity { get; } = identity;
            public DateTimeOffset StartedAt { get; } = startedAt;
        }
    }

    private sealed class FakeExitedProcessObserver(ProcessIdentity identity) : IProcessObserver
    {
        private int calls;

        public Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref calls);
            return count == 1
                ? Task.FromResult(new ProcessSnapshot([new ProcessEntry(identity, 1, true)], IsComplete: true))
                : Task.FromResult(new ProcessSnapshot([], IsComplete: true));
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

    private sealed class CustomArcadePlugin(Guid sourceId, bool supportsSync = false)
        : IPlaynitePlugin, ILibrarySourceCapability, ILaunchCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            "test.custom-arcade",
            sourceId,
            "Arcade Core",
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Core | PluginSurface.Desktop,
            supportsSync
                ? PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch | PluginCapabilityKind.LibrarySync
                : PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch,
            new PluginEntryPoints());

        public LibrarySourceInfo Source { get; } = new(
            sourceId,
            "Arcade Core",
            "Arcade",
            DefaultStatusLabel: "Ready to play",
            SupportsSync: supportsSync);

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

    private sealed class StubSteamSync : ILibrarySteamSync
    {
        public Task<Playnite.Steam.Sync.SteamSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken) =>
            Task.FromResult(new Playnite.Steam.Sync.SteamSyncResult(
                Added: 1,
                Updated: 0,
                Removed: 0,
                Unchanged: 0,
                Skipped: [],
                Issues: [],
                Reconciled: true));
    }

    private sealed class StubHeroicSync : ILibraryHeroicSync
    {
        public Task<HeroicSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken) =>
            Task.FromResult(new HeroicSyncResult(1, 0, 0, 0, 0, [], Reconciled: true, RootFound: true));
    }

    private sealed class SyncEventRecorderPlugin(List<string> events) : IPlaynitePlugin, IEventHookCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            "test.sync-recorder",
            Guid.Empty,
            "Sync Recorder",
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Desktop,
            PluginCapabilityKind.EventHooks,
            new PluginEntryPoints());

        public IReadOnlyList<string> SubscribedEvents => [PluginEvents.LibrarySynced];

        public PluginRegistration Setup(PluginContext context) => new()
        {
            EventHooks = [this],
        };

        public Task OnEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
        {
            events.Add(pluginEvent.Name);
            return Task.CompletedTask;
        }
    }
}

public sealed class SmokeExternalPlugin : IPlaynitePlugin, ILibrarySourceCapability, ILaunchCapability
{
    public static readonly Guid ExternalSourceId = Guid.Parse("d9999999-9999-9999-9999-999999999999");

    public PluginDescriptor Descriptor { get; } = new(
        "smoke.external",
        ExternalSourceId,
        "Smoke External",
        new Version(1, 0, 0),
        PluginLoadKind.ExternalAssembly,
        PluginSurface.Desktop,
        PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch,
        new PluginEntryPoints(Desktop: "SmokeExternalSample.dll", EntryType: typeof(SmokeExternalPlugin).FullName));

    public LibrarySourceInfo Source { get; } = new(ExternalSourceId, "Smoke External Source", "Smoke External");

    public Guid SourceId => ExternalSourceId;

    public PluginRegistration Setup(PluginContext context) => new()
    {
        LibrarySources = [this],
        LaunchProviders = [this],
    };

    public PluginLaunchSpec DescribeLaunch(Game game) =>
        new(PluginLaunchKind.TrackedProcess, "Smoke External Runner");

    public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null) =>
        new(PluginLaunchKind.TrackedProcess, "Smoke External Runner", "/usr/bin/true");
}
