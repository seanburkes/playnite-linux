namespace Playnite.Platform.Sessions;

/// <summary>The poll wait seam, so lifecycle tests can cancel at the tracker's delay await.</summary>
public interface ISessionDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

public sealed class SystemSessionDelay : ISessionDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);
}
