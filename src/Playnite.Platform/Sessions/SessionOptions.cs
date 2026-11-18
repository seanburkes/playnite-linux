namespace Playnite.Platform.Sessions;

/// <summary>
/// Controls how a <see cref="IGameSessionTracker"/> observes a launched process.
/// <paramref name="ExitObservationSupported"/> is false when the caller already knows the
/// platform cannot observe exit; the tracker then reports the degradation and waits for a
/// manual <see cref="IGameSessionTracker.StopAsync"/> instead of inventing a stop.
/// </summary>
public sealed record SessionOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool ExitObservationSupported { get; init; } = true;

    internal void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "The poll interval must be positive.");
        }

        if (StopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StopTimeout), "The stop timeout must be positive.");
        }
    }
}
