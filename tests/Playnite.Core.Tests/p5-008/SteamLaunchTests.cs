using System.Text;
using Playnite.Platform.Sessions;
using Playnite.Steam;
using Xunit;

namespace Playnite.Core.Tests.Steam;

public sealed class SteamLaunchTests
{
    private static readonly SteamLaunchTarget Native = new(new SteamApp(42, "Synthetic", "Game with spaces",
        "/library", SteamAppState.Installed, false, null, null, 4));
    private static readonly SteamLaunchTarget Proton = Native with { ProtonPrefix = "/library/steamapps/compatdata/42/pfx" };
    private static readonly ExternalProcessEntry Client = Entry(10, 1, 10, "/steam/steam");
    private static readonly ExternalProcessSnapshot Before = new([Client], true);

    [Fact]
    public async Task Native_request_records_argv_and_attributes_game_not_client_or_wrappers()
    {
        var host = Host(Native);
        await using var launcher = Launcher(host, Native);
        var process = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        Assert.Equal("/usr/bin/steam", host.Request!.Executable);
        Assert.Equal(["-applaunch", "42"], host.Request.Arguments);
        Assert.Equal(new ProcessIdentity(22, 2, 20), process.Identity);
        Assert.NotEqual(Client.Process.Identity, process.Identity);
        await launcher.StopAsync(process, CancellationToken.None);
        Assert.Equal([22], host.Signalled.Select(identity => identity.ProcessId));
        Assert.True(launcher.StopReport!.IsDegraded);
        Assert.Equal([20, 21], launcher.StopReport.HelperSurvivors.Select(identity => identity.ProcessId).Order());
        Assert.True(host.Rows.Any(row => row.Process.Identity == Client.Process.Identity));
    }

    [Fact]
    public async Task Protocol_is_only_used_when_no_steam_executable_was_resolved()
    {
        var host = Host(Native);
        await using var launcher = Launcher(host, Native, steam: null);
        _ = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        Assert.Equal("/usr/bin/xdg-open", host.Request!.Executable);
        Assert.Equal(["steam://rungameid/42"], host.Request.Arguments);
    }

