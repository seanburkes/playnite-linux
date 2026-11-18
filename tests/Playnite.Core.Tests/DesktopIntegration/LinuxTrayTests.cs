using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Linux.DesktopIntegration;
using Xunit;

namespace Playnite.Core.Tests.DesktopIntegration;

public sealed class LinuxTrayHostProbeTests
{
    private static (LinuxTrayHostProbe Probe, FakeExternalCommandRunner Runner, FakeExecutableLocator Locator, FakeDesktopSessionProbe Session) Create(
        DesktopSessionKind kind = DesktopSessionKind.Wayland)
    {
        var session = new FakeDesktopSessionProbe
        {
            Session = new DesktopSession(kind, "test-session")
        };
        var runner = new FakeExternalCommandRunner();
        var locator = new FakeExecutableLocator();
        return (new LinuxTrayHostProbe(session, runner, locator), runner, locator, session);
    }

    [Fact]
    public async Task HeadlessSessionDegradesWithoutRunningAProbe()
    {
        var (probe, runner, locator, _) = Create(DesktopSessionKind.None);
        locator.Names.Add("gdbus");
        runner.Results.Enqueue(new ExternalCommandResult(true, 0, "(true,)", "", null));

        var status = await probe.ProbeAsync();

        Assert.False(status.HasStatusNotifierWatcher);
        Assert.Contains("no graphical session", status.Detail, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task GdbusNameOwnerIsTheHost()
    {
        var (probe, runner, locator, _) = Create();
        locator.Names.Add("gdbus");
        runner.Results.Enqueue(new ExternalCommandResult(true, 0, "(true,)", "", null));

        var status = await probe.ProbeAsync();

        Assert.True(status.HasStatusNotifierWatcher);
        Assert.Contains("gdbus", status.Detail, StringComparison.Ordinal);
        Assert.Equal("gdbus", runner.Commands.Single().FileName);
        Assert.Contains("org.kde.StatusNotifierWatcher", runner.Commands.Single().Arguments);
    }

    [Fact]
    public async Task MissingWatcherOnWaylandNamesTheWaylandLimitation()
    {
        var (probe, runner, locator, _) = Create(DesktopSessionKind.Wayland);
        locator.Names.Add("gdbus");
        runner.Results.Enqueue(new ExternalCommandResult(true, 0, "(false,)", "", null));

        var status = await probe.ProbeAsync();

        Assert.False(status.HasStatusNotifierWatcher);
        Assert.Contains("no XEmbed tray", status.Detail, StringComparison.Ordinal);
        Assert.Contains("StatusNotifierItem", status.Detail, StringComparison.Ordinal);
        Assert.Equal(1, runner.Commands.Count);
    }

    [Fact]
    public async Task MissingWatcherOnX11StatesTheXEmbedV1Limit()
    {
        var (probe, runner, locator, _) = Create(DesktopSessionKind.X11);
        locator.Names.Add("gdbus");
        runner.Results.Enqueue(new ExternalCommandResult(true, 0, "(false,)", "", null));

        var status = await probe.ProbeAsync();

        Assert.False(status.HasStatusNotifierWatcher);
        Assert.Contains("X11 XEmbed trays are not supported in v1", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedToolFallsThroughToTheNextProbe()
    {
        var (probe, runner, locator, _) = Create();
        locator.Names.Add("gdbus");
        locator.Names.Add("busctl");
        runner.Results.Enqueue(new ExternalCommandResult(true, 1, "", "session bus unavailable", null));
        runner.Results.Enqueue(new ExternalCommandResult(true, 0, "b true", "", null));

        var status = await probe.ProbeAsync();

        Assert.True(status.HasStatusNotifierWatcher);
        Assert.Contains("busctl", status.Detail, StringComparison.Ordinal);
        Assert.Equal(new[] { "gdbus", "busctl" }, runner.Commands.Select(command => command.FileName));
    }

    [Fact]
    public async Task NoProbeToolIsReportedAsADegradedTrayNotAnException()
    {
        var (probe, runner, _, _) = Create();

        var status = await probe.ProbeAsync();

        Assert.False(status.HasStatusNotifierWatcher);
        Assert.Contains("no D-Bus probe succeeded", status.Detail, StringComparison.Ordinal);
        Assert.Contains("gdbus: not installed", status.Detail, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }
}

public sealed class LinuxTrayServiceTests
{
    private static TrayModel Model() => new(
        "playnite",
        "Playnite",
        [new TrayMenuItem("show", "Show Playnite", IsDefault: true), new TrayMenuItem("exit", "Exit")]);

    [Fact]
    public async Task MissingHostReturnsAnUnhostedInstallation()
    {
        var host = new FakeTrayHostProbe { Status = new TrayHostStatus(false, "no host") };
        var presenter = new FakeTrayIconPresenter();
        var service = new LinuxTrayService(host, presenter);

        var installation = await service.InstallAsync(Model());

        Assert.False(installation.IsHosted);
        Assert.Equal(DesktopFacilityState.Unavailable, installation.Capability.State);
        Assert.Null(presenter.Presented);
        await installation.DisposeAsync();
        Assert.Equal(0, presenter.Handle.DisposeCalls);
    }

    [Fact]
    public async Task HostWithoutAPresenterIsReportedAvailableNowhere()
    {
        var host = new FakeTrayHostProbe { Status = new TrayHostStatus(true, "watcher present") };
        var service = new LinuxTrayService(host);

        var installation = await service.InstallAsync(Model());

        Assert.False(installation.IsHosted);
        Assert.Contains("no tray presenter", installation.Capability.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostedInstallationPresentsOnceAndDisposesOnce()
    {
        var host = new FakeTrayHostProbe { Status = new TrayHostStatus(true, "watcher present") };
        var presenter = new FakeTrayIconPresenter();
        var service = new LinuxTrayService(host, presenter);
        var model = Model();

        var installation = await service.InstallAsync(model);

        Assert.True(installation.IsHosted);
        Assert.Same(model, presenter.Presented);
        Assert.Equal("StatusNotifierItem", installation.Capability.Mechanism);

        await installation.DisposeAsync();
        await installation.DisposeAsync();
        Assert.Equal(1, presenter.Handle.DisposeCalls);
    }

    [Fact]
    public async Task PresenterFailureDegradesWithoutFailingStartup()
    {
        var host = new FakeTrayHostProbe { Status = new TrayHostStatus(true, "watcher present") };
        var presenter = new FakeTrayIconPresenter { Failure = new InvalidOperationException("no icon") };
        var service = new LinuxTrayService(host, presenter);

        var installation = await service.InstallAsync(Model());

        Assert.False(installation.IsHosted);
        Assert.Contains("tray presenter failed: no icon", installation.Capability.Detail, StringComparison.Ordinal);
        await installation.DisposeAsync();
    }

    [Fact]
    public async Task PresenterCancellationPropagates()
    {
        var host = new FakeTrayHostProbe { Status = new TrayHostStatus(true, "watcher present") };
        var presenter = new FakeTrayIconPresenter { Failure = new OperationCanceledException() };
        var service = new LinuxTrayService(host, presenter);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.InstallAsync(Model()));
    }
}
