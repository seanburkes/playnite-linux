using Playnite.Platform.Sessions;
using Xunit;

namespace Playnite.Core.Tests.Sessions;

public sealed class GameSessionTrackerTests
{
    private static readonly ProcessIdentity Root = new(100, 1000);

    [Fact]
    public void Terminal_states_are_exactly_stopped_cancelled_and_failed()
    {
        var terminal = Enum.GetValues<GameSessionState>().Where(state => state.IsTerminal()).ToArray();
        Assert.Equal([GameSessionState.Stopped, GameSessionState.Cancelled, GameSessionState.Failed], terminal);
        Assert.False(GameSessionState.Idle.IsTerminal());
        Assert.False(GameSessionState.Starting.IsTerminal());
        Assert.False(GameSessionState.Running.IsTerminal());
        Assert.False(GameSessionState.Stopping.IsTerminal());
    }

    [Fact]
    public async Task Tracker_reports_starting_running_then_natural_stop_with_non_decreasing_playtime()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(call => call == 1
            ? ProcessSnapshots.Running(Root)
            : ProcessSnapshots.Stopped());
        await using var tracker = new GameSessionTracker(launcher, observer, new GatedSessionDelay());
        using var recorder = new SessionEventRecorder(tracker);

        await tracker.TrackAsync(process, new SessionOptions(), CancellationToken.None);

