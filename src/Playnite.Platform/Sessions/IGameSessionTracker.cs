namespace Playnite.Platform.Sessions;

/// <summary>
/// Observes one launched game for start and stop. A tracker instance owns at most one active
/// session; a second <see cref="TrackAsync"/> before the terminal state is a programming error.
/// Cancellation and disposal are teardown: they terminate the owned process before the terminal
/// <see cref="GameSessionState.Cancelled"/> state is reported.
/// </summary>
public interface IGameSessionTracker : IAsyncDisposable
{
    event EventHandler<SessionStateChanged>? StateChanged;

    GameSessionState State { get; }

    bool IsDegraded { get; }

    TimeSpan Playtime { get; }

    Task TrackAsync(IGameProcess process, SessionOptions options, CancellationToken cancellationToken);

    /// <summary>Requests a manual stop. Returns false when no session is active.</summary>
    Task<bool> StopAsync(CancellationToken cancellationToken);
}
