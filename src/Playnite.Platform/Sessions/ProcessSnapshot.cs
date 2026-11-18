namespace Playnite.Platform.Sessions;

/// <summary>One process row from a <see cref="IProcessObserver"/> sample.</summary>
public sealed record ProcessEntry(ProcessIdentity Identity, int ParentProcessId, bool IsRunning);

/// <summary>
/// A process-table sample. <paramref name="IsComplete"/> is false when any row could not be
/// read; an incomplete sample is <em>unknown</em> coverage, never evidence that a process ended.
/// </summary>
public sealed record ProcessSnapshot(
    IReadOnlyList<ProcessEntry> Processes,
    bool IsComplete,
    string? IncompleteReason = null);

/// <summary>Reads an unattributed process-table snapshot without inspecting command lines or environments.</summary>
public interface IProcessObserver
{
    Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken);
}