        Assert.Equal(GameSessionState.Stopped, tracker.State);
        Assert.Equal([GameSessionState.Starting, GameSessionState.Running, GameSessionState.Stopping, GameSessionState.Stopped],
            recorder.Events.Select(change => change.State).ToArray());
        var playtimes = recorder.Events.Select(change => change.Playtime).ToArray();
        Assert.All(playtimes, playtime => Assert.True(playtime >= TimeSpan.Zero));
        Assert.Equal(playtimes.Order(), playtimes);
        Assert.False(process.Running);
        Assert.True(launcher.StopCalls >= 1);
    }

    [Fact]
    public async Task Recycled_pid_with_a_different_start_time_is_not_the_tracked_game()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var recycled = new ProcessIdentity(Root.ProcessId, Root.StartTimeTicks + 1);
        var observer = new ScriptedProcessObserver(_ => ProcessSnapshots.Running(recycled));
        await using var tracker = new GameSessionTracker(launcher, observer, new GatedSessionDelay());
        using var recorder = new SessionEventRecorder(tracker);

        await tracker.TrackAsync(process, new SessionOptions(), CancellationToken.None);

        Assert.Equal(GameSessionState.Stopped, tracker.State);
        Assert.Contains(recorder.Events, change => change.State == GameSessionState.Stopped);
        Assert.False(process.Running);
    }

    [Fact]
    public async Task Dead_or_zombie_root_is_not_running_and_ends_the_session()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(_ => new ProcessSnapshot([new ProcessEntry(Root, 1, false)], true));
        await using var tracker = new GameSessionTracker(launcher, observer, new GatedSessionDelay());

        await tracker.TrackAsync(process, new SessionOptions(), CancellationToken.None);

        Assert.Equal(GameSessionState.Stopped, tracker.State);
    }

    [Fact]
    public async Task Descendant_keeps_the_session_running_after_the_root_is_reparented()
    {
        var child = new ProcessEntry(new ProcessIdentity(200, 2000), Root.ProcessId, true);
        var reparented = new ProcessEntry(child.Identity, 1, true);
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(call => call switch
        {
            1 => ProcessSnapshots.Running(Root, child),
            2 => ProcessSnapshots.Running(reparented),
            _ => ProcessSnapshots.Stopped(),
        });
        await using var tracker = new GameSessionTracker(launcher, observer, new GatedSessionDelay());
        using var recorder = new SessionEventRecorder(tracker);

        await tracker.TrackAsync(process, new SessionOptions(), CancellationToken.None);

        Assert.Equal(3, observer.Calls);
        Assert.Equal(GameSessionState.Stopped, tracker.State);
        Assert.Contains(recorder.Events, change => change.State == GameSessionState.Running);
    }

    [Fact]
    public async Task Incomplete_snapshot_reports_degradation_and_never_claims_a_stop()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(_ => ProcessSnapshots.Incomplete());
        await using var tracker = new GameSessionTracker(launcher, observer, new SystemSessionDelay());
        using var recorder = new SessionEventRecorder(tracker);

        var tracking = tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(5) }, CancellationToken.None);
        var degraded = await recorder.WaitForAsync(change => change.IsDegraded);

        Assert.Equal(GameSessionState.Running, degraded.State);
        Assert.Equal(GameSessionState.Running, tracker.State);
        Assert.True(tracker.IsDegraded);
        Assert.DoesNotContain(recorder.Events, change => change.State is GameSessionState.Stopped or GameSessionState.Cancelled or GameSessionState.Failed);

        Assert.True(await tracker.StopAsync(CancellationToken.None));
        await tracking;
        Assert.Equal(GameSessionState.Stopped, tracker.State);
        Assert.False(process.Running);
    }

    [Fact]
    public async Task Complete_snapshot_after_degradation_clears_the_degradation_before_stopping()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(call => call switch
        {
            1 => ProcessSnapshots.Incomplete(),
            2 => ProcessSnapshots.Running(Root),
            _ => ProcessSnapshots.Stopped(),
        });
        await using var tracker = new GameSessionTracker(launcher, observer, new SystemSessionDelay());
        using var recorder = new SessionEventRecorder(tracker);

        await tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(1) }, CancellationToken.None);

        Assert.Contains(recorder.Events, change => change.IsDegraded);
        Assert.Contains(recorder.Events, change => !change.IsDegraded && change.State == GameSessionState.Running);
        Assert.Equal(GameSessionState.Stopped, tracker.State);
        Assert.False(tracker.IsDegraded);
    }

    [Fact]
    public async Task Manual_stop_is_required_and_reported_when_exit_observation_is_unavailable()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(_ => throw new InvalidOperationException("observation must not be attempted"));
        await using var tracker = new GameSessionTracker(launcher, observer, new GatedSessionDelay());
        using var recorder = new SessionEventRecorder(tracker);

        var tracking = tracker.TrackAsync(process, new SessionOptions { ExitObservationSupported = false }, CancellationToken.None);
        var degraded = await recorder.WaitForAsync(change => change.IsDegraded);

        Assert.Equal(GameSessionState.Running, degraded.State);
        Assert.Equal(0, observer.Calls);
        Assert.DoesNotContain(recorder.Events, change => change.State is GameSessionState.Stopped or GameSessionState.Cancelled or GameSessionState.Failed);

        Assert.True(await tracker.StopAsync(CancellationToken.None));
        await tracking;
        Assert.Equal(GameSessionState.Stopped, tracker.State);
        Assert.False(process.Running);
        Assert.Equal(0, observer.Calls);
        Assert.Contains(recorder.Events, change => change.State == GameSessionState.Stopped);
    }

    [Fact]
    public async Task Observer_failure_reports_failed_and_stops_the_process()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(_ => throw new IOException("proc table is unavailable"));
        await using var tracker = new GameSessionTracker(launcher, observer, new GatedSessionDelay());
        using var recorder = new SessionEventRecorder(tracker);

        await tracker.TrackAsync(process, new SessionOptions(), CancellationToken.None);

        Assert.Equal(GameSessionState.Failed, tracker.State);
        Assert.False(process.Running);
        Assert.Contains(recorder.Events, change => change.State == GameSessionState.Failed);
        Assert.True(launcher.StopCalls >= 1);
    }

    [Fact]
    public async Task Cancelling_the_tracker_during_the_poll_delay_reports_cancelled_and_stops_the_process()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(_ => ProcessSnapshots.Running(Root));
        var delay = new GatedSessionDelay { BlockUntilCancelled = true };
        await using var tracker = new GameSessionTracker(launcher, observer, delay);
        using var recorder = new SessionEventRecorder(tracker);
        using var cancellation = new CancellationTokenSource();

        var tracking = tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromHours(1) }, cancellation.Token);
        await delay.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracking);
        Assert.Equal(GameSessionState.Cancelled, tracker.State);
        Assert.False(process.Running);
        Assert.Contains(recorder.Events, change => change.State == GameSessionState.Cancelled);
        Assert.DoesNotContain(recorder.Events, change => change.State == GameSessionState.Stopped);
        Assert.True(launcher.StopCalls > 0);
    }

    [Fact]
    public async Task Cancelling_the_tracker_during_an_observer_snapshot_reports_cancelled_and_stops_the_process()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(_ => ProcessSnapshots.Running(Root))
        {
            BlockSnapshotUntilCancelled = true,
        };
        await using var tracker = new GameSessionTracker(launcher, observer, new GatedSessionDelay());
        using var recorder = new SessionEventRecorder(tracker);
        using var cancellation = new CancellationTokenSource();

        var tracking = tracker.TrackAsync(process, new SessionOptions(), cancellation.Token);
        await observer.SnapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracking);
        Assert.Equal(GameSessionState.Cancelled, tracker.State);
        Assert.False(process.Running);
        Assert.Contains(recorder.Events, change => change.State == GameSessionState.Cancelled);
    }

    [Fact]
    public async Task Cancelling_manual_stop_still_signals_and_reports_stopped_without_an_orphan()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process) { BlockFirstStopUntilCancelled = true };
        var observer = new ScriptedProcessObserver(_ => ProcessSnapshots.Running(Root));
        await using var tracker = new GameSessionTracker(launcher, observer, new SystemSessionDelay());
        using var recorder = new SessionEventRecorder(tracker);
        using var cancellation = new CancellationTokenSource();

        var tracking = tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(5) }, CancellationToken.None);
        await recorder.WaitForAsync(change => change.State == GameSessionState.Running);
        var stop = tracker.StopAsync(cancellation.Token);
        await launcher.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        await tracking;
        Assert.Equal(GameSessionState.Stopped, tracker.State);
        Assert.False(process.Running);
        Assert.True(launcher.StopCalls >= 2);
        Assert.DoesNotContain(recorder.Events, change => change.State == GameSessionState.Cancelled);
    }

    [Fact]
    public async Task Disposing_while_running_cancels_the_session_and_stops_the_process()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(_ => ProcessSnapshots.Running(Root));
        var tracker = new GameSessionTracker(launcher, observer, new SystemSessionDelay());
        using var recorder = new SessionEventRecorder(tracker);

        var tracking = tracker.TrackAsync(process, new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(5) }, CancellationToken.None);
        await recorder.WaitForAsync(change => change.State == GameSessionState.Running);
        await tracker.DisposeAsync();
        await tracking;

        Assert.Equal(GameSessionState.Cancelled, tracker.State);
        Assert.False(process.Running);
        Assert.Contains(recorder.Events, change => change.State == GameSessionState.Cancelled);
    }

    [Fact]
    public async Task Dispose_before_tracking_is_idempotent()
    {
        var tracker = new GameSessionTracker(new FakeProcessLauncher(), new ScriptedProcessObserver(_ => ProcessSnapshots.Stopped()));
        await tracker.DisposeAsync();
        await tracker.DisposeAsync();
        Assert.Equal(GameSessionState.Idle, tracker.State);
    }

    [Fact]
    public async Task A_second_track_call_after_the_terminal_state_is_rejected()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(_ => ProcessSnapshots.Stopped());
        await using var tracker = new GameSessionTracker(launcher, observer, new GatedSessionDelay());
        await tracker.TrackAsync(process, new SessionOptions(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tracker.TrackAsync(new FakeGameProcess(101, 1001), new SessionOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task Stop_without_an_active_session_returns_false()
    {
        await using var tracker = new GameSessionTracker(
            new FakeProcessLauncher(),
            new ScriptedProcessObserver(_ => ProcessSnapshots.Stopped()));
        Assert.False(await tracker.StopAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_before_the_first_observation_terminates_the_launched_process_as_cancelled()
    {
        var process = new FakeGameProcess(Root.ProcessId, Root.StartTimeTicks);
        var launcher = new FakeProcessLauncher(process);
        var observer = new ScriptedProcessObserver(_ => ProcessSnapshots.Running(Root));
        await using var tracker = new GameSessionTracker(launcher, observer, new GatedSessionDelay());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tracker.TrackAsync(process, new SessionOptions(), cancellation.Token));

        Assert.Equal(GameSessionState.Cancelled, tracker.State);
        Assert.False(process.Running);
        Assert.Equal(0, observer.Calls);
    }
}
