namespace Playnite.Platform.Integration;

/// <summary>Outcome of registering the application URI scheme with the desktop.</summary>
public enum ProtocolRegistrationStatus
{
    /// <summary>The desktop entry and default handler are in place.</summary>
    Registered,

    /// <summary>Registration is not permitted here (for example a sandbox); nothing was written.</summary>
    Skipped,

    /// <summary>Registration was attempted and failed; the application still starts.</summary>
    Failed,
}

/// <summary>Registration decision plus a diagnostic suitable for logs and support bundles.</summary>
public sealed record ProtocolRegistrationResult(ProtocolRegistrationStatus Status, string? Diagnostic)
{
    public bool IsRegistered => Status == ProtocolRegistrationStatus.Registered;
}

/// <summary>Registers the application's <c>playnite://</c> scheme as a desktop URL handler.</summary>
public interface IProtocolHandler
{
    string Scheme { get; }

    Task<ProtocolRegistrationResult> RegisterAsync(CancellationToken cancellationToken = default);
}
