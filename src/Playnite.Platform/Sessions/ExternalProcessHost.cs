namespace Playnite.Platform.Sessions;

/// <summary>Canonical executable evidence; null means unreadable, never a guessed path.</summary>
public sealed record ExternalProcessEntry(ProcessEntry Process, string? Executable);

public sealed record ExternalProcessSnapshot(IReadOnlyList<ExternalProcessEntry> Processes, bool IsComplete);

/// <summary>Host-namespace observation and identity-checked signalling, without shell commands.</summary>
public interface IExternalProcessHost
{
    Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken);
    Task<int> RunAsync(LaunchRequest request, CancellationToken cancellationToken);
    Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken);
    /// <summary>Pin the identity and reverify both executable and stat before SIGTERM. No PID fallback.</summary>
    Task SignalAsync(ProcessIdentity identity, string expectedExecutable, CancellationToken cancellationToken);
}
