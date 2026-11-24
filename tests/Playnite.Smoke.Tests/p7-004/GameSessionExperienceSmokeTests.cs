using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Shell;
using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Linux.Sessions;
using Playnite.Platform.Sessions;
using Playnite.Steam;
using Playnite.Steam.Sync;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class GameSessionExperienceSmokeTests
{
    [Fact]
    public void GameLaunchPlanner_parses_argv_composes_wrappers_and_rejects_shells_and_bad_paths()
    {
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-planner-");
        try
        {
            Assert.True(GameLaunchPlanner.TryParseArguments(
                "--fullscreen --title \"Hollow \\\"Knight\\\"\" 'single quoted arg' --flag=foo\\ bar",
                out var parsed,
                out var error));
            Assert.Null(error);
            Assert.Equal(
                ["--fullscreen", "--title", "Hollow \"Knight\"", "single quoted arg", "--flag=foo bar"],
                parsed);

            Assert.False(GameLaunchPlanner.TryParseArguments("--unclosed \"quote", out _, out var quoteError));
            Assert.False(string.IsNullOrWhiteSpace(quoteError));
            Assert.False(GameLaunchPlanner.TryParseArguments("--bad\0arg", out _, out var nulError));
            Assert.False(string.IsNullOrWhiteSpace(nulError));

            // Shells, env wrappers, symlinked shells, and relative paths are always rejected.
            Assert.Null(GameLaunchPlanner.CreatePlayAction("/bin/sh", "-c echo", temp.FullName, false, false).Action);
            Assert.Null(GameLaunchPlanner.CreatePlayAction("/usr/bin/bash", null, null, false, false).Action);
            Assert.Null(GameLaunchPlanner.CreatePlayAction("/usr/bin/env", "sh -c id", null, false, false).Action);
            Assert.Null(GameLaunchPlanner.CreatePlayAction("relative/game", null, null, false, false).Action);

            var shellSymlink = Path.Combine(temp.FullName, "disguised-game");
            File.CreateSymbolicLink(shellSymlink, "/bin/sh");
            var symlinkResult = GameLaunchPlanner.CreatePlayAction(shellSymlink, "-c id", temp.FullName, false, false);
            Assert.Null(symlinkResult.Action);
            Assert.Contains("blocked interpreter", symlinkResult.Error, StringComparison.OrdinalIgnoreCase);

            // Missing wrapper binary fails cleanly with an actionable error.
            var missingWrapper = GameLaunchPlanner.CreatePlayAction(
                "/games/app",
                "--fast",
                "/games",
                useGameMode: true,
                useMangoHud: false,
                toolResolver: _ => null);
            Assert.Null(missingWrapper.Action);
            Assert.Contains("gamemoderun", missingWrapper.Error, StringComparison.OrdinalIgnoreCase);

            // GameMode + MangoHud wrap in deterministic order and round-trip through InspectPlayAction.
            string? ResolveFakeTool(string name) => name switch
            {
                "gamemoderun" => "/fake/bin/gamemoderun",
                "mangohud" => "/fake/bin/mangohud",
                _ => null,
            };

            var gameSubDir = Path.Combine(temp.FullName, "mygame");
            Directory.CreateDirectory(gameSubDir);
            var gameBin = Path.Combine(gameSubDir, "game.bin");
            var wrapped = GameLaunchPlanner.CreatePlayAction(
                gameBin,
                "--save-dir \"/tmp/my saves\"",
                workingDirectory: null,
                useGameMode: true,
                useMangoHud: true,
                toolResolver: ResolveFakeTool);
            Assert.Null(wrapped.Error);
            Assert.NotNull(wrapped.Action);
            Assert.NotNull(wrapped.Request);
            Assert.Equal("/fake/bin/gamemoderun", wrapped.Request!.Executable);
            Assert.Equal(
                ["/fake/bin/mangohud", gameBin, "--save-dir", "/tmp/my saves"],
                wrapped.Request.Arguments);

            var inspected = GameLaunchPlanner.InspectPlayAction(wrapped.Action);
            Assert.True(inspected.UseGameMode);
            Assert.True(inspected.UseMangoHud);
            Assert.Equal(gameBin, inspected.Executable);
            Assert.Equal(["--save-dir", "/tmp/my saves"], inspected.Arguments);

            // Fallback working directory for wrapped action uses game binary directory (never wrapper dir).
            var wrappedGame = new Game(Guid.NewGuid(), "custom-1", "Test Game") { GameActions = [wrapped.Action!] };
            var built = GameLaunchPlanner.TryBuildLaunchRequest(wrappedGame);
            Assert.Null(built.Error);
            Assert.Equal(gameSubDir, built.Request!.WorkingDirectory);
            Assert.Equal($"[GameMode + MangoHud] {gameBin}", GameLaunchPlanner.DescribeLaunchTarget(wrappedGame, false, null));

            // Negative TryBuildLaunchRequest cases (missing, DB-wrapped shell, relative path, malformed args, wrapper flag).
            Assert.Null(GameLaunchPlanner.TryBuildLaunchRequest(new Game(Guid.NewGuid(), "empty", "No Action")).Request);
            var dbWrappedShell = new Game(Guid.NewGuid(), "bad-wrap", "Wrapped Shell")
            {
                GameActions =
                [
                    new GameAction { Type = GameActionType.File, IsPlayAction = true, Path = "/usr/bin/gamemoderun", Arguments = "/bin/sh -c id" },
                ],
            };
            Assert.Null(GameLaunchPlanner.TryBuildLaunchRequest(dbWrappedShell).Request);

            var badArgs = GameLaunchPlanner.InspectPlayAction(new GameAction
            {
                Type = GameActionType.File,
                IsPlayAction = true,
                Path = "/usr/bin/true",
                Arguments = "--unclosed \"quote",
            });
            Assert.True(badArgs.ParseFailed);
            Assert.Equal("--unclosed \"quote", badArgs.RawArguments);

            var flagOnlyWrapper = GameLaunchPlanner.InspectPlayAction(new GameAction
            {
                Type = GameActionType.File,
                IsPlayAction = true,
                Path = "/usr/bin/mangohud",
                Arguments = "--dlsym /usr/bin/true",
            });
            Assert.False(flagOnlyWrapper.UseMangoHud);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Tracked_session_runs_updates_live_ui_persists_playtime_playcount_and_last_activity_on_natural_exit()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-session-");
        try
        {
            var dbPath = Path.Combine(temp.FullName, "library.sqlite");
            var gameId = Guid.NewGuid();
            var secondGameId = Guid.NewGuid();
            await SeedGamesAsync(
                dbPath,
                [
                    new Game(gameId, "custom-tracked", "Alpha Chrono Runner")
                    {
                        IsInstalled = true,
                        Playtime = ulong.MaxValue - 10,
                        PlayCount = 2,
                        GameActions =
                        [
                            new GameAction
                            {
                                Type = GameActionType.File,
                                IsPlayAction = true,
                                Name = "Play",
                                Path = "/opt/games/chrono",
                                Arguments = "--level 1",
                            },
                        ],
                    },
                    new Game(secondGameId, "custom-other", "Zulu Other")
                    {
                        IsInstalled = false,
                    },
                ],
                token);

            var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 24, 18, 0, 0, TimeSpan.Zero));
            var identity = new ProcessIdentity(4242, 99999, 4242);
            var fakeLauncher = new FakeProcessLauncher(identity, time);
            var fakeObserver = new FakeProcessObserver(identity);
            var fakeDelay = new GateSessionDelay();
            var fakeSleepService = new FakeSleepInhibitionService();
            await using var sleepInhibition = new SessionSleepInhibition(fakeSleepService);
            await using var manager = new DesktopGameSessionManager(
                fakeLauncher,
                fakeObserver,
                fakeDelay,
                time,
                sleepInhibition,
                new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(10), ExitConfirmationSamples = 2 });

            Assert.False(await manager.StopActiveSessionAsync(token));

            await WithViewAsync(dbPath, manager, null, async (model, view) =>
            {
                var list = view.FindControl<ListBox>("GameList")!;
                var playButton = view.FindControl<Button>("PlayButton")!;
                var stopButton = view.FindControl<Button>("StopButton")!;
                var addButton = view.FindControl<Button>("AddGameButton")!;
                var configButton = view.FindControl<Button>("ConfigureLaunchButton")!;
                var syncButton = view.FindControl<Button>("SyncSteamButton")!;
                var prevButton = view.FindControl<Button>("PreviousButton")!;
                var nextButton = view.FindControl<Button>("NextButton")!;
                var badge = view.FindControl<Border>("SessionBadge")!;
                var badgeText = view.FindControl<TextBlock>("SessionBadgeText")!;
                var playStatus = view.FindControl<TextBlock>("PlayStatus")!;

                Assert.True(playButton.IsEnabled);
                Assert.False(stopButton.IsVisible);
                Assert.False(badge.IsVisible);
                Assert.Equal("2 plays", view.FindControl<TextBlock>("DetailsPlayCount")!.Text);
                Assert.Equal("/opt/games/chrono", view.FindControl<TextBlock>("DetailsLaunchTarget")!.Text);

                playButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await fakeDelay.WaitForEnterAsync(token);

                PumpUntil(() => badge.IsVisible && badgeText.Text == "Running" && stopButton.IsVisible, token);
                Assert.False(playButton.IsEnabled);
                Assert.False(addButton.IsEnabled);
                Assert.False(configButton.IsEnabled);
                Assert.False(syncButton.IsEnabled);
                Assert.False(prevButton.IsEnabled);
                Assert.False(nextButton.IsEnabled);
                Assert.True(stopButton.IsEnabled);
                Assert.True(sleepInhibition.IsHeld);
                Assert.Equal(1, sleepInhibition.ActiveSessionCount);

                // Concurrent session start must be rejected cleanly.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    manager.RunSessionAsync(dbPath, secondGameId, "Zulu Other", new LaunchRequest("/usr/bin/true", []), token));

                // Switching selection to non-active game hides SessionBadge and keeps nav/actions disabled.
                list.SelectedIndex = 1;
                await view.SelectionTask;
                Assert.False(badge.IsVisible);
                Assert.False(prevButton.IsEnabled);
                list.SelectedIndex = 0;
                await view.SelectionTask;
                Assert.True(badge.IsVisible);

                // Advance fake clock 90s while process stays running (verifying Playtime saturates at ulong.MaxValue), then exit.
                time.Advance(TimeSpan.FromSeconds(90));
                fakeDelay.ReleaseOnce();
                await fakeDelay.WaitForEnterAsync(token);

                fakeObserver.IsProcessPresent = false;
                fakeDelay.ReleaseOnce();
                await fakeDelay.WaitForEnterAsync(token);
                fakeDelay.ReleaseOnce();

                await view.SessionTask;
                Dispatcher.UIThread.RunJobs();

                Assert.False(badge.IsVisible);
                Assert.False(stopButton.IsVisible);
                Assert.True(playButton.IsEnabled);
                Assert.True(addButton.IsEnabled);
                Assert.True(configButton.IsEnabled);
                Assert.False(sleepInhibition.IsHeld);
                Assert.Equal(0, sleepInhibition.ActiveSessionCount);
                Assert.Contains("exited (+90s)", playStatus.Text, StringComparison.Ordinal);
                Assert.Equal("3 plays", view.FindControl<TextBlock>("DetailsPlayCount")!.Text);
                Assert.Equal("2026-11-24", view.FindControl<TextBlock>("DetailsLastPlayed")!.Text);

                var db = await LibraryDatabase.OpenAsync(dbPath, token);
                var saved = await db.ReadAsync(s => s.Games.GetAsync(gameId), token);
                Assert.NotNull(saved);
                Assert.Equal(ulong.MaxValue, saved!.Value.Playtime);
                Assert.Equal(3UL, saved.Value.PlayCount);
                Assert.Equal(time.GetUtcNow().UtcDateTime, saved.Value.LastActivity);
                Assert.NotNull(model);
            }, token);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Session_manager_handles_launch_failure_and_degraded_tracking_cleanly()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-degraded-");
        try
        {
            var missingDbPath = Path.Combine(temp.FullName, "missing.sqlite");
            var missingId = Guid.NewGuid();
            await SeedGamesAsync(
                missingDbPath,
                [
                    new Game(missingId, "custom-missing", "Missing Binary Game")
                    {
                        IsInstalled = true,
                        GameActions =
                        [
                            new GameAction { Type = GameActionType.File, IsPlayAction = true, Name = "Play", Path = "/nonexistent/playnite-missing-game" },
                        ],
                    },
                ],
                token);

            var sleepService1 = new FakeSleepInhibitionService();
            await using var sleepInhibition1 = new SessionSleepInhibition(sleepService1);
            await using var realLauncher = new LinuxProcessLauncher();
            await using var failureManager = new DesktopGameSessionManager(
                realLauncher,
                new LinuxProcessObserver(),
                sleepInhibition: sleepInhibition1);

            await WithViewAsync(missingDbPath, failureManager, null, async (_, view) =>
            {
                var playButton = view.FindControl<Button>("PlayButton")!;
                var playStatus = view.FindControl<TextBlock>("PlayStatus")!;
                playButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.SessionTask;
                Dispatcher.UIThread.RunJobs();

                Assert.Contains("Could not launch Missing Binary Game", playStatus.Text, StringComparison.Ordinal);
                Assert.True(playButton.IsEnabled);
                Assert.False(view.FindControl<Button>("StopButton")!.IsVisible);
                Assert.False(sleepInhibition1.IsHeld);
                Assert.Equal(0, sleepInhibition1.ActiveSessionCount);

                var db = await LibraryDatabase.OpenAsync(missingDbPath, token);
                var saved = await db.ReadAsync(s => s.Games.GetAsync(missingId), token);
                Assert.Equal(0UL, saved!.Value.PlayCount);
            }, token);

            // Degraded mode: ExitObservationSupported = false -> badge reads "Running (degraded)" until Stop clicked.
            var degradedDbPath = Path.Combine(temp.FullName, "degraded.sqlite");
            var degradedId = Guid.NewGuid();
            await SeedGamesAsync(
                degradedDbPath,
                [
                    new Game(degradedId, "custom-degraded", "Degraded Target")
                    {
                        IsInstalled = true,
                        GameActions =
                        [
                            new GameAction { Type = GameActionType.File, IsPlayAction = true, Name = "Play", Path = "/opt/games/degraded" },
                        ],
                    },
                ],
                token);

            var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 24, 19, 0, 0, TimeSpan.Zero));
            var fakeId = new ProcessIdentity(5151, 88888, 5151);
            await using var sleepInhibition2 = new SessionSleepInhibition(new FakeSleepInhibitionService());
            await using var degradedManager = new DesktopGameSessionManager(
                new FakeProcessLauncher(fakeId, time),
                new FakeProcessObserver(fakeId),
                timeProvider: time,
                sleepInhibition: sleepInhibition2,
                sessionOptions: new SessionOptions { ExitObservationSupported = false });

            await WithViewAsync(degradedDbPath, degradedManager, null, async (_, view) =>
            {
                var playButton = view.FindControl<Button>("PlayButton")!;
                var stopButton = view.FindControl<Button>("StopButton")!;
                var badgeText = view.FindControl<TextBlock>("SessionBadgeText")!;

                playButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => badgeText.Text == "Running (degraded)" && stopButton.IsVisible && stopButton.IsEnabled, token);

                stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.StopTask;
                await view.SessionTask;
                Dispatcher.UIThread.RunJobs();

                Assert.Contains("degraded", view.FindControl<TextBlock>("PlayStatus")!.Text, StringComparison.OrdinalIgnoreCase);
                Assert.False(sleepInhibition2.IsHeld);
            }, token);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Stop_button_terminates_real_linux_process_records_playtime_and_leaves_no_orphan()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-realstop-");
        try
        {
            var dbPath = Path.Combine(temp.FullName, "library.sqlite");
            var gameId = Guid.NewGuid();
            await SeedGamesAsync(
                dbPath,
                [
                    new Game(gameId, "custom-sleep", "Sleep Target")
                    {
                        IsInstalled = true,
                        GameActions =
                        [
                            new GameAction
                            {
                                Type = GameActionType.File,
                                IsPlayAction = true,
                                Name = "Play",
                                Path = "/usr/bin/sleep",
                                Arguments = "30",
                                WorkingDir = temp.FullName,
                            },
                        ],
                    },
                ],
                token);

            await using var realLauncher = new LinuxProcessLauncher();
            var capturingLauncher = new CapturingProcessLauncher(realLauncher);
            await using var manager = new DesktopGameSessionManager(
                capturingLauncher,
                new LinuxProcessObserver(),
                sessionOptions: new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(40), ExitConfirmationSamples = 2 });

            await WithViewAsync(dbPath, manager, null, async (_, view) =>
            {
                var playButton = view.FindControl<Button>("PlayButton")!;
                var stopButton = view.FindControl<Button>("StopButton")!;
                var badgeText = view.FindControl<TextBlock>("SessionBadgeText")!;

                playButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => capturingLauncher.LastStarted is not null && stopButton.IsVisible && stopButton.IsEnabled && badgeText.Text == "Running", token);

                var started = capturingLauncher.LastStarted!;
                stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.StopTask;
                await view.SessionTask;
                Dispatcher.UIThread.RunJobs();

                Assert.False(stopButton.IsVisible);
                Assert.True(playButton.IsEnabled);
                Assert.Equal("1 play", view.FindControl<TextBlock>("DetailsPlayCount")!.Text);

                var statAfter = await new LinuxProcStatReader().ReadAsync(started.Identity.ProcessId, token);
                Assert.True(
                    statAfter is null
                    || statAfter.Value.StartTimeTicks != started.Identity.StartTimeTicks
                    || !statAfter.Value.IsRunning);

                var db = await LibraryDatabase.OpenAsync(dbPath, token);
                var saved = await db.ReadAsync(s => s.Games.GetAsync(gameId), token);
                Assert.NotNull(saved);
                Assert.Equal(1UL, saved!.Value.PlayCount);
                Assert.NotNull(saved.Value.LastActivity);
            }, token);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Add_custom_game_and_configure_launch_editor_validates_persists_and_launches()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-editor-");
        try
        {
            var dbPath = Path.Combine(temp.FullName, "library.sqlite");
            string? ToolResolver(string tool) => tool switch
            {
                "gamemoderun" => "/fake/bin/gamemoderun",
                "mangohud" => "/fake/bin/mangohud",
                _ => null,
            };

            await WithViewAsync(dbPath, null, ToolResolver, async (model, view) =>
            {
                var addButton = view.FindControl<Button>("AddGameButton")!;
                var configButton = view.FindControl<Button>("ConfigureLaunchButton")!;
                Assert.True(addButton.IsEnabled);
                Assert.False(configButton.IsEnabled);

                // Verify Cancel closes with null result.
                view.EditorDialogRunner = (editor, _) =>
                {
                    editor.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return Task.FromResult(editor.Result);
                };
                addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.EditorTask;
                Assert.Empty(view.FindControl<ListBox>("GameList")!.Items);

                // Drive AddGameButton -> GameLaunchEditorWindow: invalid input shows error, valid input clears error and saves.
                view.EditorDialogRunner = (editor, _) =>
                {
                    var saveBtn = editor.FindControl<Button>("SaveButton")!;
                    var errorText = editor.FindControl<TextBlock>("ValidationErrorText")!;
                    var nameBox = editor.FindControl<TextBox>("GameNameBox")!;
                    var exeBox = editor.FindControl<TextBox>("ExecutablePathBox")!;
                    var argsBox = editor.FindControl<TextBox>("ArgumentsBox")!;
                    var workBox = editor.FindControl<TextBox>("WorkingDirBox")!;
                    var descBox = editor.FindControl<TextBox>("DescriptionBox")!;
                    var gameModeCheck = editor.FindControl<CheckBox>("GameModeCheckBox")!;
                    var mangoCheck = editor.FindControl<CheckBox>("MangoHudCheckBox")!;
                    var preview = editor.FindControl<TextBlock>("PreviewText")!;

                    exeBox.Text = "/bin/sh";
                    saveBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Null(editor.Result);
                    Assert.True(errorText.IsVisible);

                    nameBox.Text = "Hades Native";
                    exeBox.Text = "/usr/bin/true";
                    argsBox.Text = "--windowed \"fast mode\"";
                    workBox.Text = temp.FullName;
                    descBox.Text = "Custom native launch entry.";
                    gameModeCheck.IsChecked = true;
                    mangoCheck.IsChecked = true;
                    Assert.False(errorText.IsVisible);
                    Assert.Contains("--windowed", preview.Text, StringComparison.Ordinal);

                    saveBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.NotNull(editor.Result);
                    return Task.FromResult<GameLaunchEditorResult?>(editor.Result);
                };

                addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.EditorTask;

                var list = view.FindControl<ListBox>("GameList")!;
                Assert.Single(list.Items);
                Assert.Equal("Hades Native", view.FindControl<TextBlock>("DetailsName")!.Text);
                Assert.True(view.FindControl<Button>("PlayButton")!.IsEnabled);
                Assert.True(configButton.IsEnabled);
                Assert.Equal("Custom native launch entry.", view.FindControl<TextBlock>("DetailsDescription")!.Text);

                // Seed a non-play action + LegacyJson on the added game to verify Configure Launch preserves them.
                var addedId = model.Selected!.Id;
                var db = await LibraryDatabase.OpenAsync(dbPath, token);
                await db.WriteAsync(async s =>
                {
                    var current = (await s.Games.GetAsync(addedId))!;
                    var g = current.Value;
                    g.GameActions!.Add(new GameAction { Type = GameActionType.URL, IsPlayAction = false, Name = "Wiki", Path = "https://example.invalid" });
                    await s.Games.UpdateAsync(new StoredGame(g, "{\"custom_tag\":42}"));
                    return true;
                }, token);

                // Drive ConfigureLaunchButton -> verifies wrapped round-trip, clears wrappers, and saves /usr/bin/sleep 0.25.
                view.EditorDialogRunner = (editWindow, _) =>
                {
                    Assert.Equal("/usr/bin/true", editWindow.FindControl<TextBox>("ExecutablePathBox")!.Text);
                    Assert.Equal("--windowed \"fast mode\"", editWindow.FindControl<TextBox>("ArgumentsBox")!.Text);
                    Assert.True(editWindow.FindControl<CheckBox>("GameModeCheckBox")!.IsChecked);
                    Assert.True(editWindow.FindControl<CheckBox>("MangoHudCheckBox")!.IsChecked);

                    editWindow.FindControl<CheckBox>("GameModeCheckBox")!.IsChecked = false;
                    editWindow.FindControl<CheckBox>("MangoHudCheckBox")!.IsChecked = false;
                    editWindow.FindControl<TextBox>("ExecutablePathBox")!.Text = "/usr/bin/sleep";
                    editWindow.FindControl<TextBox>("ArgumentsBox")!.Text = "0.25";
                    editWindow.FindControl<TextBox>("WorkingDirBox")!.Text = string.Empty;
                    editWindow.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return Task.FromResult<GameLaunchEditorResult?>(editWindow.Result);
                };

                configButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.EditorTask;
                Assert.Equal("/usr/bin/sleep", view.FindControl<TextBlock>("DetailsLaunchTarget")!.Text);

                var updatedStored = await db.ReadAsync(s => s.Games.GetAsync(addedId), token);
                Assert.Equal("{\"custom_tag\":42}", updatedStored!.LegacyJson);
                Assert.Contains(updatedStored.Value.GameActions!, a => !a.IsPlayAction && a.Name == "Wiki");

                // Click PlayButton to verify the newly configured custom native game actually runs and records PlayCount.
                view.FindControl<Button>("PlayButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await view.SessionTask;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("1 play", view.FindControl<TextBlock>("DetailsPlayCount")!.Text);
            }, token);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Unload_stops_active_tracked_session_releases_sleep_inhibition_without_orphans_or_ui_errors()
    {
        var token = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("playnite-p7-004-unload-");
        try
        {
            var dbPath = Path.Combine(temp.FullName, "library.sqlite");
            var gameId = Guid.NewGuid();
            await SeedGamesAsync(
                dbPath,
                [
                    new Game(gameId, "custom-unload", "Unload Sleep Target")
                    {
                        IsInstalled = true,
                        GameActions =
                        [
                            new GameAction
                            {
                                Type = GameActionType.File,
                                IsPlayAction = true,
                                Name = "Play",
                                Path = "/usr/bin/sleep",
                                Arguments = "30",
                                WorkingDir = temp.FullName,
                            },
                        ],
                    },
                ],
                token);

            var sleepService = new FakeSleepInhibitionService();
            await using var sleepInhibition = new SessionSleepInhibition(sleepService);
            await using var realLauncher = new LinuxProcessLauncher();
            var capturingLauncher = new CapturingProcessLauncher(realLauncher);
            await using var manager = new DesktopGameSessionManager(
                capturingLauncher,
                new LinuxProcessObserver(),
                sleepInhibition: sleepInhibition,
                sessionOptions: new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(40), ExitConfirmationSamples = 2 });

            ProcessIdentity? startedIdentity = null;
            await WithViewAsync(dbPath, manager, null, async (_, view) =>
            {
                var playButton = view.FindControl<Button>("PlayButton")!;
                var badgeText = view.FindControl<TextBlock>("SessionBadgeText")!;

                playButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => capturingLauncher.LastStarted is not null && badgeText.Text == "Running", token);
                startedIdentity = capturingLauncher.LastStarted!.Identity;
                Assert.True(sleepInhibition.IsHeld);

                (TopLevel.GetTopLevel(view) as Window)?.Close();
                Dispatcher.UIThread.RunJobs();
                await view.UnloadTask;
                await view.SessionTask;
            }, token);

            Assert.NotNull(startedIdentity);
            Assert.False(sleepInhibition.IsHeld);
            Assert.Equal(0, sleepInhibition.ActiveSessionCount);

            var statAfter = await new LinuxProcStatReader().ReadAsync(startedIdentity!.Value.ProcessId, token);
            Assert.True(
                statAfter is null
                || statAfter.Value.StartTimeTicks != startedIdentity.Value.StartTimeTicks
                || !statAfter.Value.IsRunning);

            var db = await LibraryDatabase.OpenAsync(dbPath, token);
            var saved = await db.ReadAsync(s => s.Games.GetAsync(gameId), token);
            Assert.NotNull(saved);
            Assert.Equal(1UL, saved!.Value.PlayCount);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static void PumpUntil(Func<bool> condition, CancellationToken token)
    {
        for (var i = 0; i < 250 && !token.IsCancellationRequested; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            Thread.Sleep(20);
        }

        Assert.True(condition(), "Timed out waiting for tracked game session state.");
    }

    private static async Task WithViewAsync(
        string dbPath,
        DesktopGameSessionManager? manager,
        Func<string, string?>? toolResolver,
        Func<LibraryViewModel, LibraryGridView, Task> check,
        CancellationToken token)
    {
        var completed = false;
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
        await session.Dispatch(() =>
        {
            var frame = new DispatcherFrame();
            var task = RunAsync();
            if (!task.IsCompleted)
            {
                using var registration = token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
                _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.UIThread.PushFrame(frame);
            }

            task.GetAwaiter().GetResult();

            async Task RunAsync()
            {
                var model = new LibraryViewModel(dbPath);
                var view = new LibraryGridView(
                    new SteamPlayRequest(new NoopOpener()),
                    fullscreenPreview: false,
                    new NoopSteamSync(),
                    artworkLoaderFactory: null,
                    sessionManager: manager,
                    toolResolver: toolResolver);
                var window = new Window { Width = 1280, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    window.Show();
                    view.Apply(model);
                    await check(model, view);
                    completed = true;
                }
                finally
                {
                    window.Close();
                    await view.UnloadTask;
                    model.Dispose();
                }
            }
        }, token);
        Assert.True(completed);
    }

    private static async Task SeedGamesAsync(string dbPath, IReadOnlyList<Game> games, CancellationToken token)
    {
        var db = await LibraryDatabase.OpenAsync(dbPath, token);
        await db.WriteAsync(async session =>
        {
            foreach (var game in games)
            {
                await session.Games.AddAsync(new StoredGame(game));
            }

            return games.Count;
        }, token);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset currentUtc = initialUtc;
        private long currentTimestamp = 1_000_000_000L;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => currentUtc;

        public override long GetTimestamp() => currentTimestamp;

        public void Advance(TimeSpan delta)
        {
            currentUtc += delta;
            currentTimestamp += delta.Ticks;
        }
    }

    private sealed class CapturingProcessLauncher(IProcessLauncher inner) : IProcessLauncher
    {
        public IGameProcess? LastStarted { get; private set; }

        public async Task<IGameProcess> StartAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            var process = await inner.StartAsync(request, cancellationToken).ConfigureAwait(false);
            LastStarted = process;
            return process;
        }

        public Task<bool> IsRunningAsync(IGameProcess process, CancellationToken cancellationToken) =>
            inner.IsRunningAsync(process, cancellationToken);

        public Task StopAsync(IGameProcess process, CancellationToken cancellationToken) =>
            inner.StopAsync(process, cancellationToken);

        public Task StopAsync(
            IGameProcess process,
            IReadOnlyCollection<ProcessIdentity> trackedProcesses,
            CancellationToken cancellationToken) =>
            inner.StopAsync(process, trackedProcesses, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeGameProcess(ProcessIdentity identity, DateTimeOffset startedAt) : IGameProcess
    {
        public ProcessIdentity Identity { get; } = identity;
        public DateTimeOffset StartedAt { get; } = startedAt;
    }

    private sealed class FakeProcessLauncher(ProcessIdentity identity, TimeProvider time) : IProcessLauncher
    {
        public Task<IGameProcess> StartAsync(LaunchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IGameProcess>(new FakeGameProcess(identity, time.GetUtcNow()));

        public Task<bool> IsRunningAsync(IGameProcess process, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task StopAsync(IGameProcess process, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StopAsync(IGameProcess process, IReadOnlyCollection<ProcessIdentity> trackedProcesses, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProcessObserver(ProcessIdentity identity) : IProcessObserver
    {
        public bool IsProcessPresent { get; set; } = true;

        public Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
        {
            var entries = IsProcessPresent
                ? new[] { new ProcessEntry(identity, 1, true) }
                : [];
            return Task.FromResult(new ProcessSnapshot(entries, IsComplete: true));
        }
    }

    private sealed class GateSessionDelay : ISessionDelay
    {
        private readonly object gate = new();
        private TaskCompletionSource? entered;
        private TaskCompletionSource? release;

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            TaskCompletionSource currentRelease;
            lock (gate)
            {
                currentRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                release = currentRelease;
                entered?.TrySetResult();
            }

            return currentRelease.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForEnterAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource waiter;
            lock (gate)
            {
                if (release is not null)
                {
                    return;
                }

                entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = entered;
            }

            await waiter.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseOnce()
        {
            lock (gate)
            {
                var current = release;
                release = null;
                entered = null;
                current?.TrySetResult();
            }
        }
    }

    private sealed class FakeSleepInhibitionService : ISleepInhibitionService
    {
        public Task<SleepInhibitionLease> InhibitAsync(
            SleepInhibitionRequest request,
            CancellationToken cancellationToken = default)
        {
            var handle = new FakeHandle();
            return Task.FromResult(SleepInhibitionLease.Held(
                DesktopCapability.Available(DesktopFacility.SleepInhibition, "fake-inhibit", "test lease"),
                handle));
        }

        private sealed class FakeHandle : ISleepInhibitorHandle
        {
            private bool released;
            public string Mechanism => "fake-inhibit";
            public bool IsHeld => !released;

            public ValueTask DisposeAsync()
            {
                released = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class NoopSteamSync : ILibrarySteamSync
    {
        public Task<SteamSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken) =>
            Task.FromResult(new SteamSyncResult(0, 0, 0, 0, [], [], true));
    }

    private sealed class NoopOpener : ISteamUriOpener
    {
        public Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
