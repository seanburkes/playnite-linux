namespace Playnite.Platform.DesktopIntegration;

/// <summary>
/// Owns one sleep-inhibition lease for the span of one or more game sessions. Acquisition completes
/// before <see cref="BeginSessionAsync"/> returns (before the game starts), and release completes
/// before <see cref="EndSessionAsync"/> returns (after the game stopped). Concurrent sessions share
/// the lease; the last session to end releases it.
/// </summary>
public sealed class SessionSleepInhibition : IAsyncDisposable
{
    private readonly ISleepInhibitionService service;
    private readonly SleepInhibitionRequest request;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> sessions = new(StringComparer.Ordinal);
    private volatile SleepInhibitionLease? lease;
    private volatile bool disposed;
    private int activeSessions;

    public SessionSleepInhibition(ISleepInhibitionService service, SleepInhibitionRequest? request = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.request = request ?? new SleepInhibitionRequest();
    }

    public DesktopCapability Capability { get; private set; } =
        DesktopCapability.Unavailable(DesktopFacility.SleepInhibition, "no session has started");

    public bool IsHeld => Volatile.Read(ref activeSessions) > 0 && lease is { IsHeld: true };

    public int ActiveSessionCount => Volatile.Read(ref activeSessions);

    public async Task<bool> BeginSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (!sessions.Add(sessionId))
            {
                return lease is { IsHeld: true };
            }

            Volatile.Write(ref activeSessions, sessions.Count);
            if (sessions.Count > 1)
            {
                return lease is { IsHeld: true };
            }

            SleepInhibitionLease acquired;
            try
            {
                acquired = await service.InhibitAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                sessions.Remove(sessionId);
                Volatile.Write(ref activeSessions, sessions.Count);
                throw;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await acquired.DisposeAsync().ConfigureAwait(false);
                sessions.Remove(sessionId);
                Volatile.Write(ref activeSessions, sessions.Count);
                cancellationToken.ThrowIfCancellationRequested();
            }

            lease = acquired;
            Capability = acquired.Capability;
            return acquired.IsHeld;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task EndSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (disposed)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!sessions.Remove(sessionId))
            {
                return;
            }

            Volatile.Write(ref activeSessions, sessions.Count);
            if (sessions.Count != 0 || lease is null)
            {
                return;
            }

            var released = lease;
            lease = null;
            await released.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            sessions.Clear();
            Volatile.Write(ref activeSessions, 0);
            var released = lease;
            lease = null;
            if (released is not null)
            {
                await released.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
