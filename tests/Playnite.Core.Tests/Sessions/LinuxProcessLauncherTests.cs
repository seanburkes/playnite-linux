using Playnite.Platform.Linux.Sessions;
using Playnite.Platform.Sessions;
using Xunit;

namespace Playnite.Core.Tests.Sessions;

public sealed class LinuxProcessLauncherTests
{
    private static readonly string SleepPath = File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep";

    [Fact]
    public void Start_info_is_an_argv_vector_and_never_a_command_string()
    {
        var startInfo = new LinuxProcessStartInfoFactory().Create(
            new LaunchRequest("/usr/bin/printf", ["%s; touch /tmp/p5-004-nope", "a b"]));

        var setsid = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : "/bin/setsid";
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal([setsid], new[] { startInfo.FileName });
        Assert.Equal(["/usr/bin/printf", "%s; touch /tmp/p5-004-nope", "a b"], startInfo.ArgumentList);
    }

    [Fact]
    public void Shell_command_forms_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new LaunchRequest("/bin/sh", ["-c", "echo hi"]));
        Assert.Throws<ArgumentException>(() => new LaunchRequest("/bin/bash", ["-c", "echo hi"]));
        Assert.Throws<ArgumentException>(() => new LaunchRequest("sleep", ["1"]));
        Assert.Throws<ArgumentException>(() => new LaunchRequest("/usr/bin/sleep", ["1\0"]));
    }

    [Fact]
    public async Task Arguments_containing_shell_metacharacters_are_passed_literally()
    {
        Assert.True(File.Exists("/usr/bin/touch"), "The lifecycle tests require a real touch binary.");
        var suffix = Path.GetRandomFileName();
        var literal = Path.Combine(Path.GetTempPath(), $"p5-004-{suffix};touch p5-004-{suffix}");
        var injected = Path.Combine(Environment.CurrentDirectory, $"p5-004-{suffix}");
        await using var launcher = new LinuxProcessLauncher();
        try
        {
            try
            {
                var process = await launcher.StartAsync(new LaunchRequest("/usr/bin/touch", [literal]), CancellationToken.None);
                await launcher.StopAsync(process, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // touch may exit before /proc can supply its identity; the argument effect below
                // is still the evidence under test.
            }

            Assert.True(File.Exists(literal), "The argv element was split or expanded instead of passed literally.");
            Assert.False(File.Exists(injected), "The argv element was interpreted by a shell.");
        }
        finally
        {
            File.Delete(literal);
            File.Delete(injected);
        }
    }

    [Fact]
    public async Task Real_stop_leaves_no_child()
    {
        Assert.True(File.Exists(SleepPath), "The lifecycle tests require a real sleep binary.");
        await using var launcher = new LinuxProcessLauncher();
        var process = await launcher.StartAsync(new LaunchRequest(SleepPath, ["30"]), CancellationToken.None);
        Assert.True(await launcher.IsRunningAsync(process, CancellationToken.None));
        await using var tracker = new GameSessionTracker(launcher, new LinuxProcessObserver());
        using var recorder = new SessionEventRecorder(tracker);
        var tracking = tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(10) }, CancellationToken.None);
        await recorder.WaitForAsync(change => change.State == GameSessionState.Running);

        Assert.True(await tracker.StopAsync(CancellationToken.None));
        await tracking;

        Assert.Equal(GameSessionState.Stopped, tracker.State);
        Assert.False(await launcher.IsRunningAsync(process, CancellationToken.None));
        await AssertProcessGoneAsync(process.Identity);
    }

    [Fact]
    public async Task Real_stop_during_starting_leaves_no_child()
    {
        Assert.True(File.Exists(SleepPath), "The lifecycle tests require a real sleep binary.");
        await using var launcher = new LinuxProcessLauncher();
        var process = await launcher.StartAsync(new LaunchRequest(SleepPath, ["30"]), CancellationToken.None);
        await using var tracker = new GameSessionTracker(launcher, new LinuxProcessObserver());
        using var starting = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        tracker.StateChanged += (_, change) =>
        {
            if (change.State == GameSessionState.Starting)
            {
                starting.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };

        var tracking = Task.Run(() => tracker.TrackAsync(
            process,
            new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(10) },
            CancellationToken.None));
        Assert.True(starting.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.True(await tracker.StopAsync(CancellationToken.None));
        }
        finally
        {
            release.Set();
        }

        await tracking;
        Assert.Equal(GameSessionState.Stopped, tracker.State);
        await AssertProcessGoneAsync(process.Identity);
    }

    [Fact]
    public async Task Real_cancel_leaves_no_child()
    {
        Assert.True(File.Exists(SleepPath), "The lifecycle tests require a real sleep binary.");
        await using var launcher = new LinuxProcessLauncher();
        var process = await launcher.StartAsync(new LaunchRequest(SleepPath, ["30"]), CancellationToken.None);
        await using var tracker = new GameSessionTracker(launcher, new LinuxProcessObserver());
        using var recorder = new SessionEventRecorder(tracker);
        using var cancellation = new CancellationTokenSource();
        var tracking = tracker.TrackAsync(
            process,
            new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(10) },
            cancellation.Token);
        await recorder.WaitForAsync(change => change.State == GameSessionState.Running);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracking);

        Assert.Equal(GameSessionState.Cancelled, tracker.State);
        await AssertProcessGoneAsync(process.Identity);
    }

    [Fact]
    public async Task Real_tracker_disposal_leaves_no_child()
    {
        Assert.True(File.Exists(SleepPath), "The lifecycle tests require a real sleep binary.");
        await using var launcher = new LinuxProcessLauncher();
        var process = await launcher.StartAsync(new LaunchRequest(SleepPath, ["30"]), CancellationToken.None);
        var tracker = new GameSessionTracker(launcher, new LinuxProcessObserver());
        using var recorder = new SessionEventRecorder(tracker);
        var tracking = tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(10) }, CancellationToken.None);
        await recorder.WaitForAsync(change => change.State == GameSessionState.Running);

        await tracker.DisposeAsync();
        await tracking;

        Assert.Equal(GameSessionState.Cancelled, tracker.State);
        await AssertProcessGoneAsync(process.Identity);
    }

    [Fact]
    public async Task Real_launcher_shutdown_leaves_no_child()
    {
        Assert.True(File.Exists(SleepPath), "The lifecycle tests require a real sleep binary.");
        IProcessLauncher launcher = new LinuxProcessLauncher();
        var process = await launcher.StartAsync(new LaunchRequest(SleepPath, ["30"]), CancellationToken.None);

        await launcher.DisposeAsync();

        await AssertProcessGoneAsync(process.Identity);
    }

    [Fact]
    public async Task Start_cancelled_during_identity_read_kills_the_spawned_process()
    {
        Assert.True(File.Exists(SleepPath), "The lifecycle tests require a real sleep binary.");
        var processSystem = new RecordingProcessSystem();
        var reader = new GatedProcStatReader();
        reader.BlockNextRead();
        await using var launcher = new LinuxProcessLauncher(processSystem, reader);
        using var cancellation = new CancellationTokenSource();

        var start = launcher.StartAsync(new LaunchRequest(SleepPath, ["30"]), cancellation.Token);
        await reader.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var processId = processSystem.LastHandle!.ProcessId;
        var stat = (await new LinuxProcStatReader().ReadAsync(processId, CancellationToken.None))!.Value;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await AssertProcessGoneAsync(new ProcessIdentity(processId, stat.StartTimeTicks, stat.ProcessGroupId));
    }

    [Fact]
    public async Task IsRunning_cancelled_during_stat_read_propagates_without_killing()
    {
        Assert.True(File.Exists(SleepPath), "The lifecycle tests require a real sleep binary.");
        var reader = new GatedProcStatReader();
        await using var launcher = new LinuxProcessLauncher(new SystemProcessSystem(), reader);
        var process = await launcher.StartAsync(new LaunchRequest(SleepPath, ["30"]), CancellationToken.None);
        reader.BlockNextRead();
        using var cancellation = new CancellationTokenSource();

        var running = launcher.IsRunningAsync(process, cancellation.Token);
        await reader.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(await launcher.IsRunningAsync(process, CancellationToken.None));
        await launcher.StopAsync(process, CancellationToken.None);
        await AssertProcessGoneAsync(process.Identity);
    }

    [Fact]
    public async Task Stop_cancelled_during_exit_wait_still_kills_and_reaps_the_process()
    {
        Assert.True(File.Exists(SleepPath), "The lifecycle tests require a real sleep binary.");
        var processSystem = new RecordingProcessSystem { GateCancellableWait = true };
        await using var launcher = new LinuxProcessLauncher(processSystem, new LinuxProcStatReader());
        var process = await launcher.StartAsync(new LaunchRequest(SleepPath, ["30"]), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var stop = launcher.StopAsync(process, cancellation.Token);
        await processSystem.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        await AssertProcessGoneAsync(process.Identity);
    }

    [Fact]
    public async Task Observer_snapshot_cancelled_during_proc_read_propagates()
    {
        var reader = new GatedProcStatReader();
        reader.BlockNextRead();
        var observer = new LinuxProcessObserver(reader);
        using var cancellation = new CancellationTokenSource();

        var snapshot = observer.SnapshotAsync(cancellation.Token);
        await reader.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshot);
    }

    [Fact]
    public async Task Real_observer_contains_the_current_process_with_a_matching_identity()
    {
        var observer = new LinuxProcessObserver();
        var snapshot = await observer.SnapshotAsync(CancellationToken.None);
        Assert.True(snapshot.IsComplete, snapshot.IncompleteReason);
        var stat = await new LinuxProcStatReader().ReadAsync(Environment.ProcessId, CancellationToken.None);
        Assert.NotNull(stat);
        Assert.Contains(snapshot.Processes, entry =>
            entry.Identity == new ProcessIdentity(
                Environment.ProcessId, stat.Value.StartTimeTicks, stat.Value.ProcessGroupId));
    }

    [Fact]
    public async Task Failed_start_reports_the_orphan_when_kill_and_wait_fail()
    {
        Assert.True(File.Exists(SleepPath), "The lifecycle tests require a real sleep binary.");
        var processSystem = new RecordingProcessSystem { FailHandleOperations = true };
        await using var launcher = new LinuxProcessLauncher(processSystem, new NullProcStatReader());

        var exception = await Assert.ThrowsAsync<ProcessOrphanedException>(() =>
            launcher.StartAsync(new LaunchRequest(SleepPath, ["30"]), CancellationToken.None));

        Assert.True(exception.ProcessId > 0);
        Assert.IsType<AggregateException>(exception.InnerException);
        try
        {
            using var orphan = System.Diagnostics.Process.GetProcessById(exception.ProcessId);
            orphan.Kill(entireProcessTree: true);
            await orphan.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
            // The process exited on its own; the reporting assertion is the subject here.
        }
    }

    [Fact]
    public async Task Real_root_exit_keeps_a_tracked_child_and_stop_reaps_it()
    {
        const string python = "/usr/bin/python3";
        Assert.True(File.Exists(python), "The root-exit lifecycle test requires python3.");
        Assert.True(File.Exists(SleepPath), "The lifecycle tests require a real sleep binary.");
        var pidFile = Path.Combine(Path.GetTempPath(), $"p5-004-child-{Path.GetRandomFileName()}");
        const string script =
            "import os,sys,time\n" +
            "pid=os.fork()\n" +
            "if pid==0:\n" +
            "    time.sleep(30)\n" +
            "    os._exit(0)\n" +
            "open(sys.argv[1],'w').write(str(pid))\n" +
            "time.sleep(2)\n" +
            "os._exit(0)\n";
        ProcessIdentity childIdentity = default;
        await using var launcher = new LinuxProcessLauncher();
        try
        {
            var process = await launcher.StartAsync(
                new LaunchRequest(python, ["-c", script, pidFile]), CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!File.Exists(pidFile) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.True(File.Exists(pidFile), "The child PID file was not written.");
            var childPid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            var childStat = await new LinuxProcStatReader().ReadAsync(childPid, CancellationToken.None);
            Assert.NotNull(childStat);
            childIdentity = new ProcessIdentity(childPid, childStat.Value.StartTimeTicks, childStat.Value.ProcessGroupId);

            await using var tracker = new GameSessionTracker(launcher, new LinuxProcessObserver());
            using var recorder = new SessionEventRecorder(tracker);
            var tracking = tracker.TrackAsync(
                process,
                new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(10) },
                CancellationToken.None);
            await recorder.WaitForAsync(change => change.State == GameSessionState.Running);

            while (await launcher.IsRunningAsync(process, CancellationToken.None))
            {
                await Task.Delay(10);
            }

            await Task.Delay(100);
            Assert.Equal(GameSessionState.Running, tracker.State);
            Assert.True(await IdentityIsRunningAsync(childIdentity), "The reparented child did not survive the root exit.");
            Assert.True(await tracker.StopAsync(CancellationToken.None));
            await tracking;
            Assert.Equal(GameSessionState.Stopped, tracker.State);
            await AssertProcessGoneAsync(process.Identity);
            await AssertProcessGoneAsync(childIdentity);
        }
        finally
        {
            File.Delete(pidFile);
            if (childIdentity.ProcessId > 0 && await IdentityIsRunningAsync(childIdentity))
            {
                try
                {
                    using var child = System.Diagnostics.Process.GetProcessById(childIdentity.ProcessId);
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
            }
        }
    }

    [Fact]
    public async Task Real_escaped_child_is_reported_as_a_survivor_not_silently_ignored()
    {
        const string python = "/usr/bin/python3";
        Assert.True(File.Exists(python), "The process-group test requires python3.");
        var pidFile = Path.Combine(Path.GetTempPath(), $"p5-004-escape-{Path.GetRandomFileName()}");
        const string script =
            "import os,sys,time\n" +
            "pid=os.fork()\n" +
            "if pid==0:\n" +
            "    os.setsid()\n" +
            "    time.sleep(30)\n" +
            "    os._exit(0)\n" +
            "open(sys.argv[1],'w').write(str(pid))\n" +
            "time.sleep(2)\n" +
            "os._exit(0)\n";
        ProcessIdentity childIdentity = default;
        await using var launcher = new LinuxProcessLauncher();
        try
        {
            var process = await launcher.StartAsync(
                new LaunchRequest(python, ["-c", script, pidFile]), CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!File.Exists(pidFile) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.True(File.Exists(pidFile), "The escaping child PID file was not written.");
            var childPid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            while (DateTime.UtcNow < deadline)
            {
                var stat = await new LinuxProcStatReader().ReadAsync(childPid, CancellationToken.None);
                if (stat is { } row && row.ProcessGroupId == childPid)
                {
                    childIdentity = new ProcessIdentity(childPid, row.StartTimeTicks, row.ProcessGroupId);
                    break;
                }

                await Task.Delay(10);
            }

            Assert.True(childIdentity.ProcessId == childPid, "The child never entered its own session.");
            await using var tracker = new GameSessionTracker(launcher, new LinuxProcessObserver());
            using var recorder = new SessionEventRecorder(tracker);
            var tracking = tracker.TrackAsync(
                process,
                new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(10) },
                CancellationToken.None);
            await recorder.WaitForAsync(change => change.State == GameSessionState.Running);

            while (await launcher.IsRunningAsync(process, CancellationToken.None))
            {
                await Task.Delay(10);
            }

            await Task.Delay(100);
            var survivor = await Assert.ThrowsAsync<ProcessSurvivorException>(() => tracker.StopAsync(CancellationToken.None));
            Assert.Contains(survivor.Survivors, identity => identity.ProcessId == childPid);
            Assert.Equal(GameSessionState.Running, tracker.State);
            Assert.True(tracker.IsDegraded);
            Assert.True(await IdentityIsRunningAsync(childIdentity), "The escaped child must be reported, not killed silently.");

            await KillIdentityAsync(childIdentity);
            Assert.True(await tracker.StopAsync(CancellationToken.None));
            await tracking;
            Assert.Equal(GameSessionState.Stopped, tracker.State);
            await AssertProcessGoneAsync(process.Identity);
            await AssertProcessGoneAsync(childIdentity);
        }
        finally
        {
            File.Delete(pidFile);
            if (childIdentity.ProcessId > 0)
            {
                await KillIdentityAsync(childIdentity);
            }
        }
    }

    [Fact]
    public async Task Observer_marks_only_the_unreadable_row()
    {
        var root = Path.Combine(Path.GetTempPath(), $"p5-004-proc-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(Path.Combine(root, "100"));
        Directory.CreateDirectory(Path.Combine(root, "101"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "100", "stat"),
            "100 (game) S 1 100 100 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 5 0 0");
        await File.WriteAllTextAsync(Path.Combine(root, "101", "stat"), "not a stat row");
        try
        {
            var observer = new LinuxProcessObserver(new LinuxProcStatReader(root), root);
            var snapshot = await observer.SnapshotAsync(CancellationToken.None);

            Assert.True(snapshot.IsComplete);
            Assert.Contains(snapshot.Processes, entry => entry.Identity == new ProcessIdentity(100, 5, 100));
            Assert.Equal([101], snapshot.Unreadable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task KillIdentityAsync(ProcessIdentity identity)
    {
        if (!await IdentityIsRunningAsync(identity))
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private static async Task<bool> IdentityIsRunningAsync(ProcessIdentity identity)
    {
        var stat = await new LinuxProcStatReader().ReadAsync(identity.ProcessId, CancellationToken.None);
        return stat is { } row
            && row.ProcessId == identity.ProcessId
            && row.StartTimeTicks == identity.StartTimeTicks
            && row.IsRunning;
    }

    private static async Task AssertProcessGoneAsync(ProcessIdentity identity)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (!TryReadIdentity(identity.ProcessId, out var stat) || stat.StartTimeTicks != identity.StartTimeTicks)
            {
                return;
            }

            if (!stat.IsRunning)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Process {identity.ProcessId} with start ticks {identity.StartTimeTicks} survived.");
    }

    private static bool TryReadIdentity(int processId, out ProcStat stat)
    {
        try
        {
            stat = LinuxProcStat.Parse(File.ReadAllText(Path.Combine("/proc", processId.ToString(), "stat")));
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            stat = default;
            return false;
        }
    }
}
