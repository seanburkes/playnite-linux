namespace Playnite.Platform.Sessions;

/// <summary>A failed start whose spawned process could not be signalled and reaped.</summary>
public sealed class ProcessOrphanedException : Exception
{
    public ProcessOrphanedException(int processId, Exception startFailure, Exception reapFailure)
        : base($"Process {processId} could not be reaped after a failed start; it may still be running.",
            new AggregateException(startFailure, reapFailure))
    {
        ProcessId = processId;
    }

    public int ProcessId { get; }
}
