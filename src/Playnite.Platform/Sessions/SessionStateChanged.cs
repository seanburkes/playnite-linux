namespace Playnite.Platform.Sessions;

/// <summary>
/// A state transition or degradation notice. <see cref="PreviousState"/> equals
/// <see cref="State"/> for a degradation/recovery notice; such a notice never ends a session.
/// </summary>
public sealed class SessionStateChanged : EventArgs
{
    public SessionStateChanged(
        GameSessionState previousState,
        GameSessionState state,
        ProcessIdentity identity,
        TimeSpan playtime,
        bool isDegraded,
        string? reason)
    {
        PreviousState = previousState;
        State = state;
        Identity = identity;
        Playtime = playtime;
        IsDegraded = isDegraded;
        Reason = reason;
    }

    public GameSessionState PreviousState { get; }

    public GameSessionState State { get; }

    public ProcessIdentity Identity { get; }

    public TimeSpan Playtime { get; }

    public bool IsDegraded { get; }

    public string? Reason { get; }
}
