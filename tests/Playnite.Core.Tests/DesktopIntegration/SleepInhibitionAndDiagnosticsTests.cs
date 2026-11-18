using System.Diagnostics;
using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Linux.DesktopIntegration;
using Xunit;

namespace Playnite.Core.Tests.DesktopIntegration;

public sealed class StdinHoldTests
{
    private static ProcessStartInfo HoldStartInfo() => new()
    {
        FileName = "/bin/cat",
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    [Fact]
    public async Task ClosingTheLifelineReleasesTheChildWithoutAnyOtherCleanup()
    {
        var hold = await StdinHold.StartAsync(
            HoldStartInfo(),
            "test-hold",
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.NotNull(hold);
        Assert.True(hold.IsHeld);
        Assert.False(hold.HasExited);

        // This is the crash path: only the pipe closes, exactly as the kernel does when the
        // parent process dies. No lease disposal runs before the child exits.
        hold.CloseLifeline();
        Assert.True(await WaitForAsync(() => hold.HasExited, TimeSpan.FromSeconds(5)));

        await hold.DisposeAsync();
        Assert.True(hold.Released);
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        var hold = await StdinHold.StartAsync(
            HoldStartInfo(),
            "test-hold",
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.NotNull(hold);
        await hold.DisposeAsync();
        await hold.DisposeAsync();

        Assert.True(hold.Released);
    }

    [Fact]
    public async Task ACommandThatExitsDuringStartupIsReportedAsNoHold()
    {
        var startInfo = HoldStartInfo();
        startInfo.FileName = "/bin/false";

        var hold = await StdinHold.StartAsync(
            startInfo,
            "test-hold",
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);

        Assert.Null(hold);
    }

    [Fact]
    public void SystemdInhibitBuildsTheDocumentedArgumentList()
    {
        var startInfo = SystemdInhibitBackend.CreateStartInfo(
            new SleepInhibitionRequest("Playnite", "Playing a game"),
            "systemd-inhibit",
            "cat");

        Assert.Equal("systemd-inhibit", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.Equal(
            new[] { "--what=sleep", "--who=Playnite", "--why=Playing a game", "--mode=block", "cat" },
            startInfo.ArgumentList);
    }

    [Fact]
    public async Task MissingToolsAreReportedAsUnavailableNotThrown()
    {
        var backend = new SystemdInhibitBackend(new FakeExecutableLocator());

        Assert.False(backend.IsAvailable);
        var result = await backend.TryAcquireAsync(new SleepInhibitionRequest());

        Assert.Null(result.Handle);
        Assert.Contains("not installed", result.Detail, StringComparison.Ordinal);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }
}

public sealed class LinuxDesktopIntegrationDiagnosticsTests
{
    private static LinuxDesktopIntegrationDiagnostics Create(
        DesktopSessionKind kind,
        bool watcher,
        params string[] tools)
    {
        var session = new FakeDesktopSessionProbe
        {
            Session = new DesktopSession(kind, "test-session")
        };
        var host = new FakeTrayHostProbe
        {
            Status = new TrayHostStatus(watcher, watcher ? "watcher present" : "no watcher")
        };
        var locator = new FakeExecutableLocator();
        foreach (var tool in tools)
        {
            locator.Names.Add(tool);
        }

        return new LinuxDesktopIntegrationDiagnostics(session, host, locator);
    }

    [Fact]
    public async Task HeadlessWithoutToolsReportsEveryFacilityUnavailable()
    {
        var session = new FakeDesktopSessionProbe
        {
            Session = new DesktopSession(DesktopSessionKind.None, "test-session")
        };
        var locator = new FakeExecutableLocator();
        var trayProbe = new LinuxTrayHostProbe(session, new FakeExternalCommandRunner(), locator);
        var diagnostics = new LinuxDesktopIntegrationDiagnostics(session, trayProbe, locator);

        var report = await diagnostics.DiagnoseAsync();

        Assert.Equal(DesktopSessionKind.None, report.Session.Kind);
        Assert.All(
            report.Facilities,
            capability => Assert.Equal(DesktopFacilityState.Unavailable, capability.State));
        Assert.Contains("no graphical session", report.For(DesktopFacility.Tray).Detail, StringComparison.Ordinal);
        Assert.Contains("neither notify-send nor gdbus", report.For(DesktopFacility.Notifications).Detail, StringComparison.Ordinal);
        Assert.Contains("systemd-inhibit is not installed", report.For(DesktopFacility.SleepInhibition).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompleteGraphicalSessionReportsAllFacilitiesAvailable()
    {
        var diagnostics = Create(
            DesktopSessionKind.Wayland,
            watcher: true,
            NotifySendBackend.ToolName,
            GdbusNotificationBackend.ToolName,
            SystemdInhibitBackend.ExecutableName,
            SystemdInhibitBackend.HoldCommandName);

        var report = await diagnostics.DiagnoseAsync();

        Assert.Equal(DesktopSessionKind.Wayland, report.Session.Kind);
        Assert.All(
            report.Facilities,
            capability => Assert.Equal(DesktopFacilityState.Available, capability.State));
        Assert.Equal("StatusNotifierItem", report.For(DesktopFacility.Tray).Mechanism);
        Assert.Equal(NotifySendBackend.ToolName, report.For(DesktopFacility.Notifications).Mechanism);
        Assert.Equal(SystemdInhibitBackend.ExecutableName, report.For(DesktopFacility.SleepInhibition).Mechanism);
    }

    [Fact]
    public async Task GdbusIsTheNotificationsFallbackWhenNotifySendIsMissing()
    {
        var diagnostics = Create(
            DesktopSessionKind.X11,
            watcher: false,
            GdbusNotificationBackend.ToolName,
            SystemdInhibitBackend.ExecutableName,
            SystemdInhibitBackend.HoldCommandName);

        var report = await diagnostics.DiagnoseAsync();

        var notifications = report.For(DesktopFacility.Notifications);
        Assert.Equal(DesktopFacilityState.Available, notifications.State);
        Assert.Equal(GdbusNotificationBackend.ToolName, notifications.Mechanism);
        Assert.Contains("fallback", notifications.Detail, StringComparison.Ordinal);
        Assert.Equal(DesktopFacilityState.Unavailable, report.For(DesktopFacility.Tray).State);
    }

    [Fact]
    public async Task MissingHoldCommandMakesSleepInhibitionUnavailable()
    {
        var diagnostics = Create(
            DesktopSessionKind.Wayland,
            watcher: false,
            SystemdInhibitBackend.ExecutableName);

        var report = await diagnostics.DiagnoseAsync();

        var sleep = report.For(DesktopFacility.SleepInhibition);
        Assert.Equal(DesktopFacilityState.Unavailable, sleep.State);
        Assert.Contains("hold command", sleep.Detail, StringComparison.Ordinal);
    }
}
