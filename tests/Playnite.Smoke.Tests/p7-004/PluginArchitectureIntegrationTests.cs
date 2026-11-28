using System.Net;
using System.Net.Http;
using System.Text;
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

        Assert.True(host.HasUiSlotProviders(PluginUiSlots.CardBadge));
        Assert.True(host.HasUiSlotProviders(PluginUiSlots.DetailsBadges));
        Assert.True(host.HasUiSlotProviders(PluginUiSlots.DetailsSection));
        Assert.True(host.TryGetMetadataProvider(ProtonDbPlugin.ProviderIdentifier, out _));
        Assert.NotEmpty(host.GetSettingsSchema(ProtonDbPlugin.PluginId));
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

    [Fact]
    public async Task ProtonDbPlugin_renders_badges_sections_persists_settings_and_handles_http_errors()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-protondb-");
        try
        {
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "220", "Half-Life 2")
                {
                    PluginId = StoreCatalog.SteamPluginId,
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "500", "Portal Offline")
                {
                    PluginId = StoreCatalog.SteamPluginId,
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "snes-1", "Super Mario World")
                {
                    PluginId = SnesRomProfile.SourceId,
                    IsInstalled = true,
                }));
                return true;
            }, token);

            var requests = new List<string>();
            var returnServerErrorFor500 = true;
            var clock = new TestMutableTimeProvider(new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero));
            using var httpClient = new HttpClient(new StubHttpMessageHandler((req, cancellationToken) =>
            {
                var uri = req.RequestUri!.ToString();
                requests.Add(uri);
                if (uri.Contains("/220.json", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"tier":"platinum","bestReportedTier":"native","confidence":"strong","total":412}""",
                            Encoding.UTF8,
                            "application/json"),
                    });
                }

                if (uri.Contains("/500.json", StringComparison.Ordinal))
                {
                    if (returnServerErrorFor500)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                    }

                    // BOM-prefixed response proves BOM handling & post-backoff recovery.
                    var bomJson = Encoding.UTF8.GetPreamble()
                        .Concat(Encoding.UTF8.GetBytes("""{"tier":"gold","confidence":"good","total":88}"""))
                        .ToArray();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bomJson),
                    });
                }

                if (uri.Contains("/999999.json", StringComparison.Ordinal))
                {
                    throw new OperationCanceledException("Simulated HTTP timeout", cancellationToken);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"tier":"native"}""", Encoding.UTF8, "application/json"),
                });
            }));

            // Offline by default: opt-in HTTP is not contacted until onlineLookup=true, and Setup writes no disk settings eagerly.
            using (var hostOffline = DesktopPluginHostFactory.CreateForLibrary(dbPath, timeProvider: clock, protonDbClient: httpClient))
            {
                Assert.False(File.Exists(Path.Combine(tempDir.FullName, "plugin-settings", $"{ProtonDbPlugin.PluginId}.json")));
                await WithHeadlessUiAsync(async () =>
                {
                    var opener = new FakeOpener();
                    var model = new LibraryViewModel(dbPath, hostOffline, ownsPlugins: false);
                    var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: false);
                    var window = new Window { Width = 1200, Height = 720, Content = view };
                    try
                    {
                        await model.LoadAsync(token);
                        window.Show();
                        view.Apply(model);
                        await view.CardSlotsTask;
                        await view.DetailsSlotsTask;

                        Assert.Empty(requests);
                        var badgePanel = view.FindControl<WrapPanel>("DetailsPluginBadges")!;
                        Assert.False(badgePanel.IsVisible);
                    }
                    finally
                    {
                        window.Close();
                        await view.UnloadTask;
                        model.Dispose();
                    }
                }, token);

                // Opt in to online lookup and fetch 220 via selection (CardBadge stays cache-only).
                hostOffline.SettingsStore.Set(ProtonDbPlugin.PluginId, ProtonDbPlugin.OnlineLookupSettingKey, "true");
                await WithHeadlessUiAsync(async () =>
                {
                    var opener = new FakeOpener();
                    var model = new LibraryViewModel(dbPath, hostOffline, ownsPlugins: false);
                    var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: false);
                    var window = new Window { Width = 1200, Height = 720, Content = view };
                    try
                    {
                        await model.LoadAsync(token);
                        window.Show();
                        view.Apply(model);
                        await view.CardSlotsTask;
                        await view.DetailsSlotsTask;

                        Assert.Single(requests);
                        Assert.Contains(hostOffline.GetRecords(), r => r.Descriptor.Id == ProtonDbPlugin.PluginId && r.State == PluginLifecycleState.Active);

                        // Reload page now renders card.badge for 220 directly from cache without additional HTTP calls.
                        view.Apply(model);
                        await view.CardSlotsTask;
                        await view.DetailsSlotsTask;
                        Assert.Single(requests);

                        var cardItem = (LibraryCardItem)((ListBoxItem)view.FindControl<ListBox>("GameList")!.Items[0]!).DataContext!;
                        var cardBadge = Assert.Single(cardItem.PluginBadges);
                        Assert.Equal("Platinum", cardBadge.Label);
                        Assert.True(cardItem.HasPluginBadges);

                        var badgePanel = view.FindControl<WrapPanel>("DetailsPluginBadges")!;
                        var sectionPanel = view.FindControl<StackPanel>("DetailsPluginSections")!;
                        Assert.True(badgePanel.IsVisible);
                        Assert.True(sectionPanel.IsVisible);
                        var badgeButton = Assert.IsType<Button>(Assert.Single(badgePanel.Children));
                        badgeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await view.OpenLinkTask;
                        Assert.Equal(["https://www.protondb.com/app/220"], Assert.Single(opener.Requests).Arguments);

                        var metaResults = await hostOffline.FetchMetadataAsync(
                            new PluginGameSnapshot(model.Rows[0].Id, "Half-Life 2", "220", StoreCatalog.SteamPluginId, true),
                            token);
                        var meta = Assert.Single(metaResults);
                        Assert.Equal("platinum", meta.Attributes!["tier"]);
                        Assert.Equal("native", meta.Attributes["bestReportedTier"]);

                        // Non-Steam game gets no ProtonDB badge or section.
                        var snesItem = (LibraryCardItem)((ListBoxItem)view.FindControl<ListBox>("GameList")!.Items[2]!).DataContext!;
                        Assert.Empty(snesItem.PluginBadges);

                        // HTTP 500 error enters backoff without re-requesting before expiry or faulting plugin.
                        var list = view.FindControl<ListBox>("GameList")!;
                        list.SelectedIndex = 1; // Portal Offline (500)
                        await view.SelectionTask;
                        await view.DetailsSlotsTask;
                        Assert.Equal(2, requests.Count);
                        Assert.False(badgePanel.IsVisible);
                        Assert.False(sectionPanel.IsVisible);

                        // Advance clock past 5-minute backoff and recover with BOM-prefixed 200 JSON.
                        clock.Advance(TimeSpan.FromMinutes(6));
                        returnServerErrorFor500 = false;
                        list.SelectedIndex = 0;
                        await view.SelectionTask;
                        await view.DetailsSlotsTask;
                        list.SelectedIndex = 1;
                        await view.SelectionTask;
                        await view.DetailsSlotsTask;
                        Assert.Equal(3, requests.Count);
                        Assert.True(badgePanel.IsVisible);
                        Assert.Contains("Gold", Assert.IsType<TextBlock>(Assert.IsType<Button>(Assert.Single(badgePanel.Children)).Content!).Text);

                        // Timeout OperationCanceledException does not fault ProtonDbPlugin.
                        var timeoutResults = await hostOffline.ResolveUiSlotAsync(
                            PluginUiSlots.DetailsBadges,
                            new PluginGameSnapshot(Guid.NewGuid(), "Timeout Game", "999999", StoreCatalog.SteamPluginId, true),
                            token);
                        Assert.Empty(timeoutResults);
                        Assert.Contains(hostOffline.GetRecords(), r => r.Descriptor.Id == ProtonDbPlugin.PluginId && r.State == PluginLifecycleState.Active);

                        // Concurrent uncached requests for appId 730 coalesce into a single HTTP request.
                        requests.Clear();
                        var snap730 = new PluginGameSnapshot(Guid.NewGuid(), "Counter-Strike", "730", StoreCatalog.SteamPluginId, true);
                        await Task.WhenAll(
                            hostOffline.ResolveUiSlotAsync(PluginUiSlots.DetailsBadges, snap730, token),
                            hostOffline.ResolveUiSlotAsync(PluginUiSlots.DetailsSection, snap730, token),
                            hostOffline.FetchMetadataAsync(snap730, token));
                        Assert.Single(requests);

                        // Disabling via EnabledSettingKey hides badges without faulting.
                        hostOffline.SettingsStore.Set(ProtonDbPlugin.PluginId, ProtonDbPlugin.EnabledSettingKey, "false");
                        view.Apply(model);
                        await view.CardSlotsTask;
                        await view.DetailsSlotsTask;
                        Assert.False(badgePanel.IsVisible);
                        hostOffline.SettingsStore.Set(ProtonDbPlugin.PluginId, ProtonDbPlugin.EnabledSettingKey, "true");
                    }
                    finally
                    {
                        window.Close();
                        await view.UnloadTask;
                        model.Dispose();
                    }
                }, token);
            }

            // Second host instance with onlineLookup still false reads persisted protondb-cache.json from disk with zero HTTP calls.
            requests.Clear();
            using var hostReloaded = DesktopPluginHostFactory.CreateForLibrary(dbPath, timeProvider: clock, protonDbClient: httpClient);
            hostReloaded.SettingsStore.Set(ProtonDbPlugin.PluginId, ProtonDbPlugin.OnlineLookupSettingKey, "false");
            Assert.True(File.Exists(Path.Combine(tempDir.FullName, "plugin-settings", $"{ProtonDbPlugin.PluginId}.json")));
            Assert.True(File.Exists(Path.Combine(tempDir.FullName, "plugin-settings", ProtonDbPlugin.CacheFileName)));

            await WithHeadlessUiAsync(async () =>
            {
                var opener = new FakeOpener();
                var model = new LibraryViewModel(dbPath, hostReloaded, ownsPlugins: false);
                var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: false);
                var window = new Window { Width = 1200, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);
                    await view.CardSlotsTask;
                    await view.DetailsSlotsTask;

                    Assert.Empty(requests);
                    var cardItem = (LibraryCardItem)((ListBoxItem)view.FindControl<ListBox>("GameList")!.Items[0]!).DataContext!;
                    Assert.Equal("Platinum", Assert.Single(cardItem.PluginBadges).Label);
                    Assert.True(view.FindControl<WrapPanel>("DetailsPluginBadges")!.IsVisible);
                    Assert.True(view.FindControl<StackPanel>("DetailsPluginSections")!.IsVisible);

                    // Disabling ProtonDbPlugin hides badges and sections cleanly on refresh.
                    Assert.True(hostReloaded.Disable(ProtonDbPlugin.PluginId));
                    view.Apply(model);
                    await view.CardSlotsTask;
                    await view.DetailsSlotsTask;

                    var clearedCard = (LibraryCardItem)((ListBoxItem)view.FindControl<ListBox>("GameList")!.Items[0]!).DataContext!;
                    Assert.Empty(clearedCard.PluginBadges);
                    Assert.False(view.FindControl<WrapPanel>("DetailsPluginBadges")!.IsVisible);
                    Assert.False(view.FindControl<StackPanel>("DetailsPluginSections")!.IsVisible);
                }
                finally
                {
                    window.Close();
                    await view.UnloadTask;
                    model.Dispose();
                }
            }, token);

            // Caching > 260 Steam apps persists to protondb-cache.json without exhausting FilePluginSettingsStore's 256-key cap.
            Assert.True(hostReloaded.Enable(ProtonDbPlugin.PluginId));
            hostReloaded.SettingsStore.Set(ProtonDbPlugin.PluginId, ProtonDbPlugin.OnlineLookupSettingKey, "true");
            for (var i = 0; i < 265; i++)
            {
                var app = (10000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var bulkMeta = await hostReloaded.FetchMetadataAsync(
                    new PluginGameSnapshot(Guid.NewGuid(), $"Bulk {i}", app, StoreCatalog.SteamPluginId, true),
                    token);
                Assert.Single(bulkMeta);
            }

            hostReloaded.SettingsStore.Set(ProtonDbPlugin.PluginId, "extraSetting", "still-works");
            Assert.Equal("still-works", hostReloaded.SettingsStore.Get(ProtonDbPlugin.PluginId, "extraSetting"));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Custom_ui_slot_plugins_isolate_faults_and_open_sanitized_https_links()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-custom-slots-");
        try
        {
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "custom-1", "AAA Shader Quest")
                {
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "custom-2", "ZZZ Slow Game")
                {
                    IsInstalled = true,
                }));
                return true;
            }, token);

            using var host = DesktopPluginHostFactory.CreateDefault();
            Assert.True(host.RegisterBuiltIn(new HealthyUiSlotPlugin(slowGate)));
            Assert.True(host.RegisterBuiltIn(new ThrowingUiSlotPlugin()));

            await WithHeadlessUiAsync(async () =>
            {
                var opener = new FakeOpener();
                var model = new LibraryViewModel(dbPath, host, ownsPlugins: false);
                var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: false);
                var window = new Window { Width = 1200, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);
                    await view.CardSlotsTask;
                    await view.DetailsSlotsTask;

                    Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.throwing-ui-slot" && r.State == PluginLifecycleState.Faulted);

                    var list = view.FindControl<ListBox>("GameList")!;
                    var cardItem = (LibraryCardItem)((ListBoxItem)list.Items[0]!).DataContext!;
                    Assert.Equal("Deck Verified", Assert.Single(cardItem.PluginBadges).Label);

                    // Toggle to List View and verify list-row renders and preserves PluginBadges cleanly.
                    view.FindControl<Button>("ListViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(((ListBoxItem)list.Items[0]!).Classes.Contains("list-row"));
                    Assert.True(cardItem.HasPluginBadges);
                    view.FindControl<Button>("GridViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    var badgePanel = view.FindControl<WrapPanel>("DetailsPluginBadges")!;
                    var sectionPanel = view.FindControl<StackPanel>("DetailsPluginSections")!;
                    Assert.True(badgePanel.IsVisible);
                    Assert.True(sectionPanel.IsVisible);

                    // Unsafe javascript: badge renders as Border (non-clickable), while safe https: badge renders as Button.
                    var linkBadge = Assert.Single(badgePanel.Children.OfType<Button>());
                    Assert.Single(badgePanel.Children.OfType<Border>());
                    linkBadge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.OpenLinkTask;
                    Assert.Equal(["https://example.com/deck-notes"], opener.Requests[0].Arguments);

                    // DetailsSection renders fields and clickable HTTPS section link button.
                    var sectionBorder = Assert.IsType<Border>(Assert.Single(sectionPanel.Children));
                    var sectionBody = Assert.IsType<StackPanel>(sectionBorder.Child);
                    var sectionButton = sectionBody.Children.OfType<Button>().Single();
                    sectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.OpenLinkTask;
                    Assert.Equal(["https://example.com/deck-report"], opener.Requests[1].Arguments);

                    // Supersession + empty selection: select ZZZ Slow Game, then immediately clear selection before slowGate opens.
                    list.SelectedIndex = 1;
                    await view.SelectionTask;
                    var pendingSlowDetails = view.DetailsSlotsTask;
                    list.SelectedIndex = -1;
                    await view.SelectionTask;
                    slowGate.TrySetResult();
                    await pendingSlowDetails;
                    await view.DetailsSlotsTask;
                    Assert.False(badgePanel.IsVisible);
                    Assert.False(sectionPanel.IsVisible);
                }
                finally
                {
                    slowGate.TrySetResult();
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
    public async Task PluginManagerWindow_configures_settings_lazily_refreshes_ui_and_persists_plugin_state()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-plugin-manager-");
        try
        {
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "220", "Half-Life 2")
                {
                    PluginId = StoreCatalog.SteamPluginId,
                    IsInstalled = true,
                }));
                return true;
            }, token);

            // Broken external manifest under <libraryDir>/plugins exercises discovery diagnostics panel.
            var pluginsRoot = Path.Combine(tempDir.FullName, "plugins");
            var brokenDir = Path.Combine(pluginsRoot, "broken-manifest");
            Directory.CreateDirectory(brokenDir);
            await File.WriteAllTextAsync(Path.Combine(brokenDir, PluginManifestParser.PluginManifestFileName), "{ invalid json", token);

            var requests = new List<string>();
            using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
            {
                var uri = request.RequestUri!.ToString();
                requests.Add(uri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"tier":"platinum","bestReportedTier":"native","confidence":"strong","total":512}""",
                        Encoding.UTF8,
                        "application/json"),
                });
            }));

            var customSourceId = Guid.Parse("f4444444-4444-4444-4444-444444444444");
            var lazySettingsActivations = 0;

            void RegisterCustomTestPlugins(PluginHost targetHost)
            {
                var lazyDesc = new PluginDescriptor(
                    "smoke.lazy-settings",
                    Guid.Empty,
                    "Smoke Lazy Settings",
                    new Version(1, 0, 0),
                    PluginLoadKind.ExternalAssembly,
                    PluginSurface.Desktop,
                    PluginCapabilityKind.Settings,
                    new PluginEntryPoints(Desktop: "smoke-lazy.dll"),
                    Description: "Lazy test plugin with settings.");
                targetHost.RegisterLazy(lazyDesc, () =>
                {
                    lazySettingsActivations++;
                    return new SmokeMultiSettingsPlugin("smoke.lazy-settings", Guid.Empty, "Smoke Lazy Settings", isSource: false);
                });

                Assert.True(targetHost.RegisterBuiltIn(
                    new SmokeMultiSettingsPlugin("smoke.custom-source", customSourceId, "Custom Toggle Source", isSource: true)));
                Assert.True(targetHost.RegisterBuiltIn(
                    new SmokeThrowingSchemaPlugin("smoke.throwing-schema")));
            }

            using (var host1 = DesktopPluginHostFactory.CreateForLibrary(dbPath, protonDbClient: httpClient))
            {
                RegisterCustomTestPlugins(host1);
                await WithHeadlessUiAsync(async () =>
                {
                    var opener = new FakeOpener();
                    var model = new LibraryViewModel(dbPath, host1, ownsPlugins: false);
                    var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: false);
                    var window = new Window { Width = 1200, Height = 720, Content = view };
                    try
                    {
                        await model.LoadAsync(token);
                        window.Show();
                        view.Apply(model);
                        await view.CardSlotsTask;
                        await view.DetailsSlotsTask;

                        // Initially offline: zero HTTP calls, no ProtonDB details badges, custom source present.
                        Assert.Empty(requests);
                        Assert.False(view.FindControl<WrapPanel>("DetailsPluginBadges")!.IsVisible);
                        Assert.Contains(model.SourceFilterOptions, o => o.PluginId == customSourceId);

                        // Open Plugins Manager via toolbar PluginsButton.
                        view.FindControl<Button>("PluginsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        PumpUntil(() => window.OwnedWindows.OfType<PluginManagerWindow>().Any(), token);
                        var manager = window.OwnedWindows.OfType<PluginManagerWindow>().Single();

                        // Host & discovery diagnostics panel surfaces the malformed external manifest.
                        var hostDiagPanel = manager.FindControl<Border>("HostDiagnosticsPanel")!;
                        Assert.True(hostDiagPanel.IsVisible);

                        // Selecting lazy external settings plugin does NOT activate it eagerly.
                        var pluginList = manager.FindControl<ListBox>("PluginList")!;
                        var lazyItem = pluginList.Items.OfType<ListBoxItem>().Single(i => Equals(i.Tag, "smoke.lazy-settings"));
                        pluginList.SelectedItem = lazyItem;
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal(0, lazySettingsActivations);
                        var loadSettingsBtn = manager.FindControl<Button>("LoadSettingsButton")!;
                        Assert.True(loadSettingsBtn.IsVisible);

                        // Clicking Load Settings activates lazily and renders controls.
                        loadSettingsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(1, lazySettingsActivations);
                        Assert.False(loadSettingsBtn.IsVisible);

                        // Select custom built-in settings plugin and test invalid -> valid -> Reset Defaults.
                        var customItem = pluginList.Items.OfType<ListBoxItem>().Single(i => Equals(i.Tag, "smoke.custom-source"));
                        pluginList.SelectedItem = customItem;
                        Dispatcher.UIThread.RunJobs();
                        var fieldsPanel = manager.FindControl<StackPanel>("SettingsFieldsPanel")!;
                        Assert.True(fieldsPanel.IsVisible);

                        var textBoxes = GetDescendants<TextBox>(fieldsPanel).ToList();
                        var checkBoxes = GetDescendants<CheckBox>(fieldsPanel).ToList();
                        var combos = GetDescendants<ComboBox>(fieldsPanel).ToList();
                        Assert.Equal(3, textBoxes.Count); // String, Path, Integer
                        Assert.Single(checkBoxes);
                        Assert.Single(combos);

                        textBoxes[2].Text = "invalid-int";
                        manager.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var statusText = manager.FindControl<TextBlock>("SettingsStatusText")!;
                        Assert.True(statusText.IsVisible);
                        Assert.Contains("invalid", statusText.Text, StringComparison.OrdinalIgnoreCase);
                        Assert.Null(host1.SettingsStore.Get("smoke.custom-source", "limit"));

                        checkBoxes[0].IsChecked = true;
                        textBoxes[0].Text = "custom-value";
                        textBoxes[1].Text = "/games/custom";
                        textBoxes[2].Text = "42";
                        combos[0].SelectedItem = "Beta";
                        manager.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal("Settings saved.", statusText.Text);
                        Assert.Equal("42", host1.SettingsStore.Get("smoke.custom-source", "limit"));

                        manager.FindControl<Button>("ResetDefaultsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal("Defaults restored and saved.", statusText.Text);
                        Assert.False(checkBoxes[0].IsChecked == true);
                        Assert.Equal("default", textBoxes[0].Text);
                        Assert.Equal("/tmp/custom", textBoxes[1].Text);
                        Assert.Equal("10", textBoxes[2].Text);
                        Assert.Equal("Alpha", combos[0].SelectedItem);
                        Assert.Equal("10", host1.SettingsStore.Get("smoke.custom-source", "limit"));
                        Assert.Equal("Alpha", host1.SettingsStore.Get("smoke.custom-source", "mode"));

                        // Selecting an active plugin whose Fields getter throws immediately transitions UI to Faulted + shows diagnostics.
                        var throwingSchemaItem = pluginList.Items.OfType<ListBoxItem>().Single(i => Equals(i.Tag, "smoke.throwing-schema"));
                        pluginList.SelectedItem = throwingSchemaItem;
                        Dispatcher.UIThread.RunJobs();
                        Assert.False(manager.FindControl<CheckBox>("EnabledToggle")!.IsChecked == true);
                        Assert.True(manager.FindControl<Border>("SelectedDiagnosticsPanel")!.IsVisible);
                        Assert.Contains(host1.GetRecords(), r => r.Descriptor.Id == "smoke.throwing-schema" && r.State == PluginLifecycleState.Faulted);

                        // Select ProtonDbPlugin and enable onlineLookup via its checkbox, then save & close.
                        var protonItem = pluginList.Items.OfType<ListBoxItem>().Single(i => Equals(i.Tag, ProtonDbPlugin.PluginId));
                        pluginList.SelectedItem = protonItem;
                        Dispatcher.UIThread.RunJobs();
                        var protonCheckBoxes = GetDescendants<CheckBox>(fieldsPanel).ToList();
                        Assert.Equal(2, protonCheckBoxes.Count);
                        protonCheckBoxes[1].IsChecked = true; // onlineLookup
                        manager.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal("Settings saved.", statusText.Text);

                        manager.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await view.PluginsDialogTask;
                        await view.CardSlotsTask;
                        await view.DetailsSlotsTask;

                        // Closing with changes refreshed LibraryGridView & ProtonDB fetched Platinum rating.
                        Assert.Single(requests);
                        Assert.True(view.FindControl<WrapPanel>("DetailsPluginBadges")!.IsVisible);
                        Assert.True(view.FindControl<StackPanel>("DetailsPluginSections")!.IsVisible);

                        // Select custom source in SourceCombo before disabling it in PluginManagerWindow.
                        var sourceCombo = view.FindControl<ComboBox>("SourceCombo")!;
                        var customOption = model.SourceFilterOptions.Single(o => o.PluginId == customSourceId);
                        sourceCombo.SelectedItem = customOption;
                        await view.QueryTask;
                        Assert.Equal(customSourceId, model.SourcePluginFilterId);

                        // Open Plugins Manager again to disable both ProtonDbPlugin and smoke.custom-source.
                        view.FindControl<Button>("PluginsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        PumpUntil(() => window.OwnedWindows.OfType<PluginManagerWindow>().Any(), token);
                        var manager2 = window.OwnedWindows.OfType<PluginManagerWindow>().Single();
                        var pluginList2 = manager2.FindControl<ListBox>("PluginList")!;
                        var enabledToggle = manager2.FindControl<CheckBox>("EnabledToggle")!;

                        pluginList2.SelectedItem = pluginList2.Items.OfType<ListBoxItem>().Single(i => Equals(i.Tag, ProtonDbPlugin.PluginId));
                        Dispatcher.UIThread.RunJobs();
                        enabledToggle.IsChecked = false;

                        pluginList2.SelectedItem = pluginList2.Items.OfType<ListBoxItem>().Single(i => Equals(i.Tag, "smoke.custom-source"));
                        Dispatcher.UIThread.RunJobs();
                        enabledToggle.IsChecked = false;

                        manager2.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await view.PluginsDialogTask;
                        await view.CardSlotsTask;
                        await view.DetailsSlotsTask;

                        // Disabled source dropped from SourceCombo (resetting active filter to All) and badges/sections cleared.
                        Assert.DoesNotContain(model.SourceFilterOptions, o => o.PluginId == customSourceId);
                        Assert.Equal(0, sourceCombo.SelectedIndex);
                        Assert.False(view.FindControl<WrapPanel>("DetailsPluginBadges")!.IsVisible);
                        Assert.False(view.FindControl<StackPanel>("DetailsPluginSections")!.IsVisible);
                    }
                    finally
                    {
                        window.Close();
                        await view.UnloadTask;
                        model.Dispose();
                    }
                }, token);
            }

            // Fresh host after restart respects persisted Disabled state with zero HTTP lookups and excludes disabled source.
            requests.Clear();
            using var host2 = DesktopPluginHostFactory.CreateForLibrary(dbPath, protonDbClient: httpClient);
            RegisterCustomTestPlugins(host2);
            Assert.Contains(host2.GetRecords(), r => r.Descriptor.Id == ProtonDbPlugin.PluginId && r.State == PluginLifecycleState.Disabled);
            Assert.Contains(host2.GetRecords(), r => r.Descriptor.Id == "smoke.custom-source" && r.State == PluginLifecycleState.Disabled);

            await WithHeadlessUiAsync(async () =>
            {
                var opener = new FakeOpener();
                var model = new LibraryViewModel(dbPath, host2, ownsPlugins: false);
                var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: false);
                var window = new Window { Width = 1200, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);
                    await view.CardSlotsTask;
                    await view.DetailsSlotsTask;

                    Assert.Empty(requests);
                    Assert.DoesNotContain(model.SourceFilterOptions, o => o.PluginId == customSourceId);
                    Assert.False(view.FindControl<WrapPanel>("DetailsPluginBadges")!.IsVisible);
                    Assert.False(view.FindControl<StackPanel>("DetailsPluginSections")!.IsVisible);

                    // Re-enabling disabled source plugin via PluginManagerWindow restores it dynamically in SourceCombo.
                    view.FindControl<Button>("PluginsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => window.OwnedWindows.OfType<PluginManagerWindow>().Any(), token);
                    var manager3 = window.OwnedWindows.OfType<PluginManagerWindow>().Single();
                    var pluginList3 = manager3.FindControl<ListBox>("PluginList")!;
                    pluginList3.SelectedItem = pluginList3.Items.OfType<ListBoxItem>().Single(i => Equals(i.Tag, "smoke.custom-source"));
                    Dispatcher.UIThread.RunJobs();
                    manager3.FindControl<CheckBox>("EnabledToggle")!.IsChecked = true;
                    manager3.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.PluginsDialogTask;
                    Assert.Contains(model.SourceFilterOptions, o => o.PluginId == customSourceId);
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
    public async Task LibraryGridView_plugin_actions_flyouts_execute_commands_lazily_and_refresh_ui()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-plugin-commands-");
        try
        {
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "220", "AAA Half-Life 2")
                {
                    PluginId = StoreCatalog.SteamPluginId,
                    IsInstalled = true,
                }));
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "snes-1", "ZZZ Super Metroid")
                {
                    PluginId = SnesRomProfile.SourceId,
                    IsInstalled = true,
                }));
                return true;
            }, token);

            var clock = new TestMutableTimeProvider(new DateTimeOffset(2026, 11, 27, 20, 0, 0, TimeSpan.Zero));
            var fail220 = true;
            var requests = new List<string>();
            using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
            {
                requests.Add(request.RequestUri!.ToString());
                if (fail220)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"tier":"native","confidence":"strong","total":900}""",
                        Encoding.UTF8,
                        "application/json"),
                });
            }));

            var lazyCommandActivations = 0;
            using var host = DesktopPluginHostFactory.CreateForLibrary(
                dbPath,
                timeProvider: clock,
                steamSync: new StubSteamSync(),
                heroicSync: new StubHeroicSync(),
                protonDbClient: httpClient);
            host.RegisterLazy(
                new PluginDescriptor(
                    "smoke.lazy-command",
                    Guid.Empty,
                    "Smoke Lazy Command",
                    new Version(1, 0, 0),
                    PluginLoadKind.ExternalAssembly,
                    PluginSurface.Desktop,
                    PluginCapabilityKind.Commands,
                    new PluginEntryPoints(Desktop: "smoke-lazy-cmd.dll")),
                () =>
                {
                    lazyCommandActivations++;
                    return new SmokeCommandPlugin("smoke.lazy-command", "smoke.lazy.ping", "Lazy Ping", shouldThrow: false);
                });
            Assert.True(host.RegisterBuiltIn(
                new SmokeCommandPlugin("smoke.throwing-command", "smoke.boom", "Throwing Command", shouldThrow: true)));

            await WithHeadlessUiAsync(async () =>
            {
                var opener = new FakeOpener();
                var model = new LibraryViewModel(dbPath, host, ownsPlugins: false);
                var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: false);
                var window = new Window { Width = 1200, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);
                    await view.CardSlotsTask;
                    await view.DetailsSlotsTask;

                    // Non-materializing visibility check kept lazy command plugin unmaterialized.
                    var toolbarBtn = view.FindControl<Button>("PluginActionsButton")!;
                    var gameBtn = view.FindControl<Button>("GamePluginActionsButton")!;
                    var toolbarFlyout = (MenuFlyout)toolbarBtn.Flyout!;
                    var gameFlyout = (MenuFlyout)gameBtn.Flyout!;
                    Assert.True(toolbarBtn.IsVisible);
                    Assert.True(gameBtn.IsVisible);
                    Assert.Equal(0, lazyCommandActivations);

                    // Steam game selected (index 0): offline ProtonDB refresh gives guidance without HTTP calls.
                    gameFlyout.ShowAt(gameBtn);
                    var refreshItem = gameFlyout.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, ProtonDbPlugin.RefreshRatingCommandId));
                    refreshItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    gameFlyout.Hide();
                    await view.CommandTask;
                    Assert.Empty(requests);
                    Assert.Contains("Enable online ProtonDB", view.FindControl<TextBlock>("PlayStatus")!.Text, StringComparison.Ordinal);

                    // Opt in online, fail once on selection (5m backoff), then recover and click Refresh ProtonDB Rating within backoff window.
                    host.SettingsStore.Set(ProtonDbPlugin.PluginId, ProtonDbPlugin.OnlineLookupSettingKey, "true");
                    view.Apply(model);
                    await view.DetailsSlotsTask;
                    Assert.Single(requests); // Failed 500
                    Assert.False(view.FindControl<WrapPanel>("DetailsPluginBadges")!.IsVisible);

                    fail220 = false;
                    clock.Advance(TimeSpan.FromSeconds(10)); // Still well inside 5-minute backoff.
                    gameFlyout.ShowAt(gameBtn);
                    refreshItem = gameFlyout.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, ProtonDbPlugin.RefreshRatingCommandId));
                    refreshItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    gameFlyout.Hide();
                    await view.CommandTask;
                    await view.DetailsSlotsTask;

                    Assert.Equal(2, requests.Count);
                    Assert.Contains("Native", view.FindControl<TextBlock>("PlayStatus")!.Text, StringComparison.Ordinal);
                    Assert.True(view.FindControl<WrapPanel>("DetailsPluginBadges")!.IsVisible);
                    Assert.True(view.FindControl<StackPanel>("DetailsPluginSections")!.IsVisible);
                    Assert.True(File.Exists(Path.Combine(tempDir.FullName, "plugin-settings", ProtonDbPlugin.CacheFileName)));

                    // Selecting SNES game (index 1) filters out Steam-only ProtonDB Refresh command in GamePluginActionsFlyout.
                    var list = view.FindControl<ListBox>("GameList")!;
                    list.SelectedIndex = 1;
                    await view.SelectionTask;
                    await view.DetailsSlotsTask;
                    gameFlyout.ShowAt(gameBtn);
                    Assert.DoesNotContain(gameFlyout.Items.OfType<MenuItem>(), m => Equals(m.Tag, ProtonDbPlugin.RefreshRatingCommandId));
                    gameFlyout.Hide();
                    Assert.False(gameBtn.IsVisible); // After lazy plugin had no GameContext commands, button hides for non-Steam game.

                    // Switch back to Steam game, reload to see card badge, then Clear ProtonDB Cache via toolbar flyout.
                    list.SelectedIndex = 0;
                    await view.SelectionTask;
                    view.Apply(model);
                    await view.CardSlotsTask;
                    await view.DetailsSlotsTask;
                    Assert.Equal(1, lazyCommandActivations); // Opening game flyout checked lazy Commands capability once.

                    var firstCard = (LibraryCardItem)((ListBoxItem)list.Items[0]!).DataContext!;
                    Assert.Equal("Native", Assert.Single(firstCard.PluginBadges).Label);

                    // Turn offline before clearing cache so RefreshSlots verifies disk removal & cleared badges without re-fetching.
                    host.SettingsStore.Set(ProtonDbPlugin.PluginId, ProtonDbPlugin.OnlineLookupSettingKey, "false");
                    toolbarFlyout.ShowAt(toolbarBtn);
                    var clearCacheItem = toolbarFlyout.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, ProtonDbPlugin.ClearCacheCommandId));
                    clearCacheItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    toolbarFlyout.Hide();
                    await view.CommandTask;
                    await view.CardSlotsTask;
                    await view.DetailsSlotsTask;

                    Assert.False(File.Exists(Path.Combine(tempDir.FullName, "plugin-settings", ProtonDbPlugin.CacheFileName)));
                    Assert.Empty(firstCard.PluginBadges);
                    Assert.Contains("Cleared", view.FindControl<TextBlock>("SyncStatus")!.Text, StringComparison.Ordinal);

                    // Execute lazy command and throwing command cleanly via toolbar flyout.
                    toolbarFlyout.ShowAt(toolbarBtn);
                    var pingItem = toolbarFlyout.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, "smoke.lazy.ping"));
                    pingItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    toolbarFlyout.Hide();
                    await view.CommandTask;
                    Assert.Contains("Lazy pong for library", view.FindControl<TextBlock>("SyncStatus")!.Text, StringComparison.Ordinal);

                    toolbarFlyout.ShowAt(toolbarBtn);
                    var boomItem = toolbarFlyout.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, "smoke.boom"));
                    boomItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    toolbarFlyout.Hide();
                    await view.CommandTask;
                    Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "smoke.throwing-command" && r.State == PluginLifecycleState.Faulted);

                    // Faulted command disappears from flyout; Steam sync command refreshes library cleanly.
                    toolbarFlyout.ShowAt(toolbarBtn);
                    Assert.DoesNotContain(toolbarFlyout.Items.OfType<MenuItem>(), m => Equals(m.Tag, "smoke.boom"));
                    var steamSyncItem = toolbarFlyout.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, SteamPlugin.SyncCommandId));
                    steamSyncItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    toolbarFlyout.Hide();
                    await view.CommandTask;
                    Assert.Contains("Steam sync", view.FindControl<TextBlock>("SyncStatus")!.Text, StringComparison.OrdinalIgnoreCase);
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
    public async Task LibraryGridView_game_flyout_lazy_check_hides_non_matching_and_toggling_providers_updates_visibility()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-cmd-visibility-");
        try
        {
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(dbPath, token);
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "snes-only", "Super Metroid")
                {
                    PluginId = SnesRomProfile.SourceId,
                    IsInstalled = true,
                }));
                return true;
            }, token);

            var lazyCmdLoads = 0;
            const string lazyPluginId = "smoke.lazy-mixed-commands";
            using var host = new PluginHost(PluginSurface.Core | PluginSurface.Desktop);
            host.RegisterBuiltIn(new SnesRomPlugin());
            host.RegisterLazy(
                new PluginDescriptor(
                    lazyPluginId,
                    Guid.Empty,
                    "Lazy Mixed Commands",
                    new Version(1, 0, 0),
                    PluginLoadKind.ExternalAssembly,
                    PluginSurface.Desktop,
                    PluginCapabilityKind.Commands,
                    new PluginEntryPoints(Desktop: "lazy-mixed.dll")),
                () =>
                {
                    lazyCmdLoads++;
                    return new MixedCommandsPlugin(lazyPluginId, StoreCatalog.SteamPluginId);
                });

            await WithHeadlessUiAsync(async () =>
            {
                var opener = new FakeOpener();
                var model = new LibraryViewModel(dbPath, host, ownsPlugins: false);
                var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: false);
                var window = new Window { Width = 1200, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);
                    await view.CardSlotsTask;
                    await view.DetailsSlotsTask;

                    var toolbarBtn = view.FindControl<Button>("PluginActionsButton")!;
                    var gameBtn = view.FindControl<Button>("GamePluginActionsButton")!;
                    var gameFlyout = (MenuFlyout)gameBtn.Flyout!;

                    // Before opening any flyout, lazy Commands-only plugin stays Registered and buttons stay optimistically visible.
                    Assert.True(toolbarBtn.IsVisible);
                    Assert.True(gameBtn.IsVisible);
                    Assert.Equal(0, lazyCmdLoads);

                    // Opening game flyout on a non-Steam game activates lazy Commands plugin, finds zero matching commands, and hides on close.
                    gameFlyout.ShowAt(gameBtn);
                    Assert.Equal(1, lazyCmdLoads);
                    Assert.All(gameFlyout.Items.OfType<MenuItem>(), m => Assert.False(m.IsEnabled));
                    gameFlyout.Hide();
                    Assert.False(gameBtn.IsVisible);

                    // Disabling the sole command provider via PluginManagerWindow hides PluginActionsButton; re-enabling restores it.
                    view.FindControl<Button>("PluginsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => window.OwnedWindows.OfType<PluginManagerWindow>().Any(), token);
                    var manager = window.OwnedWindows.OfType<PluginManagerWindow>().Single();
                    var list = manager.FindControl<ListBox>("PluginList")!;
                    list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(i => Equals(i.Tag, lazyPluginId));
                    Dispatcher.UIThread.RunJobs();
                    manager.FindControl<CheckBox>("EnabledToggle")!.IsChecked = false;
                    manager.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.PluginsDialogTask;
                    Assert.False(toolbarBtn.IsVisible);

                    view.FindControl<Button>("PluginsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => window.OwnedWindows.OfType<PluginManagerWindow>().Any(), token);
                    var manager2 = window.OwnedWindows.OfType<PluginManagerWindow>().Single();
                    var list2 = manager2.FindControl<ListBox>("PluginList")!;
                    list2.SelectedItem = list2.Items.OfType<ListBoxItem>().Single(i => Equals(i.Tag, lazyPluginId));
                    Dispatcher.UIThread.RunJobs();
                    manager2.FindControl<CheckBox>("EnabledToggle")!.IsChecked = true;
                    manager2.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await view.PluginsDialogTask;
                    Assert.True(toolbarBtn.IsVisible);
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
    public async Task LibraryGridView_heroic_sync_command_publishes_event_rebuilds_rows_and_guards_concurrent_clicks()
    {
        var token = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("playnite-heroic-cmd-");
        try
        {
            var dbPath = Path.Combine(tempDir.FullName, "library.sqlite");
            _ = await LibraryDatabase.OpenAsync(dbPath, token);

            var syncEvents = new List<string>();
            var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var slowPlugin = new SlowToolbarCommandPlugin("smoke.slow-cmd", "slow.run", "Slow Run", slowGate);

            using var host = DesktopPluginHostFactory.CreateForLibrary(
                dbPath,
                heroicSync: new AddingHeroicSync(dbPath));
            Assert.True(host.RegisterBuiltIn(new SyncEventRecorderPlugin(syncEvents)));
            Assert.True(host.RegisterBuiltIn(slowPlugin));

            await WithHeadlessUiAsync(async () =>
            {
                var opener = new FakeOpener();
                var model = new LibraryViewModel(dbPath, host, ownsPlugins: false);
                var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: false);
                var window = new Window { Width = 1200, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);
                    await view.CardSlotsTask;
                    await view.DetailsSlotsTask;
                    Assert.Empty(model.Rows);

                    var toolbarBtn = view.FindControl<Button>("PluginActionsButton")!;
                    var toolbarFlyout = (MenuFlyout)toolbarBtn.Flyout!;

                    // Execute heroic.sync command from PluginActionsButton -> adds Heroic row, rebuilds UI, publishes LibrarySynced.
                    toolbarFlyout.ShowAt(toolbarBtn);
                    var heroicItem = toolbarFlyout.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, HeroicPlugin.SyncCommandId));
                    heroicItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    toolbarFlyout.Hide();
                    await view.CommandTask;
                    Assert.Contains(PluginEvents.LibrarySynced, syncEvents);
                    var addedRow = Assert.Single(model.Rows);
                    Assert.Equal("Heroic Added Game", addedRow.Name);
                    Assert.Single(view.FindControl<ListBox>("GameList")!.Items);

                    // While a slow command is in flight (commandPending), a second click does not clobber CommandTask or double-run.
                    toolbarFlyout.ShowAt(toolbarBtn);
                    var slowItem = toolbarFlyout.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, "slow.run"));
                    slowItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    var inFlightTask = view.CommandTask;
                    Assert.False(inFlightTask.IsCompleted);
                    slowItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.Same(inFlightTask, view.CommandTask);
                    toolbarFlyout.Hide();

                    slowGate.SetResult();
                    await inFlightTask;
                    Assert.Equal(1, slowPlugin.ExecutionCount);
                }
                finally
                {
                    slowGate.TrySetResult();
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

    private static void PumpUntil(Func<bool> condition, CancellationToken token, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Timed out waiting for headless UI condition.");
            }

            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private static IEnumerable<T> GetDescendants<T>(Control root) where T : Control
    {
        if (root is T match)
        {
            yield return match;
        }

        if (root is Panel panel)
        {
            foreach (var child in panel.Children.OfType<Control>())
            {
                foreach (var nested in GetDescendants<T>(child))
                {
                    yield return nested;
                }
            }
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

    private sealed class AddingHeroicSync(string dbPath) : ILibraryHeroicSync
    {
        public async Task<HeroicSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken)
        {
            var database = await LibraryDatabase.OpenAsync(dbPath, cancellationToken);
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(Guid.NewGuid(), "epic-added-1", "Heroic Added Game")
                {
                    PluginId = StoreCatalog.EpicPluginId,
                    IsInstalled = true,
                }));
                return true;
            }, cancellationToken);
            return new HeroicSyncResult(1, 0, 0, 0, 0, [], Reconciled: true, RootFound: true);
        }
    }

    private sealed class SlowToolbarCommandPlugin(
        string pluginId,
        string commandId,
        string label,
        TaskCompletionSource gate) : IPlaynitePlugin, ICommandCapability
    {
        private int executionCount;

        public int ExecutionCount => Volatile.Read(ref executionCount);

        public PluginDescriptor Descriptor { get; } = new(
            pluginId,
            Guid.Empty,
            label,
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Desktop,
            PluginCapabilityKind.Commands,
            new PluginEntryPoints());

        public IReadOnlyList<PluginCommandDescriptor> Commands =>
            [new(commandId, pluginId, label, PluginCommandPlacement.LibraryToolbar)];

        public PluginRegistration Setup(PluginContext context) => new()
        {
            CommandProviders = [this],
        };

        public async Task<PluginCommandResult> ExecuteAsync(
            string id,
            PluginCommandContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executionCount);
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new PluginCommandResult(true, "Slow done");
        }
    }

    private sealed class MixedCommandsPlugin(string pluginId, Guid targetSourceId) : IPlaynitePlugin, ICommandCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            pluginId,
            Guid.Empty,
            "Mixed Commands",
            new Version(1, 0, 0),
            PluginLoadKind.ExternalAssembly,
            PluginSurface.Desktop,
            PluginCapabilityKind.Commands,
            new PluginEntryPoints(Desktop: "lazy-mixed.dll"));

        public IReadOnlyList<PluginCommandDescriptor> Commands { get; } =
        [
            new("mixed.game", pluginId, "Steam Only Game Action", PluginCommandPlacement.GameContext, targetSourceId),
            new("mixed.toolbar", pluginId, "Mixed Toolbar Action", PluginCommandPlacement.LibraryToolbar),
        ];

        public PluginRegistration Setup(PluginContext context) => new()
        {
            CommandProviders = [this],
        };

        public Task<PluginCommandResult> ExecuteAsync(string commandId, PluginCommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new PluginCommandResult(true, "ok"));
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

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class HealthyUiSlotPlugin(TaskCompletionSource? slowGate = null) : IPlaynitePlugin, IUiSlotCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            "test.healthy-ui-slot",
            Guid.Empty,
            "Healthy UI Slot",
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Desktop,
            PluginCapabilityKind.UiSlots,
            new PluginEntryPoints());

        public IReadOnlyList<string> SubscribedSlots =>
            [PluginUiSlots.CardBadge, PluginUiSlots.DetailsBadges, PluginUiSlots.DetailsSection];

        public PluginRegistration Setup(PluginContext context) => new()
        {
            UiSlotProviders = [this],
        };

        public async Task<PluginUiSlotContribution?> ResolveSlotAsync(
            string slotId,
            PluginGameSnapshot game,
            CancellationToken cancellationToken)
        {
            if (game.GameId == "custom-2" && slowGate is not null && slotId != PluginUiSlots.CardBadge)
            {
                await slowGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return slotId switch
            {
                PluginUiSlots.CardBadge => new PluginUiSlotContribution(
                    Descriptor.Id,
                    slotId,
                    Badges: [new PluginUiBadge("deck.card", "Deck Verified", PluginBadgeTone.Positive)]),
                PluginUiSlots.DetailsBadges => new PluginUiSlotContribution(
                    Descriptor.Id,
                    slotId,
                    Badges:
                    [
                        new PluginUiBadge(
                            "deck.details",
                            "Deck Verified",
                            PluginBadgeTone.Positive,
                            "View notes",
                            "https://example.com/deck-notes"),
                        new PluginUiBadge(
                            "deck.unsafe",
                            "Static Only",
                            PluginBadgeTone.Muted,
                            "Sanitized link",
                            "javascript:alert(1)"),
                    ]),
                PluginUiSlots.DetailsSection => new PluginUiSlotContribution(
                    Descriptor.Id,
                    slotId,
                    Sections:
                    [
                        new PluginUiSection(
                            "deck.section",
                            "Handheld Verification",
                            "Runs cleanly at 60 FPS.",
                            [new PluginUiSectionField("Controller", "Full support")],
                            LinkLabel: "Open Report",
                            LinkUrl: "https://example.com/deck-report"),
                    ]),
                _ => null,
            };
        }
    }

    private sealed class TestMutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan delta) => current += delta;
    }

    private sealed class ThrowingUiSlotPlugin : IPlaynitePlugin, IUiSlotCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            "test.throwing-ui-slot",
            Guid.Empty,
            "Throwing UI Slot",
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Desktop,
            PluginCapabilityKind.UiSlots,
            new PluginEntryPoints());

        public IReadOnlyList<string> SubscribedSlots =>
            [PluginUiSlots.CardBadge, PluginUiSlots.DetailsBadges, PluginUiSlots.DetailsSection];

        public PluginRegistration Setup(PluginContext context) => new()
        {
            UiSlotProviders = [this],
        };

        public Task<PluginUiSlotContribution?> ResolveSlotAsync(
            string slotId,
            PluginGameSnapshot game,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated slot crash");
    }

    private sealed class SmokeMultiSettingsPlugin(
        string pluginId,
        Guid sourceId,
        string displayName,
        bool isSource)
        : IPlaynitePlugin, ILibrarySourceCapability, IPluginSettingsCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            pluginId,
            sourceId,
            displayName,
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Desktop,
            isSource ? PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Settings : PluginCapabilityKind.Settings,
            new PluginEntryPoints());

        public LibrarySourceInfo Source { get; } = new(sourceId, displayName, displayName);

        public Guid SourceId => sourceId;

        public IReadOnlyList<PluginSettingField> Fields =>
        [
            new("enabledOpt", "Enable Custom Feature", PluginSettingType.Boolean, DefaultValue: "false", Description: "Toggle feature"),
            new("apiToken", "Display Token", PluginSettingType.String, DefaultValue: "default"),
            new("dataPath", "Data Folder", PluginSettingType.Path, DefaultValue: "/tmp/custom"),
            new("limit", "Batch Limit", PluginSettingType.Integer, DefaultValue: "10"),
            new("mode", "Sync Mode", PluginSettingType.Choice, DefaultValue: "Alpha", Choices: ["Alpha", "Beta"]),
        ];

        public PluginRegistration Setup(PluginContext context) => new()
        {
            LibrarySources = isSource ? [this] : [],
            Settings = this,
        };
    }

    private sealed class SmokeThrowingSchemaPlugin(string pluginId) : IPlaynitePlugin, IPluginSettingsCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            pluginId,
            Guid.Empty,
            "Throwing Schema Plugin",
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Desktop,
            PluginCapabilityKind.Settings,
            new PluginEntryPoints());

        public IReadOnlyList<PluginSettingField> Fields =>
            throw new InvalidOperationException("Simulated schema failure");

        public PluginRegistration Setup(PluginContext context) => new()
        {
            Settings = this,
        };
    }

    private sealed class SmokeCommandPlugin(
        string pluginId,
        string commandId,
        string label,
        bool shouldThrow) : IPlaynitePlugin, ICommandCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            pluginId,
            Guid.Empty,
            label,
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Desktop,
            PluginCapabilityKind.Commands,
            new PluginEntryPoints());

        public IReadOnlyList<PluginCommandDescriptor> Commands =>
            [new(commandId, pluginId, label, PluginCommandPlacement.LibraryToolbar)];

        public PluginRegistration Setup(PluginContext context) => new()
        {
            CommandProviders = [this],
        };

        public Task<PluginCommandResult> ExecuteAsync(
            string id,
            PluginCommandContext context,
            CancellationToken cancellationToken)
        {
            if (shouldThrow)
            {
                throw new InvalidOperationException("Simulated command crash");
            }

            return Task.FromResult(new PluginCommandResult(true, $"Lazy pong for library ({Path.GetFileName(context.DatabasePath)})"));
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
