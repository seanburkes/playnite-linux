namespace Playnite.SDK;

/// <summary>
/// Outcome of a host-backed operation. Unavailable and Failed are explicit results, not
/// exceptions: a missing UI host must not stop startup or crash the CLI.
/// </summary>
public enum HostOperationStatus
{
    /// <summary>The host answered the request.</summary>
    Completed,

    /// <summary>No host was registered, or the registered host cannot provide the facility.</summary>
    Unavailable,

    /// <summary>The host was registered but the request failed.</summary>
    Failed,
}