    [Fact]
    public async Task Proton_prefix_game_is_attributed_but_wineserver_does_not_keep_session_alive()
    {
        var host = Host(Proton);
        await using var launcher = Launcher(host, Proton);
        var process = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        Assert.Equal(22, process.Identity.ProcessId);
        await using var tracker = new GameSessionTracker(launcher, launcher);
        var samples = 0;
        host.OnSnapshot = () =>
        {
            if (++samples >= 2)
            {
                host.Rows.RemoveAll(row => row.Process.Identity.ProcessId == 22);
            }
        };
        await tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(1) }, CancellationToken.None);
        Assert.Equal(GameSessionState.Stopped, tracker.State);
        Assert.DoesNotContain(host.Rows, row => SteamProcessAttribution.Matches(Proton, row.Executable));
        Assert.Equal([20, 21], launcher.StopReport!.HelperSurvivors.Select(identity => identity.ProcessId).Order());
        Assert.Empty(host.Signalled); // The game exited naturally; helpers must remain untouched.
        Assert.DoesNotContain(host.Signalled, identity => identity.ProcessId == 10);
    }

    [Theory]
    [InlineData("shared-client")]
    [InlineData("shared-existing")]
    [InlineData("multiple")]
    [InlineData("incomplete")]
    [InlineData("unrelated-member")]
    [InlineData("no-game")]
    [InlineData("preexisting-game")]
    public void Ambiguous_or_unverifiable_samples_are_unknown(string shape)
    {
        var before = Before;
        var game = Entry(22, 2, 20, Native.App.InstallPath + "/game");
        ExternalProcessSnapshot after = new([Client, game], true);
        switch (shape)
        {
            case "shared-client": after = new([Client, game with { Process = game.Process with { Identity = new(22, 2, 10) } }], true); break;
            case "shared-existing": before = new([Client, Entry(20, 1, 20, "/other")], true); break;
            case "multiple": after = new([Client, game, Entry(23, 3, 30, Native.App.InstallPath + "/other")], true); break;
            case "incomplete": after = after with { IsComplete = false }; break;
            case "unrelated-member": after = new([Client, game, Entry(23, 3, 20, "/unrelated/tool")], true); break;
            case "no-game": after = Before; break;
            case "preexisting-game": before = after; break;
        }

        Assert.Null(SteamProcessAttribution.Find(Native, before, after));
    }

    [Fact]
    public void Fresh_unreadable_runtime_helper_is_tolerated_but_never_a_candidate()
    {
        // Real Dwarf Fortress shape: pv-adverb lives in Steam's mount namespace, so the host
        // cannot read /proc/PID/exe even though the member is live and fresh.
        var game = Entry(22, 2, 20, Native.App.InstallPath + "/game");
        var decision = SteamProcessAttribution.Evaluate(Native, Before,
            new([Client, Entry(21, 2, 20, null), game], true));
        Assert.Null(decision.Rejection);
        Assert.Equal(22, decision.Identity!.Value.ProcessId);
        Assert.Equal(new ProcessIdentity(22, 2, 20), Assert.Single(decision.Candidates).Identity);
        Assert.Equal("pid=21 exe=<unreadable>", Assert.Single(decision.ToleratedMembers));
    }

    [Fact]
    public void Unreadable_member_that_predates_the_launch_is_shared_and_unknown()
    {
        var before = new ExternalProcessSnapshot([Client, Entry(21, 1, 20, null)], true);
        var after = new ExternalProcessSnapshot([Client, Entry(21, 1, 20, null),
            Entry(22, 2, 20, Native.App.InstallPath + "/game")], true);
        Assert.Null(SteamProcessAttribution.Find(Native, before, after));
        Assert.Contains("contains pre-existing pid=21", SteamProcessAttribution.Evaluate(Native, before, after).Rejection);
    }

    [Fact]
    public void Proton_game_identities_in_one_fresh_group_are_attributed_together()
    {
        var exe = Entry(22, 2, 20, Proton.ProtonPrefix + "/drive_c/Game.exe");
        var launcher = Entry(23, 2, 20, Proton.App.InstallPath + "/Game.bin");
        var decision = SteamProcessAttribution.Evaluate(Proton, Before,
            new([Client, Entry(20, 2, 20, "/steam/pv-adverb"), exe, launcher], true));
        Assert.Null(decision.Rejection);
        Assert.Equal(20, decision.ProcessGroup);
        Assert.Equal([22, 23], decision.Candidates.Select(candidate => candidate.Identity.ProcessId).Order());
    }

    [Fact]
    public void Candidates_in_two_fresh_groups_are_ambiguous_unknown()
    {
        var decision = SteamProcessAttribution.Evaluate(Native, Before,
            new([Client, Entry(22, 2, 20, Native.App.InstallPath + "/game"),
                Entry(23, 3, 30, Native.App.InstallPath + "/other")], true));
        Assert.Null(decision.Identity);
        Assert.Contains("span 2 process groups", decision.Rejection);
    }

    [Fact]
    public async Task Wait_progress_names_each_candidate_and_the_exact_rejecting_predicate()
    {
        var progress = new List<SteamAttributionProgress>();
        var host = new FakeHost();
        host.OnRun = () => host.Rows.Add(Entry(22, 2, 10, Native.App.InstallPath + "/game"));
        await using var launcher = new SteamProcessLauncher(host, Native, new()
        {
            SteamExecutable = "/usr/bin/steam", ProtocolOpener = "/usr/bin/xdg-open",
            PollInterval = TimeSpan.FromMilliseconds(1), AttributionWindow = TimeSpan.FromMilliseconds(300),
            ProgressInterval = TimeSpan.FromMilliseconds(20), Progress = new InlineProgress(progress.Add),
        });
        await Assert.ThrowsAsync<SteamAttributionUnknownException>(() => launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None));
        Assert.Empty(host.Signalled);
        Assert.Contains(progress, row => row.Decision.Contains("pid=22", StringComparison.Ordinal)
            && row.Decision.Contains("pgid=10", StringComparison.Ordinal)
            && row.Decision.Contains("rejected: group 10 contains pre-existing pid=10", StringComparison.Ordinal));
    }

    [Fact]
    public void Recycled_pid_is_a_new_process_but_same_pid_start_time_in_a_new_group_is_not()
    {
        var old = Entry(22, 1, 19, Native.App.InstallPath + "/game");
        var before = new ExternalProcessSnapshot([Client, old], true);
        Assert.NotNull(SteamProcessAttribution.Find(Native, before,
            new([Client, Entry(22, 2, 20, Native.App.InstallPath + "/game")], true)));
        Assert.Null(SteamProcessAttribution.Find(Native, before,
            new([Client, Entry(22, 1, 20, Native.App.InstallPath + "/game")], true)));
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("steamwebhelper")]
    [InlineData("pv-adverb")]
    [InlineData("wineserver")]
    [InlineData("wine64-preloader")]
    [InlineData("pressure-vessel")]
    [InlineData("pressure-vessel-supervisor")]
    [InlineData("steam-runtime-launcher-service")]
    [InlineData("steam-runtime-supervisor")]
    [InlineData("srt-logger")]
    [InlineData("proton")]
    [InlineData("wineboot.exe")]
    [InlineData("winedevice.exe")]
    [InlineData("services.exe")]
    [InlineData("explorer.exe")]
    [InlineData("steam.sh")]
    public void Infrastructure_never_starts_a_session_even_under_the_install_root(string name)
    {
        Assert.Null(SteamProcessAttribution.Find(Native, Before,
            new([Client, Entry(22, 2, 20, Native.App.InstallPath + "/" + name)], true)));
    }

    [Fact]
    public void Prefix_and_install_path_matches_are_component_bounded()
    {
        Assert.False(SteamProcessAttribution.Matches(Proton, Proton.ProtonPrefix + "-other/game.exe"));
        Assert.False(SteamProcessAttribution.Matches(Native, Native.App.InstallPath + "-other/game"));
        Assert.False(SteamProcessAttribution.Matches(Native, Native.App.InstallPath + "/../other/game"));
    }

    [Fact]
    public async Task Unknown_deadline_does_not_claim_start_or_signal_any_process()
    {
        var host = new FakeHost();
        await using var launcher = Launcher(host, Native, window: TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAsync<SteamAttributionUnknownException>(() => launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None));
        Assert.Empty(host.Signalled);
    }

    [Fact]
    public async Task Proton_can_appear_after_sixty_seconds_with_wait_progress_and_an_attributed_decision()
    {
        var clock = new ManualSteamTime();
        var progress = new List<SteamAttributionProgress>();
        var host = new FakeHost();
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnSnapshot = () =>
        {
            if (host.Request is null) { return; }
            if (clock.GetUtcNow() >= DateTimeOffset.UnixEpoch.AddSeconds(140))
            {
                host.Rows.Add(Entry(22, 2, 20, Proton.ProtonPrefix + "/game.exe"));
            }
            sampled.TrySetResult();
        };
        var launcher = new SteamProcessLauncher(host, Proton, new()
        {
            SteamExecutable = "/usr/bin/steam", ProtocolOpener = "/usr/bin/xdg-open",
            PollInterval = TimeSpan.FromSeconds(1),
            Progress = new InlineProgress(progress.Add),
        }, clock);
        using var cancellation = new CancellationTokenSource();
        var start = launcher.StartAsync(launcher.CreateRequest(), cancellation.Token);
        try
        {
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clock.PollScheduled.WaitAsync(TimeSpan.FromSeconds(5));
            sampled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            clock.Advance(TimeSpan.FromSeconds(70));
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clock.PollScheduled.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(start.IsCompleted);
            Assert.Contains(progress, row => row.Elapsed == TimeSpan.FromSeconds(70) && row.Decision.StartsWith("WAITING:", StringComparison.Ordinal));
            clock.Advance(TimeSpan.FromSeconds(70));
            var game = await start.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(22, game.Identity.ProcessId);
            Assert.Equal(TimeSpan.FromSeconds(240), progress[^1].Window);
            Assert.Equal(TimeSpan.FromSeconds(140), progress[^1].Elapsed);
            Assert.StartsWith("ATTRIBUTED:", progress[^1].Decision);
        }
        finally
        {
            host.OnSnapshot = null;
            host.Rows.RemoveAll(row => SteamProcessAttribution.Matches(Proton, row.Executable));
            cancellation.Cancel();
            clock.Advance(TimeSpan.FromSeconds(240));
            await launcher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Window_expiry_reports_a_terminal_unknown_without_signalling()
    {
        var clock = new ManualSteamTime();
        var progress = new List<SteamAttributionProgress>();
        var host = new FakeHost();
        await using var launcher = new SteamProcessLauncher(host, Proton, new()
        {
            ProtocolOpener = "/usr/bin/xdg-open", Progress = new InlineProgress(progress.Add),
        }, clock);
        var start = launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(240));
        var error = await Assert.ThrowsAsync<SteamAttributionUnknownException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("window 240s", error.Message);
        Assert.StartsWith("UNKNOWN: attribution window expired", progress[^1].Decision);
        Assert.Equal(TimeSpan.FromSeconds(240), progress[^1].Elapsed);
        Assert.Empty(host.Signalled);
    }

    [Theory]
    [InlineData("steam-launch-wrapper", true)]
    [InlineData("pv-adverb", true)]
    [InlineData("wineserver", true)]
    [InlineData("srt-bwrap", true)]
    [InlineData("bash", false)]
    public async Task Steam_owned_helper_sharing_a_fresh_group_is_never_signalled(string helper, bool identified)
    {
        var host = new FakeHost();
        host.OnRun = () => host.Rows.AddRange([
            Entry(20, 2, 20, "/steam/" + helper), Entry(22, 2, 20, Native.App.InstallPath + "/game"),
        ]);
        await using var launcher = Launcher(host, Native);
        var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        await launcher.StopAsync(game, CancellationToken.None);
        Assert.Equal([22], host.Signalled.Select(identity => identity.ProcessId));
        Assert.True(launcher.StopReport!.IsDegraded);
        if (identified)
        {
            Assert.Equal([new ProcessIdentity(20, 2, 20)], launcher.StopReport.HelperSurvivors);
            Assert.Empty(launcher.StopReport.UnidentifiedSurvivors);
        }
        else
        {
            // A bare shell carries no runtime path or parent evidence, so it is not claimed
            // to be a Steam helper even though it is left unsignalled.
            Assert.Empty(launcher.StopReport.HelperSurvivors);
            Assert.Equal([new ProcessIdentity(20, 2, 20)], launcher.StopReport.UnidentifiedSurvivors);
            Assert.Contains("manual inspection required", launcher.StopReport.Reason);
        }

        Assert.Contains("group not empty", launcher.StopReport.Reason);
        Assert.Contains(host.Rows, row => row.Process.Identity == Client.Process.Identity);
    }

    [Fact]
    public async Task Proton_game_exe_and_install_launcher_are_stopped_individually_in_one_fresh_group()
    {
        var host = new FakeHost();
        host.OnRun = () => host.Rows.AddRange([
            Entry(20, 2, 20, "/steam/pv-adverb"),
            Entry(22, 2, 20, Proton.ProtonPrefix + "/drive_c/Game.exe"),
            Entry(23, 2, 20, Proton.App.InstallPath + "/Game.bin"),
        ]);
        await using var launcher = Launcher(host, Proton);
        var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        Assert.Equal(20, game.Identity.ProcessGroupId);
        await launcher.StopAsync(game, CancellationToken.None);
        Assert.Equal([22, 23], host.Signalled.Select(identity => identity.ProcessId).Order());
        Assert.DoesNotContain(host.Signalled, identity => identity.ProcessId == 20);
        Assert.Equal([new ProcessIdentity(20, 2, 20)], launcher.StopReport!.HelperSurvivors);
        Assert.Contains(host.Rows, row => row.Process.Identity == Client.Process.Identity);
    }

    [Fact]
    public async Task Fresh_unreadable_helper_does_not_degrade_tracking_and_is_never_signalled()
    {
        var host = new FakeHost();
        host.OnRun = () => host.Rows.AddRange([
            Entry(20, 2, 20, "/steam/pv-adverb"), Entry(21, 2, 20, "/steam/wineserver"),
            Entry(22, 2, 20, Native.App.InstallPath + "/game"), Entry(24, 3, 20, null),
        ]);
        await using var launcher = Launcher(host, Native);
        var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        var snapshot = await launcher.SnapshotAsync(CancellationToken.None);
        Assert.True(snapshot.IsComplete);
        Assert.Equal([22], snapshot.Processes.Select(row => row.Identity.ProcessId));
        await launcher.StopAsync(game, CancellationToken.None);
        Assert.Equal([22], host.Signalled.Select(identity => identity.ProcessId));
        Assert.Equal([20, 21], launcher.StopReport!.HelperSurvivors.Select(identity => identity.ProcessId).Order());
        Assert.Equal([24], launcher.StopReport.UnidentifiedSurvivors.Select(identity => identity.ProcessId).Order());
        Assert.Contains("manual inspection required", launcher.StopReport.Reason);
    }

    [Fact]
    public async Task Unreadable_survivor_without_evidence_is_unidentified_and_not_a_helper_claim()
    {
        var host = new FakeHost();
        host.OnRun = () => host.Rows.AddRange([
            Entry(22, 2, 20, Native.App.InstallPath + "/game"),
            Entry(24, 2, 20, null),
        ]);
        await using var launcher = Launcher(host, Native);
        var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        await launcher.StopAsync(game, CancellationToken.None);
        Assert.Equal([22], host.Signalled.Select(identity => identity.ProcessId));
        var report = launcher.StopReport!;
        Assert.True(report.IsDegraded);
        Assert.Empty(report.HelperSurvivors);
        Assert.Equal([new ProcessIdentity(24, 2, 20)], report.UnidentifiedSurvivors);
        Assert.Contains("unidentified survivor(s) pid=24", report.Reason);
        Assert.Contains("manual inspection required", report.Reason);
        Assert.DoesNotContain("helper", report.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unreadable_survivor_with_observed_steam_parent_is_an_identified_helper()
    {
        var host = new FakeHost();
        host.OnRun = () => host.Rows.AddRange([
            Entry(20, 2, 20, "/steam/pv-adverb"),
            Entry(22, 2, 20, Native.App.InstallPath + "/game"),
        ]);
        await using var launcher = Launcher(host, Native);
        var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        // Its /proc/PID/exe stays unreadable; only the observed parent names it as Steam-owned.
        host.Rows.Add(Entry(24, 2, 20, null, parent: 20));
        await launcher.StopAsync(game, CancellationToken.None);
        var report = launcher.StopReport!;
        Assert.True(report.IsDegraded);
        Assert.Equal([20, 24], report.HelperSurvivors.Select(identity => identity.ProcessId).Order());
        Assert.Empty(report.UnidentifiedSurvivors);
    }

    [Fact]
    public async Task Unreadable_game_executable_is_torn_evidence_not_a_changed_identity_or_exit()
    {
        var host = Host(Native);
        var launcher = new SteamProcessLauncher(host, Native, new()
        {
            SteamExecutable = "/usr/bin/steam", ProtocolOpener = "/usr/bin/xdg-open",
            PollInterval = TimeSpan.FromMilliseconds(1), AttributionWindow = TimeSpan.FromSeconds(1),
            StopTimeout = TimeSpan.FromMilliseconds(20),
        });
        try
        {
            var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
            // The same PID/start/PGID, mid-exit: /proc/PID/exe no longer resolves.
            host.Rows.RemoveAll(row => row.Process.Identity.ProcessId == 22);
            host.Rows.Add(Entry(22, 2, 20, null));
            var snapshot = await launcher.SnapshotAsync(CancellationToken.None);
            Assert.False(snapshot.IsComplete);
            Assert.Contains("unreadable", snapshot.IncompleteReason);
            var error = await Assert.ThrowsAsync<ProcessSurvivorException>(
                () => launcher.StopAsync(game, CancellationToken.None));
            Assert.Contains("stayed unreadable", error.Message);
            Assert.Null(launcher.StopReport);
            Assert.Empty(host.Signalled);
        }
        finally
        {
            host.Rows.RemoveAll(row => row.Process.Identity.ProcessId == 22);
            await launcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task Incomplete_sample_after_attribution_is_not_a_natural_exit_and_never_signals()
    {
        var host = Host(Native);
        await using var launcher = Launcher(host, Native);
        var process = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        await using var tracker = new GameSessionTracker(launcher, launcher);
        var events = new List<SessionStateChanged>();
        tracker.StateChanged += (_, change) => { lock (events) { events.Add(change); } };
        SessionStateChanged[] Recorded()
        {
            lock (events) { return [.. events]; }
        }

        // First let the tracker observe the live game, so a later torn sample is the only
        // evidence the exit path could ever use.
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnSnapshot = () => observed.TrySetResult();
        using var cancellation = new CancellationTokenSource();
        var tracking = tracker.TrackAsync(process,
            new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(1) }, cancellation.Token);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GameSessionState.Running, tracker.State);
        host.OnSnapshot = null;

        // The sample becomes unreadable and its rows vanish: a torn /proc read is not an exit.
        host.Complete = false;
        host.Rows.Clear();
        var torn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnSnapshot = () => torn.TrySetResult();
        await torn.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50); // Many more torn samples: the exit confirmation can never be reached.
        host.OnSnapshot = null;

        var snapshot = await launcher.SnapshotAsync(CancellationToken.None);
        Assert.False(snapshot.IsComplete);
        Assert.NotNull(snapshot.IncompleteReason);
        Assert.Equal(GameSessionState.Running, tracker.State);
        Assert.True(tracker.IsDegraded);
        Assert.Empty(host.Signalled);
        Assert.DoesNotContain(Recorded(), change => change.State is GameSessionState.Stopping
            or GameSessionState.Stopped or GameSessionState.Cancelled or GameSessionState.Failed);

        // /proc recovers and the game is still running; the session must stop it, not assume exit.
        host.Rows.AddRange([
            Entry(20, 2, 20, "/steam/pv-adverb"),
            Entry(21, 2, 20, "/steam/wineserver"),
            Entry(22, 2, 20, Native.App.InstallPath + "/game"),
        ]);
        host.Complete = true;
        var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnSnapshot = () => healthy.TrySetResult();
        await healthy.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        host.OnSnapshot = null;
        Assert.Equal(GameSessionState.Running, tracker.State);
        Assert.False(tracker.IsDegraded);

        Assert.True(await tracker.StopAsync(CancellationToken.None));
        await tracking;
        Assert.Equal(GameSessionState.Stopped, tracker.State);
        Assert.Equal([22], host.Signalled.Select(identity => identity.ProcessId));
        Assert.Equal([20, 21], launcher.StopReport!.HelperSurvivors.Select(identity => identity.ProcessId).Order());
    }

    [Fact]
    public async Task Tracker_disposal_stops_game_identities_but_not_steam_helpers()
    {
        var host = Host(Native);
        await using var launcher = Launcher(host, Native);
        var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        var tracker = new GameSessionTracker(launcher, launcher);
        var tracking = tracker.TrackAsync(game, new SessionOptions(), CancellationToken.None);
        await tracker.DisposeAsync();
        await tracking;
        Assert.Equal(GameSessionState.Cancelled, tracker.State);
        AssertOnlyClientAndUnsignalledHelpersRemain(host);
        Assert.True(launcher.StopReport!.IsDegraded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preexisting_or_client_shared_group_is_unknown_and_receives_zero_signals(bool client)
    {
        var host = new FakeHost();
        var group = client ? 10 : 20;
        if (!client) { host.Rows.Add(Entry(20, 1, group, "/steam/pv-adverb")); }
        host.OnRun = () => host.Rows.Add(Entry(22, 2, group, Native.App.InstallPath + "/game"));
        await using var launcher = Launcher(host, Native, window: TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAsync<SteamAttributionUnknownException>(() => launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None));
        Assert.Empty(host.Signalled);
        Assert.Null(launcher.StopReport);
        Assert.Contains(Client, host.Rows);
        Assert.Contains(host.Rows, row => row.Process.Identity.ProcessId == 22); // Owner must stop unknown games.
    }

    [Fact]
    public async Task Surviving_game_is_reported_and_is_not_a_clean_stop()
    {
        var host = Host(Native);
        host.IgnoreSignal = true;
        var launcher = new SteamProcessLauncher(host, Native, new()
        {
            ProtocolOpener = "/usr/bin/xdg-open", PollInterval = TimeSpan.FromMilliseconds(1),
            StopTimeout = TimeSpan.FromMilliseconds(10),
        });
        try
        {
            var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
            var error = await Assert.ThrowsAsync<ProcessSurvivorException>(() => launcher.StopAsync(game, CancellationToken.None));
            Assert.Contains("could not be proven", error.Message);
            Assert.Null(launcher.StopReport);
            Assert.All(host.Signalled, identity => Assert.Equal(game.Identity, identity));
        }
        finally
        {
            host.Rows.RemoveAll(row => row.Process.Identity.ProcessId == 22); // Owner eventually stops the survivor.
            await launcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task Incomplete_baseline_does_not_even_send_a_request()
    {
        var host = new FakeHost { Complete = false };
        await using var launcher = Launcher(host, Native);
        await Assert.ThrowsAsync<SteamAttributionUnknownException>(() => launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None));
        Assert.Null(host.Request);
    }

    [Fact]
    public async Task Nonzero_request_is_not_a_successful_game_start()
    {
        var host = new FakeHost { ExitCode = 1 };
        await using var launcher = Launcher(host, Native);
        await Assert.ThrowsAsync<SteamAttributionUnknownException>(() => launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None));
        Assert.Empty(host.Signalled);
    }

    [Fact]
    public async Task Stop_refuses_a_group_that_became_shared_and_can_retry_after_owner_removes_the_intruder()
    {
        var host = Host(Native);
        await using var launcher = Launcher(host, Native);
        var process = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        host.Rows.Add(Entry(99, 3, 20, "/unrelated/application"));
        await Assert.ThrowsAsync<SteamAttributionUnknownException>(() => launcher.StopAsync(process, CancellationToken.None));
        Assert.Empty(host.Signalled);
        host.Rows.RemoveAll(row => row.Process.Identity.ProcessId == 99);
        await launcher.StopAsync(process, CancellationToken.None);
    }

    [Fact]
    public async Task Escaped_observed_child_degrades_and_is_never_signalled_in_an_unrelated_group()
    {
        var host = Host(Native);
        await using var launcher = Launcher(host, Native);
        var process = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        host.Rows.RemoveAll(row => row.Process.Identity.ProcessId == 22);
        host.Rows.Add(Entry(22, 2, 99, Native.App.InstallPath + "/game"));
        Assert.False((await launcher.SnapshotAsync(CancellationToken.None)).IsComplete);
        await Assert.ThrowsAsync<ProcessSurvivorException>(() => launcher.StopAsync(process, CancellationToken.None));
        Assert.Empty(host.Signalled);
        host.Rows.RemoveAll(row => row.Process.Identity.ProcessId == 22); // Owner manually stops escaped game.
    }

    [Fact]
    public async Task Group_reuse_does_not_authorize_signals()
    {
        var host = Host(Native);
        await using var launcher = Launcher(host, Native);
        var process = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        host.Rows.RemoveAll(row => row.Process.Identity.ProcessGroupId == 20);
        host.Rows.Add(Entry(22, 999, 20, Native.App.InstallPath + "/game"));
        await Assert.ThrowsAsync<SteamAttributionUnknownException>(() => launcher.StopAsync(process, CancellationToken.None));
        Assert.Empty(host.Signalled);
        host.Rows.RemoveAll(row => row.Process.Identity.ProcessGroupId == 20);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tracker_manual_stop_and_cancel_reap_the_attributed_group_only(bool cancel)
    {
        var host = Host(Native);
        await using var launcher = Launcher(host, Native);
        var process = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        await using var tracker = new GameSessionTracker(launcher, launcher);
        using var source = new CancellationTokenSource();
        var task = tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(1) }, source.Token);
        Assert.Equal(GameSessionState.Running, tracker.State);
        if (cancel)
        {
            source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(GameSessionState.Cancelled, tracker.State);
        }
        else
        {
            Assert.True(await tracker.StopAsync(CancellationToken.None));
            await task;
            Assert.Equal(GameSessionState.Stopped, tracker.State);
        }

        AssertOnlyClientAndUnsignalledHelpersRemain(host);
    }

    [Fact]
    public async Task Cancellation_before_attribution_never_kills_the_unattributed_client()
    {
        using var source = new CancellationTokenSource();
        var host = new FakeHost { OnRun = () => source.Cancel() };
        await using var launcher = Launcher(host, Native);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.StartAsync(launcher.CreateRequest(), source.Token));
        Assert.Empty(host.Signalled);
    }

    [Fact]
    public async Task Cancelled_manual_stop_still_reaps_before_propagating_cancellation()
    {
        var host = Host(Native);
        await using var launcher = Launcher(host, Native);
        var process = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.StopAsync(process, source.Token));
        AssertOnlyClientAndUnsignalledHelpersRemain(host);
    }

    [Fact]
    public async Task Cancellation_at_the_attribution_snapshot_cleans_up_the_newly_owned_group()
    {
        using var source = new CancellationTokenSource();
        var host = Host(Native);
        host.OnSnapshot = () =>
        {
            if (host.Request is not null)
            {
                source.Cancel();
            }
        };
        await using var launcher = Launcher(host, Native);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.StartAsync(launcher.CreateRequest(), source.Token));
        AssertOnlyClientAndUnsignalledHelpersRemain(host);
    }

    [Fact]
    public async Task Disposal_cancels_an_inflight_request_without_killing_the_client()
    {
        var host = new FakeHost { WaitInRun = true };
        var launcher = Launcher(host, Native);
        var start = launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        await host.RunEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await launcher.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Empty(host.Signalled);
    }

    [Fact]
    public async Task Disposal_reaps_an_attributed_group()
    {
        var host = Host(Native);
        var launcher = Launcher(host, Native);
        _ = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        await launcher.DisposeAsync();
        AssertOnlyClientAndUnsignalledHelpersRemain(host);
    }

    [Fact]
    public void Prefix_directory_alone_does_not_select_proton_and_missing_evidence_is_unknown()
    {
        var manifest = SteamManifestParser.Parse(Encoding.UTF8.GetBytes("AppState { appid 42 name Game installdir Game StateFlags 4 }"), "appmanifest_42.acf").Value!;
        Assert.Equal(SteamExecutionKind.Unknown, SteamExecutionSelection.Evaluate(manifest, null, true, false).Kind);
        Assert.Equal(SteamExecutionKind.Proton, SteamExecutionSelection.Evaluate(manifest, null, true, true).Kind);
        var compat = KeyValues.Parse(Encoding.UTF8.GetBytes("platform_overrides { 42 { src windows dest linux } }"), "compat.vdf").Object;
        var evidence = SteamExecutionSelection.Evaluate(manifest, compat, false, false);
        Assert.Equal(SteamExecutionKind.Proton, evidence.Kind);
        Assert.Contains("not yet verified", evidence.Reason);
    }

    [Fact]
    public void Explicit_linux_selection_is_evidence_but_conflicting_prefix_markers_take_precedence()
    {
        var manifest = SteamManifestParser.Parse(Encoding.UTF8.GetBytes("AppState { appid 42 name Game installdir Game StateFlags 4 }"), "appmanifest_42.acf").Value!;
        var compat = KeyValues.Parse(Encoding.UTF8.GetBytes("platform_overrides { 42 { src linux dest linux } }"), "compat.vdf").Object;
        Assert.Equal(SteamExecutionKind.Native, SteamExecutionSelection.Evaluate(manifest, compat, false, false).Kind);
        Assert.Equal(SteamExecutionKind.Proton, SteamExecutionSelection.Evaluate(manifest, compat, true, true).Kind);
    }

    [Fact]
    public void Owner_executable_selector_only_narrows_the_existing_install_or_prefix_match()
    {
        Assert.False(SteamProcessAttribution.Matches(Native, "/explicit/selected-game"));
        Assert.False(SteamProcessAttribution.Matches(Native with { ExecutableOverride = "/explicit/selected-game" }, "/explicit/selected-game"));
        Assert.False(SteamProcessAttribution.Matches(Native with { ExecutableOverride = "/explicit/steam" }, "/explicit/steam"));
        var selected = Native.App.InstallPath + "/selected-game";
        Assert.True(SteamProcessAttribution.Matches(Native with { ExecutableOverride = selected }, selected));
        Assert.False(SteamProcessAttribution.Matches(Native with { ExecutableOverride = selected }, Native.App.InstallPath + "/other-game"));
        Assert.True(SteamProcessAttribution.Matches(Proton with { ExecutableOverride = Proton.ProtonPrefix + "/game.exe" }, Proton.ProtonPrefix + "/game.exe"));
    }

    [Theory]
    [InlineData("/usr/bin/bash")]
    [InlineData("/usr/bin/selected-game")]
    [InlineData("/bin/tool")]
    [InlineData("/sbin/tool")]
    [InlineData("/library/steamapps/common/Game with spaces/bash")]
    [InlineData("/library/steamapps/common/Game with spaces/pv-adverb")]
    public void Owner_selector_rejects_shell_system_and_helper_binaries(string executable)
    {
        Assert.False(SteamProcessAttribution.Matches(Native with { ExecutableOverride = executable }, executable));
        Assert.Throws<ArgumentException>(() => Launcher(new FakeHost(), Native with { ExecutableOverride = executable }));
    }

    [Fact]
    public void Owner_selector_cannot_weaken_unique_fresh_group_requirements()
    {
        var executable = Native.App.InstallPath + "/selected-game";
        var target = Native with { ExecutableOverride = executable };
        Assert.Null(SteamProcessAttribution.Find(target, Before, new([Client, Entry(22, 2, 10, executable)], true)));
        Assert.Null(SteamProcessAttribution.Find(target, Before,
            new([Client, Entry(22, 2, 20, executable), Entry(23, 3, 30, executable)], true)));
        Assert.Null(SteamProcessAttribution.Find(target, new([Client, Entry(20, 1, 20, "/existing")], true),
            new([Client, Entry(22, 2, 20, executable)], true)));
    }

    private static ExternalProcessEntry Entry(int pid, long ticks, int group, string? executable, int parent = 1) =>
        new(new(new(pid, ticks, group), parent, true), executable);

    private static void AssertOnlyClientAndUnsignalledHelpersRemain(FakeHost host)
    {
        Assert.Equal([10, 20, 21], host.Rows.Select(row => row.Process.Identity.ProcessId).Order());
        Assert.Equal(Client, host.Rows.Single(row => row.Process.Identity.ProcessId == 10));
        Assert.All(host.Rows, row => Assert.True(SteamProcessAttribution.IsInfrastructure(row.Executable)));
        Assert.Equal([22], host.Signalled.Select(identity => identity.ProcessId));
    }

    private static FakeHost Host(SteamLaunchTarget target)
    {
        var host = new FakeHost();
        host.OnRun = () => host.Rows.AddRange([
            Entry(20, 2, 20, "/steam/pv-adverb"),
            Entry(21, 2, 20, "/steam/wineserver"),
            Entry(22, 2, 20, target.ProtonPrefix is { } prefix ? prefix + "/drive_c/game.exe" : target.App.InstallPath + "/game"),
        ]);
        return host;
    }

    private static SteamProcessLauncher Launcher(FakeHost host, SteamLaunchTarget target,
        string? steam = "/usr/bin/steam", TimeSpan? window = null) => new(host, target, new()
        {
            SteamExecutable = steam, ProtocolOpener = "/usr/bin/xdg-open",
            PollInterval = TimeSpan.FromMilliseconds(1), AttributionWindow = window ?? TimeSpan.FromSeconds(1),
        });

    private sealed class FakeHost : IExternalProcessHost
    {
        public List<ExternalProcessEntry> Rows { get; } = [Client];
        public List<ProcessIdentity> Signalled { get; } = [];
        public LaunchRequest? Request { get; private set; }
        public Action? OnRun { get; set; }
        public Action? OnSnapshot { get; set; }
        public bool Complete { get; set; } = true;
        public int ExitCode { get; set; }
        public bool WaitInRun { get; set; }
        public bool IgnoreSignal { get; set; }
        public TaskCompletionSource RunEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OnSnapshot?.Invoke();
            return Task.FromResult(new ExternalProcessSnapshot(Rows.ToArray(), Complete));
        }

        public async Task<int> RunAsync(LaunchRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Request = request;
            RunEntered.TrySetResult();
            OnRun?.Invoke();
            if (WaitInRun)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return ExitCode;
        }

        public Task SignalAsync(ProcessIdentity identity, CancellationToken token)
            => throw new InvalidOperationException("Steam must use executable-verified identity stop.");

        public Task SignalAsync(ProcessIdentity identity, string expectedExecutable, CancellationToken token)
        {
            Assert.Equal(expectedExecutable, Rows.Single(row => row.Process.Identity == identity).Executable);
            Assert.False(SteamProcessAttribution.IsInfrastructure(expectedExecutable));
            Signalled.Add(identity);
            if (!IgnoreSignal) { Rows.RemoveAll(row => row.Process.Identity == identity); }
            return Task.CompletedTask;
        }
    }

    private sealed class InlineProgress(Action<SteamAttributionProgress> report) : IProgress<SteamAttributionProgress>
    {
        public void Report(SteamAttributionProgress value) => report(value);
    }
}
