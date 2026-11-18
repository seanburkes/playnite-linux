using Playnite.Platform.DesktopIntegration;

namespace Playnite.Platform.Linux.DesktopIntegration;

/// <summary>
/// Startup capability report. Detection is read-only: no notification is sent, no inhibitor is
/// acquired, and an absent tool becomes an unavailable row rather than an exception.
/// </summary>
public sealed class LinuxDesktopIntegrationDiagnostics : IDesktopIntegrationDiagnostics
{
    private readonly IDesktopSessionProbe sessionProbe;
    private readonly ITrayHostProbe trayHostProbe;
    private readonly IExecutableLocator locator;

    public LinuxDesktopIntegrationDiagnostics(
        IDesktopSessionProbe sessionProbe,
        ITrayHostProbe trayHostProbe,
        IExecutableLocator locator)
    {
        this.sessionProbe = sessionProbe ?? throw new ArgumentNullException(nameof(sessionProbe));
        this.trayHostProbe = trayHostProbe ?? throw new ArgumentNullException(nameof(trayHostProbe));
        this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    public async Task<DesktopIntegrationReport> DiagnoseAsync(
        CancellationToken cancellationToken = default)
    {
        var session = sessionProbe.Probe();
        var tray = await trayHostProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        return new DesktopIntegrationReport(
            session,
            [
                tray.HasStatusNotifierWatcher
                    ? DesktopCapability.Available(
                        DesktopFacility.Tray,
                        "StatusNotifierItem",
                        tray.Detail + "; an icon presenter must still be registered by the UI")
                    : DesktopCapability.Unavailable(DesktopFacility.Tray, tray.Detail),
                NotificationsCapability(),
                SleepInhibitionCapability()
            ]);
    }

    private DesktopCapability NotificationsCapability()
    {
        if (locator.Exists(NotifySendBackend.ToolName))
        {
            return DesktopCapability.Available(
                DesktopFacility.Notifications,
                NotifySendBackend.ToolName,
                "notify-send is installed; gdbus is the fallback");
        }

        if (locator.Exists(GdbusNotificationBackend.ToolName))
        {
            return DesktopCapability.Available(
                DesktopFacility.Notifications,
                GdbusNotificationBackend.ToolName,
                "notify-send is absent; gdbus is the direct org.freedesktop.Notifications fallback");
        }

        return DesktopCapability.Unavailable(
            DesktopFacility.Notifications,
            "neither notify-send nor gdbus is installed");
    }

    private DesktopCapability SleepInhibitionCapability()
    {
        if (!locator.Exists(SystemdInhibitBackend.ExecutableName))
        {
            return DesktopCapability.Unavailable(
                DesktopFacility.SleepInhibition,
                "systemd-inhibit is not installed");
        }

        if (!locator.Exists(SystemdInhibitBackend.HoldCommandName))
        {
            return DesktopCapability.Unavailable(
                DesktopFacility.SleepInhibition,
                "the systemd-inhibit hold command (cat) is not installed");
        }

        return DesktopCapability.Available(
            DesktopFacility.SleepInhibition,
            SystemdInhibitBackend.ExecutableName,
            "systemd-inhibit holds sleep while the stdin lifeline stays open");
    }
}
