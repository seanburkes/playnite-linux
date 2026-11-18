namespace Playnite.Platform.DesktopIntegration;

public enum DesktopSessionKind
{
    /// <summary>No graphical session was observed; desktop facilities degrade instead of failing.</summary>
    None,
    Wayland,
    X11
}

/// <summary>
/// Captured session evidence. <see cref="Evidence"/> names the variable that decided the kind, so a
/// Wayland session with an XWayland <c>DISPLAY</c> is never reported as X11.
/// </summary>
public sealed record DesktopSession(DesktopSessionKind Kind, string Evidence)
{
    public bool IsGraphical => Kind != DesktopSessionKind.None;
}

public interface IDesktopSessionProbe
{
    DesktopSession Probe();
}
