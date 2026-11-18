namespace Playnite.Platform.Sessions;

/// <summary>Canonical executable evidence; null means unreadable, never a guessed path.</summary>
public sealed record ExternalProcessEntry(ProcessEntry Process, string? Executable);

/// <summary>
/// One process-table sample. <paramref name="MovedIdentities"/> lists rows whose process group
/// changed between the observer read and the identity re-read. Such a row is retained with its
/// re-read group so an attributed identity can be recognized as an escape; the sample stays
/// complete because a move by an unrelated process does not degrade the rows this session owns.
/// </summary>
public sealed record ExternalProcessSnapshot(
    IReadOnlyList<ExternalProcessEntry> Processes,
    bool IsComplete,
    IReadOnlyList<ProcessIdentity>? MovedIdentities = null)
{
    public IReadOnlyList<ProcessIdentity> Moved => MovedIdentities ?? [];
}

/// <summary>Host-namespace observation and identity-checked signalling, without shell commands.</summary>
public interface IExternalProcessHost
{
    Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken);
    Task<int> RunAsync(LaunchRequest request, CancellationToken cancellationToken);
    Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken);
    /// <summary>Pin the identity and reverify both executable and stat before SIGTERM. No PID fallback.</summary>
    Task SignalAsync(ProcessIdentity identity, string expectedExecutable, CancellationToken cancellationToken);
}
