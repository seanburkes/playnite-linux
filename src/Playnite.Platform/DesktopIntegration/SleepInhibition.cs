namespace Playnite.Platform.DesktopIntegration;

public sealed record SleepInhibitionRequest(string Who = "Playnite", string Why = "Playing a game");

public interface ISleepInhibitorHandle : IAsyncDisposable
{
    string Mechanism { get; }

    bool IsHeld { get; }
}

public sealed record SleepInhibitorAcquireResult(ISleepInhibitorHandle? Handle, string Detail);

public interface ISleepInhibitorBackend
{
    string Mechanism { get; }

    bool IsAvailable { get; }

    Task<SleepInhibitorAcquireResult> TryAcquireAsync(
        SleepInhibitionRequest request,
        CancellationToken cancellationToken = default);
}

public interface ISleepInhibitionService
{
    /// <summary>
    /// Returns a lease even when the facility is unavailable. The caller keeps starting the game;
    /// the lease reports <c>IsHeld == false</c> and the capability explains the fallback.
    /// </summary>
    Task<SleepInhibitionLease> InhibitAsync(
        SleepInhibitionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class SleepInhibitionLease : IAsyncDisposable
{
    private ISleepInhibitorHandle? handle;

    private SleepInhibitionLease(DesktopCapability capability, ISleepInhibitorHandle? handle)
    {
        Capability = capability;
        this.handle = handle;
    }

    public DesktopCapability Capability { get; }

    public bool IsHeld => handle is { IsHeld: true };

    public static SleepInhibitionLease Held(DesktopCapability capability, ISleepInhibitorHandle handle) =>
        new(capability, handle ?? throw new ArgumentNullException(nameof(handle)));

    public static SleepInhibitionLease NotHeld(DesktopCapability capability) => new(capability, null);

    public async ValueTask DisposeAsync()
    {
        var current = Interlocked.Exchange(ref handle, null);
        if (current is not null)
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }
    }
}
