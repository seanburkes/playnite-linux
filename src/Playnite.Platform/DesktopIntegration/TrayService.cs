namespace Playnite.Platform.DesktopIntegration;

public sealed record TrayMenuItem(string Id, string Label, bool Enabled = true, bool IsDefault = false);

public sealed record TrayModel(
    string Id,
    string Title,
    IReadOnlyList<TrayMenuItem> Menu,
    string? IconName = null);

/// <summary>
/// Result of looking for a tray host. <c>HasStatusNotifierWatcher</c> is the only supported host
/// shape on Linux in v1; XEmbed-only sessions degrade to no tray.
/// </summary>
public sealed record TrayHostStatus(bool HasStatusNotifierWatcher, string Detail);

public interface ITrayHostProbe
{
    Task<TrayHostStatus> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The UI edge owns icon rendering. The platform service decides whether a host exists and keeps
/// the lifecycle; a missing presenter degrades instead of pretending the icon is visible.
/// </summary>
public interface ITrayIconPresenter
{
    ValueTask<IAsyncDisposable> PresentAsync(TrayModel model, CancellationToken cancellationToken = default);
}

public interface ITrayService
{
    Task<TrayInstallation> InstallAsync(TrayModel model, CancellationToken cancellationToken = default);
}

public sealed class TrayInstallation : IAsyncDisposable
{
    private IAsyncDisposable? presenterHandle;

    public TrayInstallation(TrayModel model, DesktopCapability capability, IAsyncDisposable? presenterHandle = null)
    {
        if (capability.IsAvailable && presenterHandle is null)
        {
            throw new ArgumentException("A hosted tray installation needs the presenter handle.", nameof(presenterHandle));
        }

        Model = model;
        Capability = capability;
        this.presenterHandle = presenterHandle;
    }

    public TrayModel Model { get; }

    public DesktopCapability Capability { get; }

    public bool IsHosted => Capability.IsAvailable;

    public async ValueTask DisposeAsync()
    {
        var handle = Interlocked.Exchange(ref presenterHandle, null);
        if (handle is not null)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
    }
}
