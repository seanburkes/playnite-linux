using Playnite.Platform.Sessions;

/// <summary>Fail-closed permission boundary, independent of the acceptance program's control flow.</summary>
public sealed class ConsentProcessHost(IExternalProcessHost inner, bool allowLaunch = false, bool allowStop = false) : IExternalProcessHost
{
    public Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken) => inner.SnapshotAsync(cancellationToken);

    public Task<int> RunAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        if (!allowLaunch)
        {
            throw new UnauthorizedAccessException("Read-only acceptance: launch consent was not given.");
        }

        return inner.RunAsync(request, cancellationToken);
    }

    public Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken)
    {
        if (!allowStop)
        {
            throw new UnauthorizedAccessException("Read-only acceptance: stop consent was not given.");
        }

        return inner.SignalAsync(identity, cancellationToken);
    }
}
