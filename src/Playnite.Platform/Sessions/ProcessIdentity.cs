namespace Playnite.Platform.Sessions;

/// <summary>
/// A process identity valid inside one boot and PID namespace: the kernel PID plus the raw
/// <c>/proc/&lt;pid&gt;/stat</c> start-time field (clock ticks since boot, never wall-clock time).
/// A recycled PID has a different start time and is therefore a different identity.
/// </summary>
public readonly record struct ProcessIdentity
{
    public ProcessIdentity(int processId, long startTimeTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegative(startTimeTicks);
        ProcessId = processId;
        StartTimeTicks = startTimeTicks;
    }

    public int ProcessId { get; }

    public long StartTimeTicks { get; }
}
