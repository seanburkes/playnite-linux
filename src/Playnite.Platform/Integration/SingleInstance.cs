namespace Playnite.Platform.Integration;

/// <summary>Outcome of asking for ownership of the per-user single-instance lock.</summary>
public enum SingleInstanceStatus
{
    /// <summary>This process owns the lock and should host the activation listener.</summary>
    Owner,

    /// <summary>Another live process owns the lock; this process must not start a second app.</summary>
    AlreadyRunning,

    /// <summary>This process handed an activation URI to the live owner and should exit.</summary>
    Forwarded,

    /// <summary>The runtime directory is unusable; the application may continue without guarding.</summary>
    Unavailable,
}

/// <summary>The ownership decision plus a user-facing diagnostic when the facility degraded.</summary>
public sealed record SingleInstanceResult(SingleInstanceStatus Status, string? Diagnostic)
{
    public bool IsOwner => Status == SingleInstanceStatus.Owner;
}

/// <summary>An activation URI forwarded by a later instance to the owner.</summary>
public sealed record ActivationRequest(string Uri);

/// <summary>
/// Linux implementation: an advisory file lock that the kernel releases when the owning
/// process dies, plus a Unix domain socket for forwarding activation to that owner.
/// </summary>
public interface ISingleInstance : IAsyncDisposable
{
    SingleInstanceResult Result { get; }

    /// <summary>Raised on the owner when another instance forwards an activation URI.</summary>
    event EventHandler<ActivationRequest>? Activated;

    /// <summary>
    /// Sends an activation URI to the owning instance. A forwarded result means the owner
    /// queued or dispatched it; malformed payloads, local ownership, cancellation and an
    /// unavailable owner are reported by the returned result (host cancellation still throws).
    /// </summary>
    Task<SingleInstanceResult> ActivateAsync(string uri, CancellationToken cancellationToken = default);
}
