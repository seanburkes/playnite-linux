using System.Diagnostics;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Sessions;
using Playnite.Platform.Sessions;
using Playnite.Steam;
using Xunit;

namespace Playnite.Core.Tests.Steam;

public sealed class LinuxPidFdSignalTests
{
    [Fact]
    public async Task Scoped_signal_rechecks_executable_after_pinning_and_rejects_exec_changes()
    {
        var executable = "/games/game";
        var state = new FakeState { AfterOpen = () => executable = "/steam/steam" };
        var host = new LinuxExternalProcessHost(state, state, _ => executable);
        await host.SignalAsync(new(22, 1, 20), "/games/game", CancellationToken.None);
        Assert.Empty(state.SignalledGenerations);
        Assert.Equal(1, state.Opens);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Exec_after_the_stat_identity_check_is_rejected_by_the_final_executable_read()
    {
        var executable = "/games/game";
        var state = new FakeState { AfterRead = () => executable = "/steam/steam" };
        var host = new LinuxExternalProcessHost(state, state, _ => executable);
        await host.SignalAsync(new(22, 1, 20), "/games/game", CancellationToken.None);
        Assert.Empty(state.SignalledGenerations);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Pid_reuse_during_the_final_executable_read_cannot_redirect_the_pinned_signal()
    {
        var state = new FakeState();
        var host = new LinuxExternalProcessHost(state, state, _ => { state.Ticks = 2; return "/games/game"; });
        await host.SignalAsync(new(22, 1, 20), "/games/game", CancellationToken.None);
        // The pinned handle still targets the verified process, never the recycled PID.
        Assert.Equal([1L], state.SignalledGenerations);
        Assert.Equal(2, state.Ticks);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Scoped_signal_rechecks_the_exact_proton_command_line_argument()
    {
        const string argument = "S:\\steamapps\\common\\Hades\\x64\\Hades.exe";
        var state = new FakeState();
        var host = new LinuxExternalProcessHost(state, state, _ => "/proton/wine64-preloader",
            _ => [argument, "--later-arg"]);
        await host.SignalAsync(new(22, 1, 20), "/proton/wine64-preloader", argument, CancellationToken.None);
        Assert.Equal([1L], state.SignalledGenerations);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Scoped_signal_rejects_the_game_path_outside_argv0()
    {
        const string argument = "S:\\steamapps\\common\\Hades\\x64\\Hades.exe";
        var state = new FakeState();
        var host = new LinuxExternalProcessHost(state, state, _ => "/proton/wine64-preloader",
            _ => ["C:\\windows\\system32\\services.exe", argument]);
        await host.SignalAsync(new(22, 1, 20), "/proton/wine64-preloader", argument, CancellationToken.None);
        Assert.Empty(state.SignalledGenerations);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Scoped_signal_rejects_a_proton_argument_that_changed_before_SIGTERM()
    {
        var argument = "S:\\steamapps\\common\\Hades\\x64\\Hades.exe";
        var state = new FakeState
        {
            AfterRead = () => argument = "S:\\steamapps\\common\\Other\\Other.exe",
        };
        var host = new LinuxExternalProcessHost(state, state, _ => "/proton/wine64-preloader",
            _ => [argument]);
        await host.SignalAsync(new(22, 1, 20), "/proton/wine64-preloader",
            "S:\\steamapps\\common\\Hades\\x64\\Hades.exe", CancellationToken.None);
        Assert.Empty(state.SignalledGenerations);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Scoped_signal_rejects_an_unreadable_proton_command_line()
    {
        var state = new FakeState();
        var host = new LinuxExternalProcessHost(state, state, _ => "/proton/wine64-preloader", _ => null);
        await host.SignalAsync(new(22, 1, 20), "/proton/wine64-preloader",
            "S:\\steamapps\\common\\Hades\\x64\\Hades.exe", CancellationToken.None);
        Assert.Empty(state.SignalledGenerations);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Steam_individual_stop_terminates_a_real_game_child_in_a_nonowned_group()
    {
        var directory = Directory.CreateTempSubdirectory("playnite-p5-008b-child-").FullName;
        var target = new SteamLaunchTarget(new SteamApp(42, "Test", "Game", directory,
            SteamAppState.Installed, false, null, null, 4));
        var install = target.App.InstallPath;
        Directory.CreateDirectory(install);
        var gameExecutable = Path.Combine(install, "game");
        File.Copy("/usr/bin/sleep", gameExecutable);
        File.SetUnixFileMode(gameExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var host = new ChildHost(gameExecutable);
        var launcher = new SteamProcessLauncher(host, target, new()
        {
            ProtocolOpener = "/usr/bin/xdg-open", PollInterval = TimeSpan.FromMilliseconds(1),
        });
        try
        {
            var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await launcher.StopAsync(game, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await host.Child!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(143, host.Child.ExitCode);
            Assert.Equal([game.Identity], host.Signalled);
            Assert.Empty((await host.SnapshotAsync(CancellationToken.None)).Processes);
            Assert.False(launcher.StopReport!.IsDegraded);
        }
        finally
        {
            await launcher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Pid_recycled_while_opening_is_rejected_by_final_identity_check()
    {
        var state = new FakeState();
        state.AfterOpen = () => state.Ticks = 2;
        var host = new LinuxExternalProcessHost(state, state);
        await host.SignalAsync(new(22, 1, 20), CancellationToken.None);
        Assert.Empty(state.SignalledGenerations);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Pid_recycled_after_final_check_cannot_redirect_the_pinned_signal()
    {
        var state = new FakeState();
        state.AfterRead = () => state.Ticks = 2;
        var host = new LinuxExternalProcessHost(state, state);
        await host.SignalAsync(new(22, 1, 20), CancellationToken.None);
        Assert.Equal([1L], state.SignalledGenerations);
        Assert.Equal(2, state.Ticks);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Process_that_left_the_authorized_group_is_not_signalled()
    {
        var state = new FakeState { Group = 99 };
        var host = new LinuxExternalProcessHost(state, state);
        await host.SignalAsync(new(22, 1, 20), CancellationToken.None);
        Assert.Empty(state.SignalledGenerations);
        Assert.Equal(1, state.Disposals);
    }

    [Fact]
    public async Task Same_start_ticks_in_a_changed_group_is_retained_as_an_anomaly_and_complete()
    {
        // The observer read says pgid=20; the host's identity re-read says the same pid and start
        // ticks are now in pgid=30 (setsid). Dropping that row left a complete, empty snapshot.
        var reader = new GroupChangeStatReader(Environment.ProcessId, ticks: 2, firstGroup: 20, secondGroup: 30);
        var host = new LinuxExternalProcessHost(reader, null, _ => "/library/game");

        var snapshot = await host.SnapshotAsync(CancellationToken.None);

        // A move is reported as an anomaly, not as whole-table incompleteness: unrelated churn
        // must not abort a launch or a stop.
        Assert.True(snapshot.IsComplete);
        var row = Assert.Single(snapshot.Processes);
        Assert.Equal(new ProcessIdentity(Environment.ProcessId, 2, 30), row.Process.Identity);
        Assert.Equal(row.Process.Identity, Assert.Single(snapshot.Moved));
        Assert.True(row.Process.IsRunning);
    }

    [Fact]
    public async Task Launcher_seam_a_group_change_between_reads_cannot_produce_a_clean_stop()
    {
        var target = new SteamLaunchTarget(new SteamApp(42, "Synthetic", "Game", "/library",
            SteamAppState.Installed, false, null, null, 4));
        var reader = new SeamStatReader(Environment.ProcessId, ticks: 2, group: 20, escapedGroup: 30);
        var seam = new SeamHost(reader, target.App.InstallPath + "/game");
        var launcher = new SteamProcessLauncher(seam, target, new()
        {
            ProtocolOpener = "/usr/bin/xdg-open", PollInterval = TimeSpan.FromMilliseconds(1),
            AttributionWindow = TimeSpan.FromSeconds(1), StopTimeout = TimeSpan.FromMilliseconds(20),
        });

        var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        Assert.Equal(20, game.Identity.ProcessGroupId);
        reader.Escaped = true;

        var snapshot = await launcher.SnapshotAsync(CancellationToken.None);
        Assert.False(snapshot.IsComplete);
        Assert.Contains("left its group", snapshot.IncompleteReason);

        // The old shape produced a complete empty snapshot and therefore a clean "group empty"
        // report with zero signals while the game lived on in pgid=30. A tracked identity that
        // escaped its group is a survivor, never a terminal report.
        await Assert.ThrowsAsync<ProcessSurvivorException>(
            () => launcher.StopAsync(game, CancellationToken.None));
        Assert.Null(launcher.StopReport);
        Assert.Empty(seam.Signalled);
    }

    [Fact]
    public async Task Cancellation_before_pinning_never_opens_a_handle_or_signals()
    {
        var state = new FakeState();
        var host = new LinuxExternalProcessHost(state, state);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.SignalAsync(new(22, 1, 20), source.Token));
        Assert.Equal(0, state.Opens);
        Assert.Empty(state.SignalledGenerations);
    }

    [Fact]
    public async Task Cancellation_during_final_identity_read_closes_the_pinned_handle_without_signalling()
    {
        using var source = new CancellationTokenSource();
        var state = new FakeState { AfterOpen = () => source.Cancel() };
        var host = new LinuxExternalProcessHost(state, state);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.SignalAsync(new(22, 1, 20), source.Token));
        Assert.Equal(1, state.Disposals);
        Assert.Empty(state.SignalledGenerations);
    }

    [Fact]
    public async Task Production_pidfd_gracefully_stops_only_the_owned_test_child()
    {
        using var child = Process.Start(new ProcessStartInfo("/usr/bin/sleep")
        {
            UseShellExecute = false, ArgumentList = { "30" },
        }) ?? throw new IOException("Test child did not start.");
        try
        {
            var stat = await new LinuxProcStatReader().ReadAsync(child.Id, CancellationToken.None);
            Assert.NotNull(stat);
            var row = stat.Value;
            await new LinuxExternalProcessHost().SignalAsync(new(child.Id, row.StartTimeTicks, row.ProcessGroupId), CancellationToken.None);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
            Assert.Equal(143, child.ExitCode); // SIGTERM, not SIGKILL (137).
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(); // Cleanup only our artificial sleep child after a failed test.
                await child.WaitForExitAsync();
            }
        }
    }

    /// <summary>Host-level seam: the observer read and the identity re-read disagree on PGID.</summary>
    private sealed class GroupChangeStatReader(int processId, long ticks, int firstGroup, int secondGroup)
        : IProcStatReader
    {
        private int reads;

        public Task<ProcStat?> ReadAsync(int pid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pid != processId)
            {
                return Task.FromResult<ProcStat?>(null);
            }

            var group = Interlocked.Increment(ref reads) == 1 ? firstGroup : secondGroup;
            return Task.FromResult<ProcStat?>(new ProcStat(pid, 1, group, 'S', ticks));
        }
    }

    /// <summary>Launcher seam: a live target appears on launch, then moves group mid-read.</summary>
    private sealed class SeamStatReader(int processId, long ticks, int group, int escapedGroup) : IProcStatReader
    {
        private int escapeReads;

        public int ProcessId { get; } = processId;
        public volatile bool Present;
        public volatile bool Escaped;

        public Task<ProcStat?> ReadAsync(int pid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pid != ProcessId || !Present)
            {
                return Task.FromResult<ProcStat?>(null);
            }

            // Once escaped, the observer still reads the old group and the host re-read sees the
            // new one, reproducing the exact two-read disagreement in every snapshot.
            var read = Escaped ? Interlocked.Increment(ref escapeReads) : 1;
            var row = Escaped && read % 2 == 0 ? escapedGroup : group;
            return Task.FromResult<ProcStat?>(new ProcStat(pid, 1, row, 'S', ticks));
        }
    }

    private sealed class SeamHost(SeamStatReader reader, string executable) : IExternalProcessHost
    {
        private readonly LinuxExternalProcessHost host =
            new(reader, null, pid => pid == reader.ProcessId ? executable : null);

        public List<ProcessIdentity> Signalled { get; } = [];

        public Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
            host.SnapshotAsync(cancellationToken);

        public Task<int> RunAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            reader.Present = true;
            return Task.FromResult(0);
        }

        public Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unscoped signal forbidden.");

        public Task SignalAsync(ProcessIdentity identity, string expectedExecutable,
            CancellationToken cancellationToken)
        {
            Signalled.Add(identity);
            reader.Present = false;
            return Task.CompletedTask;
        }

        public Task SignalAsync(ProcessIdentity identity, string expectedExecutable,
            string? expectedCommandLineArgument, CancellationToken cancellationToken)
        {
            Signalled.Add(identity);
            reader.Present = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeState : IProcStatReader, IProcessSignalHandles
    {
        public long Ticks { get; set; } = 1;
        public int Group { get; init; } = 20;
        public Action? AfterOpen { get; set; }
        public Action? AfterRead { get; set; }
        public int Opens { get; private set; }
        public int Disposals { get; private set; }
        public List<long> SignalledGenerations { get; } = [];

        public Task<ProcStat?> ReadAsync(int processId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcStat? snapshot = new(processId, 1, Group, 'S', Ticks);
            AfterRead?.Invoke();
            return Task.FromResult(snapshot);
        }

        public IProcessSignalHandle Open(int processId)
        {
            Assert.Equal(22, processId);
            Opens++;
            var handle = new Handle(this, Ticks);
            AfterOpen?.Invoke();
            return handle;
        }

        private sealed class Handle(FakeState state, long pinnedGeneration) : IProcessSignalHandle
        {
            public void Terminate() => state.SignalledGenerations.Add(pinnedGeneration);
            public void Dispose() => state.Disposals++;
        }
    }

    private sealed class ChildHost(string executable) : IExternalProcessHost, IDisposable
    {
        private readonly LinuxExternalProcessHost host = new();
        public string Executable { get; set; } = executable;
        public Process? Child { get; private set; }
        public List<ProcessIdentity> Signalled { get; } = [];
        public async Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
        {
            if (Child is null || Child.HasExited) { return new([], true); }
            var stat = await new LinuxProcStatReader().ReadAsync(Child.Id, cancellationToken);
            if (stat is not { } row || !row.IsRunning) { return new([], true); }
            return new([new(new(new(row.ProcessId, row.StartTimeTicks, row.ProcessGroupId), row.ParentProcessId, true), Executable)], true);
        }
        public Task<int> RunAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            Child = Process.Start(new ProcessStartInfo(Executable) { UseShellExecute = false, ArgumentList = { "5" } });
            return Task.FromResult(Child is null ? 1 : 0);
        }
        public Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unscoped stop forbidden.");
        public Task SignalAsync(ProcessIdentity identity, string expectedExecutable, CancellationToken cancellationToken)
        {
            Signalled.Add(identity);
            return host.SignalAsync(identity, expectedExecutable, cancellationToken);
        }

        public Task SignalAsync(ProcessIdentity identity, string expectedExecutable,
            string? expectedCommandLineArgument, CancellationToken cancellationToken)
        {
            Signalled.Add(identity);
            return host.SignalAsync(identity, expectedExecutable, expectedCommandLineArgument, cancellationToken);
        }
        public void Dispose() => Child?.Dispose();
    }
}
