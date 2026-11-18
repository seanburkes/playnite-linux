using Playnite.Core.Tests.TestDoubles;
using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Linux.DesktopIntegration;
using Xunit;

namespace Playnite.Core.Tests.DesktopIntegration;

public sealed class LinuxDesktopSessionProbeTests
{
    [Fact]
    public void ExplicitWaylandWinsEvenWhenXWaylandSetsDisplay()
    {
        var environment = new MemoryEnvironment();
        environment.Variables["XDG_SESSION_TYPE"] = "wayland";
        environment.Variables["WAYLAND_DISPLAY"] = "wayland-0";
        environment.Variables["DISPLAY"] = ":0";

        var session = new LinuxDesktopSessionProbe(environment).Probe();

        Assert.Equal(DesktopSessionKind.Wayland, session.Kind);
        Assert.Equal("XDG_SESSION_TYPE=wayland", session.Evidence);
        Assert.True(session.IsGraphical);
    }

    [Fact]
    public void ExplicitX11WinsOverAStrayWaylandDisplay()
    {
        var environment = new MemoryEnvironment();
        environment.Variables["XDG_SESSION_TYPE"] = "x11";
        environment.Variables["WAYLAND_DISPLAY"] = "wayland-0";
        environment.Variables["DISPLAY"] = ":0";

        var session = new LinuxDesktopSessionProbe(environment).Probe();

        Assert.Equal(DesktopSessionKind.X11, session.Kind);
        Assert.Equal("XDG_SESSION_TYPE=x11", session.Evidence);
    }

    [Theory]
    [InlineData("wayland-0", null, DesktopSessionKind.Wayland, "WAYLAND_DISPLAY is set")]
    [InlineData(null, ":1", DesktopSessionKind.X11, "DISPLAY is set")]
    public void FallsBackToDisplayVariablesWhenSessionTypeIsAbsent(
        string? waylandDisplay,
        string? display,
        DesktopSessionKind expected,
        string expectedEvidence)
    {
        var environment = new MemoryEnvironment();
        if (waylandDisplay is not null)
        {
            environment.Variables["WAYLAND_DISPLAY"] = waylandDisplay;
        }

        if (display is not null)
        {
            environment.Variables["DISPLAY"] = display;
        }

        var session = new LinuxDesktopSessionProbe(environment).Probe();

        Assert.Equal(expected, session.Kind);
        Assert.Equal(expectedEvidence, session.Evidence);
    }

    [Fact]
    public void HeadlessSessionIsNoneAndNotGraphical()
    {
        var environment = new MemoryEnvironment();
        environment.Variables["XDG_SESSION_TYPE"] = "tty";

        var session = new LinuxDesktopSessionProbe(environment).Probe();

        Assert.Equal(DesktopSessionKind.None, session.Kind);
        Assert.Equal("XDG_SESSION_TYPE=tty", session.Evidence);
        Assert.False(session.IsGraphical);
    }

    [Fact]
    public void UnsetSessionVariablesProduceHeadlessEvidence()
    {
        var session = new LinuxDesktopSessionProbe(new MemoryEnvironment()).Probe();

        Assert.Equal(DesktopSessionKind.None, session.Kind);
        Assert.Equal("WAYLAND_DISPLAY and DISPLAY are unset", session.Evidence);
    }
}
