using Playnite.Core;

using Playnite.Platform.DesktopIntegration;

namespace Playnite.Platform.Linux.DesktopIntegration;

/// <summary>
/// Explicit session precedence: an explicit XDG_SESSION_TYPE wins, then WAYLAND_DISPLAY, then
/// DISPLAY. A Wayland session normally also exports DISPLAY through XWayland, so DISPLAY alone is
/// never used to claim X11 when Wayland evidence exists.
/// </summary>
public sealed class LinuxDesktopSessionProbe : IDesktopSessionProbe
{
    private readonly IEnvironment environment;

    public LinuxDesktopSessionProbe(IEnvironment environment) =>
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public DesktopSession Probe()
    {
        var sessionType = environment.GetVariable("XDG_SESSION_TYPE")?.Trim();
        var waylandDisplay = environment.GetVariable("WAYLAND_DISPLAY")?.Trim();
        var display = environment.GetVariable("DISPLAY")?.Trim();

        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
        {
            return new DesktopSession(DesktopSessionKind.Wayland, "XDG_SESSION_TYPE=wayland");
        }

        if (string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase))
        {
            return new DesktopSession(DesktopSessionKind.X11, "XDG_SESSION_TYPE=x11");
        }

        if (!string.IsNullOrEmpty(waylandDisplay))
        {
            return new DesktopSession(DesktopSessionKind.Wayland, "WAYLAND_DISPLAY is set");
        }

        if (!string.IsNullOrEmpty(display))
        {
            return new DesktopSession(DesktopSessionKind.X11, "DISPLAY is set");
        }

        return new DesktopSession(
            DesktopSessionKind.None,
            string.IsNullOrEmpty(sessionType)
                ? "WAYLAND_DISPLAY and DISPLAY are unset"
                : $"XDG_SESSION_TYPE={sessionType}");
    }
}
