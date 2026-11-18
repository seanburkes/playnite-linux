using Playnite.Platform.Linux.Sessions;

namespace Playnite.Core.Tests.Sessions;

internal sealed class RecordingProcessSystem : IProcessSystem
{
    private readonly IProcessSystem inner = new SystemProcessSystem();

    public bool GateCancellableWait { get; set; }

    public IProcessHandle? LastHandle { get; private set; }

    public TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IProcessHandle Start(Playnite.Platform.Sessions.LaunchRequest request)
    {
        var handle = inner.Start(request);
        LastHandle = handle;
        return GateCancellableWait ? new GatedHandle(handle, WaitEntered) : handle;
    }

    private sealed class GatedHandle : IProcessHandle
    {
        private readonly IProcessHandle inner;
        private readonly TaskCompletionSource entered;

        public GatedHandle(IProcessHandle inner, TaskCompletionSource entered)
        {
            this.inner = inner;
            this.entered = entered;
        }

        public int ProcessId => inner.ProcessId;

        public bool HasExited => inner.HasExited;

        public void KillEntireTree() => inner.KillEntireTree();

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                entered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return inner.WaitForExitAsync(cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }
}

internal sealed class GatedProcStatReader : IProcStatReader
{
    private readonly IProcStatReader inner = new LinuxProcStatReader();
    private int calls;
    private int blockNext;

    public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void BlockNextRead() => Interlocked.Exchange(ref blockNext, 1);

    public async Task<ProcStat?> ReadAsync(int processId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        if (Interlocked.Exchange(ref blockNext, 0) == 1)
        {
            ReadEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return await inner.ReadAsync(processId, cancellationToken);
    }
}
