namespace Playnite.Platform.Sessions;

/// <summary>
/// The session states of <c>docs/formal/GameSession.tla</c>. <see cref="Idle"/> is the
/// initial state; <see cref="Stopped"/>, <see cref="Cancelled"/> and <see cref="Failed"/> are
/// the only terminal states.
/// </summary>
public enum GameSessionState
{
    Idle,
    Starting,
    Running,
    Stopping,
    Stopped,
    Cancelled,
    Failed,
}

public static class GameSessionStates
{
    public static bool IsTerminal(this GameSessionState state) =>
        state is GameSessionState.Stopped or GameSessionState.Cancelled or GameSessionState.Failed;
}
