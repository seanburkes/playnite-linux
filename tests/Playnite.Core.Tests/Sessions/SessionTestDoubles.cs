using Playnite.Platform.Sessions;

namespace Playnite.Core.Tests.Sessions;

internal sealed class FakeGameProcess : IGameProcess
{
    public FakeGameProcess(int processId = 4242, long startTimeTicks = 9001)
    {
        Identity = new ProcessIdentity(processId, startTimeTicks);
    }

    public ProcessIdentity Identity { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UnixEpoch;

    public bool Running { get; set; } = true;
}

internal sealed class FakeProcessLauncher : IProcessLauncher, IAsyncDisposable
{
    private int stopCalls;

    public FakeProcessLauncher(FakeGameProcess? process = null) => Process = process ?? new FakeGameProcess();

    public FakeGameProcess Process { get; }

    public bool BlockFirstStopUntilCancelled { get; set; }

    public bool FailSecondStop { get; set; }

    public bool FailAllStopsAfterFirst { get; set; }

    public bool FailAllStops { get; set; }

    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int StopCalls => stopCalls;

    public bool Disposed { get; private set; }

    public Task<IGameProcess> StartAsync(LaunchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IGameProcess>(Process);

    public Task<bool> IsRunningAsync(IGameProcess process, CancellationToken cancellationToken) =>
        Task.FromResult(Process.Running);

    public async Task StopAsync(IGameProcess process, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref stopCalls);
        StopEntered.TrySetResult();
        if (call == 1 && BlockFirstStopUntilCancelled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (call == 2 && FailSecondStop)
        {
            throw new InvalidOperationException("the cancelled-stop retry failed");
        }

        if (call >= 2 && FailAllStopsAfterFirst)
        {
            throw new InvalidOperationException("the stop retry failed");
        }

        if (FailAllStops)
        {
            throw new InvalidOperationException("the stop failed");
        }

        Process.Running = false;
    }

    public Task StopAsync(
        IGameProcess process,
        IReadOnlyCollection<Playnite.Platform.Sessions.ProcessIdentity> trackedProcesses,
        CancellationToken cancellationToken) =>
        StopAsync(process, cancellationToken);

    public ValueTask DisposeAsync()
    {
        Process.Running = false;
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ScriptedProcessObserver : IProcessObserver
{
    private readonly Func<int, ProcessSnapshot> next;
    private int calls;

    public ScriptedProcessObserver(Func<int, ProcessSnapshot> next) => this.next = next;

    public bool BlockSnapshotUntilCancelled { get; set; }

    public TaskCompletionSource SnapshotEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action<int>? OnCall { get; set; }

    public int Calls => calls;

    public async Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref calls);
        OnCall?.Invoke(call);
        SnapshotEntered.TrySetResult();
        if (BlockSnapshotUntilCancelled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return next(call);
    }
}

internal sealed class GatedSessionDelay : ISessionDelay
{
    private int calls;

    public bool BlockUntilCancelled { get; set; }

    public TaskCompletionSource DelayEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Invoked after the tracker has applied a snapshot and entered the delay that follows it.
    /// A test can therefore observe "this observation was applied" without polling a clock.
    /// </summary>
    public Action<int>? OnDelay { get; set; }

    public async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        // Yield instead of consuming wall-clock time: a tracker whose snapshots also complete
        // synchronously must still hand the test thread a chance to run.
        await Task.Yield();
        var call = Interlocked.Increment(ref calls);
        OnDelay?.Invoke(call);
        DelayEntered.TrySetResult();
        if (BlockUntilCancelled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}

internal sealed class SignallingProcessObserver : IProcessObserver
{
    private readonly IProcessObserver inner;
    private readonly Func<ProcessSnapshot, bool> predicate;

    public SignallingProcessObserver(IProcessObserver inner, Func<ProcessSnapshot, bool> predicate)
    {
        this.inner = inner;
        this.predicate = predicate;
    }

    public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await inner.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (predicate(snapshot))
        {
            Signal.TrySetResult();
        }

        return snapshot;
    }
}

internal static class ProcessSnapshots
{
    public static ProcessSnapshot Running(ProcessIdentity root, params ProcessEntry[] descendants) =>
        new([new ProcessEntry(root, 1, true), .. descendants], true);

    public static ProcessSnapshot Running(params ProcessEntry[] entries) => new(entries, true);

    public static ProcessSnapshot Stopped() => new([], true);

    public static ProcessSnapshot Incomplete(string reason = "unreadable /proc row") => new([], false, reason);

    public static ProcessSnapshot WithUnreadable(int[] unreadable, params ProcessEntry[] entries) =>
        new(entries, true, null, unreadable);
}
