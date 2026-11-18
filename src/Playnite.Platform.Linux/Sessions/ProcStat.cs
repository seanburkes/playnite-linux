namespace Playnite.Platform.Linux.Sessions;

/// <summary>Fields of <c>/proc/&lt;pid&gt;/stat</c> that establish process identity.</summary>
public readonly record struct ProcStat(int ProcessId, int ParentProcessId, char State, long StartTimeTicks)
{
    /// <summary>A zombie or dead process is not running.</summary>
    public bool IsRunning => State is not ('Z' or 'X' or 'x');
}

/// <summary>Reads one process's stat row. Null means the process vanished; errors mean unknown coverage.</summary>
public interface IProcStatReader
{
    Task<ProcStat?> ReadAsync(int processId, CancellationToken cancellationToken);
}
