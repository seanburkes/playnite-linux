namespace Playnite.Platform.Sessions;

/// <summary>One process row from a <see cref="IProcessObserver"/> sample.</summary>
public sealed record ProcessEntry(ProcessIdentity Identity, int ParentProcessId, bool IsRunning);

/// <summary>
/// A process-table sample. <paramref name="IsComplete"/> is false when the table itself could
/// not be read. Individual unreadable rows are listed separately and degrade only the decision
/// that depends on that PID; an unrelated unreadable row cannot turn a good sample into unknown.
/// </summary>
public sealed record ProcessSnapshot(
    IReadOnlyList<ProcessEntry> Processes,
    bool IsComplete,
    string? IncompleteReason = null,
    IReadOnlyList<int>? UnreadableProcessIds = null)
{
    public IReadOnlyList<int> Unreadable => UnreadableProcessIds ?? [];
}

/// <summary>Reads an unattributed process-table snapshot without inspecting command lines or environments.</summary>
public interface IProcessObserver
{
    Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken);
}
