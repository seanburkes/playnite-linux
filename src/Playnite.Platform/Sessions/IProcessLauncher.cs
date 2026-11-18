namespace Playnite.Platform.Sessions;

/// <summary>A launched process handle with its immutable process identity.</summary>
public interface IGameProcess
{
    ProcessIdentity Identity { get; }

    DateTimeOffset StartedAt { get; }
}

/// <summary>
/// Starts and controls a game process. Every call takes cancellation at each await; starting
/// and stopping never leave a launched child behind when cancellation interrupts them.
/// </summary>
public interface IProcessLauncher : IAsyncDisposable
{
    Task<IGameProcess> StartAsync(LaunchRequest request, CancellationToken cancellationToken);

    Task<bool> IsRunningAsync(IGameProcess process, CancellationToken cancellationToken);

    /// <summary>
    /// Signals and reaps the process. A successful return proves the process is no longer
    /// running; an exception means that proof was not obtained.
    /// </summary>
    Task StopAsync(IGameProcess process, CancellationToken cancellationToken);

    /// <summary>
    /// Signals and reaps the root plus every tracked descendant identity. A successful return
    /// proves all of them are no longer running, including a descendant that outlived a root
    /// exit and was reparented.
    /// </summary>
    Task StopAsync(
        IGameProcess process,
        IReadOnlyCollection<ProcessIdentity> trackedProcesses,
        CancellationToken cancellationToken);
}
