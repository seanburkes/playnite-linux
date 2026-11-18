using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Linux.DesktopIntegration;
using Xunit;

namespace Playnite.Core.Tests.DesktopIntegration;

public sealed class LinuxNotificationServiceTests
{
    private static NotificationRequest Request(string id = "game-started", string body = "Game started") =>
        new(id, "Playnite", body);

    [Fact]
    public async Task DeliversThroughTheFirstAvailableBackend()
    {
        var primary = new FakeNotificationBackend { Mechanism = "primary" };
        var fallback = new FakeNotificationBackend { Mechanism = "fallback" };
        var service = new LinuxNotificationService([primary, fallback]);

        var delivery = await service.ShowAsync(Request());

        Assert.True(delivery.WasDelivered);
        Assert.Equal("primary", delivery.Mechanism);
        Assert.Empty(fallback.Requests);
    }

    [Fact]
    public async Task UnavailableBackendFallsBackToTheNextMechanism()
    {
        var primary = new FakeNotificationBackend { Mechanism = "primary", IsAvailable = false };
        var fallback = new FakeNotificationBackend { Mechanism = "fallback" };
        var service = new LinuxNotificationService([primary, fallback]);

        var delivery = await service.ShowAsync(Request());

        Assert.True(delivery.WasDelivered);
        Assert.Equal("fallback", delivery.Mechanism);
        Assert.Empty(primary.Requests);
    }

    [Fact]
    public async Task AllBackendsUnavailableReportsWithoutThrowing()
    {
        var service = new LinuxNotificationService(
            [new FakeNotificationBackend { Mechanism = "one", IsAvailable = false },
             new FakeNotificationBackend { Mechanism = "two", IsAvailable = false }]);

        var delivery = await service.ShowAsync(Request());

        Assert.Equal(NotificationDeliveryState.Unavailable, delivery.State);
        Assert.Contains("one: not available", delivery.Detail, StringComparison.Ordinal);
        Assert.Contains("two: not available", delivery.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedNotificationInsideTheWindowIsSuppressed()
    {
        var backend = new FakeNotificationBackend();
        var clock = new FakeTimeProvider();
        var service = new LinuxNotificationService([backend], clock);
        var request = Request();

        Assert.True((await service.ShowAsync(request)).WasDelivered);
        var duplicate = await service.ShowAsync(request);

        Assert.Equal(NotificationDeliveryState.SuppressedDuplicate, duplicate.State);
        Assert.Single(backend.Requests);

        clock.Advance(LinuxNotificationService.DefaultDuplicateWindow + TimeSpan.FromMilliseconds(1));
        Assert.True((await service.ShowAsync(request)).WasDelivered);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task DifferentIdOrBodyIsNotADuplicate()
    {
        var backend = new FakeNotificationBackend();
        var service = new LinuxNotificationService([backend], new FakeTimeProvider());

        Assert.True((await service.ShowAsync(Request(id: "first"))).WasDelivered);
        Assert.True((await service.ShowAsync(Request(id: "second"))).WasDelivered);
        Assert.True((await service.ShowAsync(Request(id: "first", body: "Different"))).WasDelivered);
        Assert.Equal(3, backend.Requests.Count);
    }

    [Fact]
    public async Task ZeroWindowDisablesDeduplication()
    {
        var backend = new FakeNotificationBackend();
        var service = new LinuxNotificationService([backend], new FakeTimeProvider(), TimeSpan.Zero);

        Assert.True((await service.ShowAsync(Request())).WasDelivered);
        Assert.True((await service.ShowAsync(Request())).WasDelivered);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task BackendFailureFallsThroughAndAFailedDeliveryDoesNotPoisonRetry()
    {
        var failing = new FakeNotificationBackend { Mechanism = "failing", Failure = new IOException("bus down") };
        var service = new LinuxNotificationService([failing], new FakeTimeProvider());

        var unavailable = await service.ShowAsync(Request());
        Assert.Equal(NotificationDeliveryState.Unavailable, unavailable.State);
        Assert.Contains("failing: bus down", unavailable.Detail, StringComparison.Ordinal);

        failing.Failure = null;
        var delivered = await service.ShowAsync(Request());

        Assert.True(delivered.WasDelivered);
        Assert.Equal(2, failing.Requests.Count);
    }

    [Fact]
    public async Task ShowAsyncDoesNotBlockTheCallingThread()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeNotificationBackend { Gate = gate };
        var service = new LinuxNotificationService([backend]);

        var delivery = service.ShowAsync(Request());

        Assert.False(delivery.IsCompleted);
        Assert.Single(backend.Requests);

        gate.SetResult(true);
        Assert.True((await delivery).WasDelivered);
    }

    [Fact]
    public async Task CancellationPropagatesAndAllowsARetry()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeNotificationBackend { Gate = gate };
        var service = new LinuxNotificationService([backend]);
        using var cancellation = new CancellationTokenSource();

        var delivery = service.ShowAsync(Request(), cancellation.Token);
        Assert.Single(backend.Requests);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);

        backend.Gate = null;
        Assert.True((await service.ShowAsync(Request())).WasDelivered);
    }

    [Fact]
    public async Task NotifySendUsesAnArgumentListAndTheOptionTerminator()
    {
        var runner = new FakeExternalCommandRunner();
        var locator = new FakeExecutableLocator();
        locator.Names.Add(NotifySendBackend.ToolName);
        var backend = new NotifySendBackend(runner, locator);

        var result = await backend.SendAsync(new NotificationRequest(
            "id",
            "-title",
            "line one\nline two",
            NotificationUrgency.Critical,
            TimeSpan.FromSeconds(3)));

        Assert.True(result.Sent);
        var command = runner.Commands.Single();
        Assert.Equal("notify-send", command.FileName);
        Assert.Contains("--urgency=critical", command.Arguments);
        Assert.Contains("--expire-time=3000", command.Arguments);
        Assert.Equal(new[] { "--", "-title", "line one\nline two" }, command.Arguments.TakeLast(3));
    }

    [Fact]
    public async Task GdbusSendsQuotedGVariantTextAndTypedEmptyContainers()
    {
        var runner = new FakeExternalCommandRunner();
        var locator = new FakeExecutableLocator();
        locator.Names.Add(GdbusNotificationBackend.ToolName);
        var backend = new GdbusNotificationBackend(runner, locator);

        var result = await backend.SendAsync(new NotificationRequest(
            "id",
            "it's a title",
            "back\\slash",
            NotificationUrgency.Normal));

        Assert.True(result.Sent);
        var arguments = runner.Commands.Single().Arguments;
        Assert.Contains("'it\\'s a title'", arguments);
        Assert.Contains("'back\\\\slash'", arguments);
        Assert.Contains("@as []", arguments);
        Assert.Contains("@a{sv} {}", arguments);
        Assert.Contains("-1", arguments);
    }

    [Fact]
    public void GdbusTextEscapesQuotesBackslashesAndControls()
    {
        Assert.Equal("'plain'", GdbusText.Quote("plain"));
        Assert.Equal("'it\\'s'", GdbusText.Quote("it's"));
        Assert.Equal("'a\\\\b'", GdbusText.Quote("a\\b"));
        Assert.Equal("'a\\nb\\rc\\td'", GdbusText.Quote("a\nb\rc\td"));
    }
}
