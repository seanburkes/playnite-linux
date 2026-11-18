using Playnite.Platform.DesktopIntegration;

namespace Playnite.Platform.Linux.DesktopIntegration;

/// <summary>
/// Linux tray lifecycle. The host must be a StatusNotifierItem watcher; when none exists (or the UI
/// has not supplied a presenter) installation reports an unavailable capability instead of throwing.
/// </summary>
public sealed class LinuxTrayService : ITrayService
{
    private readonly ITrayHostProbe hostProbe;
    private readonly ITrayIconPresenter? presenter;

    public LinuxTrayService(ITrayHostProbe hostProbe, ITrayIconPresenter? presenter = null)
    {
        this.hostProbe = hostProbe ?? throw new ArgumentNullException(nameof(hostProbe));
        this.presenter = presenter;
    }

    public async Task<TrayInstallation> InstallAsync(
        TrayModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var host = await hostProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!host.HasStatusNotifierWatcher)
        {
            return new TrayInstallation(
                model,
                DesktopCapability.Unavailable(DesktopFacility.Tray, host.Detail));
        }

        if (presenter is null)
        {
            return new TrayInstallation(
                model,
                DesktopCapability.Unavailable(
                    DesktopFacility.Tray,
                    "StatusNotifierItem host is present but no tray presenter is registered"));
        }

        try
        {
            var handle = await presenter.PresentAsync(model, cancellationToken).ConfigureAwait(false);
            return new TrayInstallation(
                model,
                DesktopCapability.Available(DesktopFacility.Tray, "StatusNotifierItem", host.Detail),
                handle);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new TrayInstallation(
                model,
                DesktopCapability.Unavailable(
                    DesktopFacility.Tray,
                    "tray presenter failed: " + exception.Message));
        }
    }
}
