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
public interface IProcessLauncher
{
    Task<IGameProcess> StartAsync(LaunchRequest request, CancellationToken cancellationToken);

    Task<bool> IsRunningAsync(IGameProcess process, CancellationToken cancellationToken);

    Task StopAsync(IGameProcess process, CancellationToken cancellationToken);
}
