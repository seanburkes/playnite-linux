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

        Process.Running = false;
    }

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

    public int Calls => calls;

    public async Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref calls);
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
    public bool BlockUntilCancelled { get; set; }

    public TaskCompletionSource DelayEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        DelayEntered.TrySetResult();
        if (BlockUntilCancelled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}

internal static class ProcessSnapshots
{
    public static ProcessSnapshot Running(ProcessIdentity root, params ProcessEntry[] descendants) =>
        new([new ProcessEntry(root, 1, true), .. descendants], true);

    public static ProcessSnapshot Running(params ProcessEntry[] entries) => new(entries, true);

    public static ProcessSnapshot Stopped() => new([], true);

    public static ProcessSnapshot Incomplete(string reason = "unreadable /proc row") => new([], false, reason);
}
