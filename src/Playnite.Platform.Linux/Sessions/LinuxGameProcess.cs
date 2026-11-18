using Playnite.Platform.Sessions;

namespace Playnite.Platform.Linux.Sessions;

/// <summary>
/// A launched Linux process. Every liveness decision compares the PID's current raw start ticks
/// with the ticks captured at launch, so a recycled PID is never mistaken for the game.
/// </summary>
internal sealed class LinuxGameProcess : IGameProcess
{
    private readonly IProcessHandle handle;
    private readonly IProcStatReader statReader;

    public LinuxGameProcess(IProcessHandle handle, ProcessIdentity identity, DateTimeOffset startedAt, IProcStatReader statReader)
    {
        this.handle = handle;
        this.statReader = statReader;
        Identity = identity;
        StartedAt = startedAt;
    }

    public ProcessIdentity Identity { get; }

    public DateTimeOffset StartedAt { get; }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stat = await statReader.ReadAsync(Identity.ProcessId, cancellationToken).ConfigureAwait(false);
        return stat is { } row
            && row.ProcessId == Identity.ProcessId
            && row.StartTimeTicks == Identity.StartTimeTicks
            && row.IsRunning;
    }

    /// <summary>
    /// Signals the whole tree, then waits. The identity check and the signal do not take the
    /// caller's token: once a stop is requested, cancellation only bounds the wait and can never
    /// skip the kill.
    /// </summary>
    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        ProcStat? stat;
        try
        {
            stat = await statReader.ReadAsync(Identity.ProcessId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            // Without identity coverage, signal the owned handle rather than leave a survivor.
            handle.KillEntireTree();
            throw;
        }

        if (stat is { } row
            && row.ProcessId == Identity.ProcessId
            && row.StartTimeTicks == Identity.StartTimeTicks
            && row.IsRunning)
        {
            handle.KillEntireTree();
        }

        try
        {
            await handle.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The process was already signalled; reap it without the cancelled token so the
            // caller observes a real termination before the cancellation propagates.
            await handle.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            throw;
        }
    }

    public void DisposeHandle() => handle.Dispose();
}
