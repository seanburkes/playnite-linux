using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Linux.DesktopIntegration;
using Xunit;

namespace Playnite.Core.Tests.DesktopIntegration;

public sealed class LinuxSleepInhibitionServiceTests
{
    private static SleepInhibitionRequest Request() => new("Playnite", "Test session");

    [Fact]
    public async Task NoBackendReturnsANotHeldLease()
    {
        var service = new LinuxSleepInhibitionService([]);

        var lease = await service.InhibitAsync(Request());

        Assert.False(lease.IsHeld);
        Assert.Equal(DesktopFacilityState.Unavailable, lease.Capability.State);
        Assert.Contains("no sleep inhibitor backend", lease.Capability.Detail, StringComparison.Ordinal);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task UnavailableBackendFallsThroughAndReportsWithoutThrowing()
    {
        var backend = new FakeSleepInhibitorBackend { IsAvailable = false };
        var service = new LinuxSleepInhibitionService([backend]);

        var lease = await service.InhibitAsync(Request());

        Assert.False(lease.IsHeld);
        Assert.Contains("fake-inhibitor: not available", lease.Capability.Detail, StringComparison.Ordinal);
        Assert.Equal(0, backend.AcquireCalls);
    }

    [Fact]
    public async Task HeldLeaseReportsItsMechanismAndReleasesOnce()
    {
        var backend = new FakeSleepInhibitorBackend();
        var service = new LinuxSleepInhibitionService([backend]);

        var lease = await service.InhibitAsync(Request());

        Assert.True(lease.IsHeld);
        Assert.Equal("fake-inhibitor", lease.Capability.Mechanism);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.False(lease.IsHeld);
        Assert.Equal(1, backend.Events.Count(item => item == "release"));
    }

    [Fact]
    public async Task BackendExceptionDegradesToTheNextBackend()
    {
        var failing = new FakeSleepInhibitorBackend
        {
            Mechanism = "failing",
            AcquireFailure = new InvalidOperationException("logind vanished")
        };
        var working = new FakeSleepInhibitorBackend { Mechanism = "working" };
        var service = new LinuxSleepInhibitionService([failing, working]);

        var lease = await service.InhibitAsync(Request());

        Assert.True(lease.IsHeld);
        Assert.Equal("working", lease.Capability.Mechanism);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task CancellationDuringAcquirePropagatesWithoutALease()
    {
        var backend = new FakeSleepInhibitorBackend
        {
            AcquireFailure = new OperationCanceledException()
        };
        var service = new LinuxSleepInhibitionService([backend]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InhibitAsync(Request()));
        Assert.Equal(0, backend.Events.Count(item => item == "release"));
    }
}

public sealed class SessionSleepInhibitionTests
{
    [Fact]
    public async Task AcquireCompletesBeforeStartAndReleaseCompletesAfterStop()
    {
        var service = new FakeSleepInhibitionService();
        await using var coordinator = new SessionSleepInhibition(service);
        var events = service.Events;

        var held = await coordinator.BeginSessionAsync("session-1");
        events.Add("game-started");
        await coordinator.EndSessionAsync("session-1");
        events.Add("game-stopped");

        Assert.True(held);
        Assert.Equal(
            new[] { "inhibit-acquire", "game-started", "release", "game-stopped" },
            events);
        Assert.Equal(0, service.ActiveHandles);
        Assert.Equal(0, coordinator.ActiveSessionCount);
        Assert.False(coordinator.IsHeld);
    }

    [Fact]
    public async Task BeginWaitsForTheInhibitorBeforeReturning()
    {
        var service = new FakeSleepInhibitionService
        {
            AcquireGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var coordinator = new SessionSleepInhibition(service);

        var begin = coordinator.BeginSessionAsync("session-1");

        Assert.False(begin.IsCompleted);
        service.AcquireGate.SetResult(true);
        Assert.True(await begin);
        Assert.True(coordinator.IsHeld);
    }

    [Fact]
    public async Task EndWaitsForTheReleaseBeforeReturning()
    {
        var service = new FakeSleepInhibitionService
        {
            ReleaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var coordinator = new SessionSleepInhibition(service);
        await coordinator.BeginSessionAsync("session-1");

        var end = coordinator.EndSessionAsync("session-1");

        Assert.False(end.IsCompleted);
        Assert.Equal(1, service.ActiveHandles);
        service.ReleaseGate.SetResult(true);
        await end;
        Assert.Equal(0, service.ActiveHandles);
        Assert.False(coordinator.IsHeld);
    }

    [Fact]
    public async Task ConcurrentSessionsShareOneLeaseAndTheLastEndReleasesIt()
    {
        var service = new FakeSleepInhibitionService();
        await using var coordinator = new SessionSleepInhibition(service);

        Assert.True(await coordinator.BeginSessionAsync("a"));
        Assert.True(await coordinator.BeginSessionAsync("b"));
        Assert.Equal(1, service.AcquireCalls);
        Assert.Equal(2, coordinator.ActiveSessionCount);

        await coordinator.EndSessionAsync("a");
        Assert.Equal(1, service.ActiveHandles);
        Assert.True(coordinator.IsHeld);

        await coordinator.EndSessionAsync("b");
        Assert.Equal(0, service.ActiveHandles);
        Assert.Equal(1, service.Events.Count(item => item == "release"));
    }

    [Fact]
    public async Task DuplicateAndUnknownSessionIdsDoNotAcquireOrReleaseTwice()
    {
        var service = new FakeSleepInhibitionService();
        await using var coordinator = new SessionSleepInhibition(service);

        await coordinator.BeginSessionAsync("a");
        await coordinator.BeginSessionAsync("a");
        await coordinator.EndSessionAsync("missing");
        await coordinator.EndSessionAsync("a");
        await coordinator.EndSessionAsync("a");

        Assert.Equal(1, service.AcquireCalls);
        Assert.Equal(1, service.Events.Count(item => item == "release"));
        Assert.Equal(0, coordinator.ActiveSessionCount);
    }

    [Fact]
    public async Task ManyConcurrentSessionEdgesLeakNoInhibitor()
    {
        var service = new FakeSleepInhibitionService();
        await using var coordinator = new SessionSleepInhibition(service);
        var sessions = Enumerable.Range(0, 32).Select(index => "session-" + index).ToArray();

        await Task.WhenAll(sessions.Select(id => coordinator.BeginSessionAsync(id)));
        Assert.Equal(1, service.AcquireCalls);
        Assert.Equal(32, coordinator.ActiveSessionCount);

        await Task.WhenAll(sessions.Select(id => coordinator.EndSessionAsync(id)));
        Assert.Equal(0, service.ActiveHandles);
        Assert.Equal(1, service.Events.Count(item => item == "release"));
        Assert.Equal(0, coordinator.ActiveSessionCount);
    }

    [Fact]
    public async Task UnavailableInhibitionStillLetsTheSessionStartAndKeepsTheReport()
    {
        var service = new FakeSleepInhibitionService { Available = false };
        await using var coordinator = new SessionSleepInhibition(service);

        var held = await coordinator.BeginSessionAsync("session-1");

        Assert.False(held);
        Assert.False(coordinator.IsHeld);
        Assert.Equal(DesktopFacilityState.Unavailable, coordinator.Capability.State);
        Assert.Equal(1, coordinator.ActiveSessionCount);
        await coordinator.EndSessionAsync("session-1");
        Assert.Equal(0, service.ActiveHandles);
    }

    [Fact]
    public async Task CancelledAcquireLeavesNoSessionAndCanBeRetried()
    {
        var service = new FakeSleepInhibitionService
        {
            AcquireGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var coordinator = new SessionSleepInhibition(service);
        using var cancellation = new CancellationTokenSource();

        var begin = coordinator.BeginSessionAsync("session-1", cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => begin);
        Assert.Equal(0, coordinator.ActiveSessionCount);
        Assert.Empty(service.Events.Where(item => item == "release"));

        service.AcquireGate = null;
        Assert.True(await coordinator.BeginSessionAsync("session-1"));
        Assert.Equal(1, coordinator.ActiveSessionCount);
    }

    [Fact]
    public async Task CancelledEndStillReleasesTheLastSessionAndInhibitor()
    {
        var service = new FakeSleepInhibitionService();
        await using var coordinator = new SessionSleepInhibition(service);
        await coordinator.BeginSessionAsync("session-1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await coordinator.EndSessionAsync("session-1", cancellation.Token);

        Assert.Equal(0, coordinator.ActiveSessionCount);
        Assert.False(coordinator.IsHeld);
        Assert.Equal(0, service.ActiveHandles);
        Assert.Equal(1, service.Events.Count(item => item == "release"));
    }

    [Fact]
    public async Task CancelledEndOfOneSessionKeepsTheLeaseForTheSurvivor()
    {
        var service = new FakeSleepInhibitionService();
        await using var coordinator = new SessionSleepInhibition(service);
        await coordinator.BeginSessionAsync("session-1");
        await coordinator.BeginSessionAsync("session-2");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await coordinator.EndSessionAsync("session-1", cancellation.Token);

        Assert.Equal(1, coordinator.ActiveSessionCount);
        Assert.True(coordinator.IsHeld);
        Assert.Equal(1, service.ActiveHandles);

        await coordinator.EndSessionAsync("session-2");
        Assert.Equal(0, coordinator.ActiveSessionCount);
        Assert.False(coordinator.IsHeld);
        Assert.Equal(0, service.ActiveHandles);
        Assert.Equal(1, service.Events.Count(item => item == "release"));
    }

    [Fact]
    public async Task ShutdownReleasesTheLeaseAndRejectsNewSessions()
    {
        var service = new FakeSleepInhibitionService();
        var coordinator = new SessionSleepInhibition(service);
        await coordinator.BeginSessionAsync("session-1");

        await coordinator.ShutdownAsync();
        await coordinator.ShutdownAsync();

        Assert.Equal(0, service.ActiveHandles);
        Assert.Equal(1, service.Events.Count(item => item == "release"));
        Assert.Equal(0, coordinator.ActiveSessionCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.BeginSessionAsync("late"));
        await coordinator.DisposeAsync();
    }
}
