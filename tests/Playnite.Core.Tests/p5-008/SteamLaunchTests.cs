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
        Assert.Equal([20, 21, 22], host.Signalled.Select(identity => identity.ProcessId).Order());
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
        Assert.DoesNotContain(host.Rows, row => row.Process.Identity.ProcessGroupId == 20 && row.Process.IsRunning);
        Assert.DoesNotContain(host.Signalled, identity => identity.ProcessId == 10);
    }

    [Theory]
    [InlineData("shared-client")]
    [InlineData("shared-existing")]
    [InlineData("multiple")]
    [InlineData("unreadable")]
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
            case "unreadable": after = new([Client, game, Entry(23, 3, 20, null)], true); break;
            case "incomplete": after = after with { IsComplete = false }; break;
            case "unrelated-member": after = new([Client, game, Entry(23, 3, 20, "/unrelated/tool")], true); break;
            case "no-game": after = Before; break;
            case "preexisting-game": before = after; break;
        }

        Assert.Null(SteamProcessAttribution.Find(Native, before, after));
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

        Assert.Single(host.Rows);
        Assert.Equal(Client, host.Rows[0]);
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
        Assert.Single(host.Rows);
        Assert.Equal(Client, host.Rows[0]);
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
        Assert.Single(host.Rows);
        Assert.Equal(Client, host.Rows[0]);
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
        Assert.Single(host.Rows);
        Assert.Equal(Client, host.Rows[0]);
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
    public void Owner_executable_override_is_opt_in_and_cannot_bypass_infrastructure_exclusion()
    {
        Assert.False(SteamProcessAttribution.Matches(Native, "/explicit/selected-game"));
        Assert.True(SteamProcessAttribution.Matches(Native with { ExecutableOverride = "/explicit/selected-game" }, "/explicit/selected-game"));
        Assert.False(SteamProcessAttribution.Matches(Native with { ExecutableOverride = "/explicit/steam" }, "/explicit/steam"));
    }

    private static ExternalProcessEntry Entry(int pid, long ticks, int group, string? executable) =>
        new(new(new(pid, ticks, group), 1, true), executable);

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
        {
            Signalled.Add(identity);
            Rows.RemoveAll(row => row.Process.Identity == identity);
            return Task.CompletedTask;
        }
    }
}
