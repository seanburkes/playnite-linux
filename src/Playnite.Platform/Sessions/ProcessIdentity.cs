namespace Playnite.Platform.Sessions;

/// <summary>
/// A process identity valid inside one boot and PID namespace: the kernel PID plus the raw
/// <c>/proc/&lt;pid&gt;/stat</c> start-time field (clock ticks since boot, never wall-clock time).
/// A recycled PID has a different start time and is therefore a different identity.
/// </summary>
public readonly record struct ProcessIdentity
{
    public ProcessIdentity(int processId, long startTimeTicks, int processGroupId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegative(startTimeTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(processGroupId);
        ProcessId = processId;
        StartTimeTicks = startTimeTicks;
        ProcessGroupId = processGroupId;
    }

    public int ProcessId { get; }

    public long StartTimeTicks { get; }

    /// <summary>The PGID captured with the identity; zero when the caller has no group contract.</summary>
    public int ProcessGroupId { get; }
}
