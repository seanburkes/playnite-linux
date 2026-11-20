using Playnite.Core.Hosting;
using Playnite.Platform.DesktopIntegration;
using Xunit;
using Playnite.SDK;

namespace Playnite.Core.Tests.Hosting;

public sealed class NotificationServiceHostTests
{
    [Fact]
    public async Task InfoMapsToNormalUrgencyAndADeliveryIsPresented()
    {
        var service = new FakeNotificationService();
        var host = new NotificationServiceHost(service);

        var result = await host.PresentAsync(new NotificationMessage("update", "Ready", NotificationType.Info));

        Assert.True(result.WasPresented);
        var request = Assert.Single(service.Requests);
        Assert.Equal("update", request.Id);
        Assert.Equal("Playnite", request.Title);
        Assert.Equal("Ready", request.Body);
        Assert.Equal(NotificationUrgency.Normal, request.Urgency);
    }

    [Fact]
    public async Task ErrorMapsToCriticalUrgency()
    {
        var service = new FakeNotificationService();
        var host = new NotificationServiceHost(service);

        await host.PresentAsync(new NotificationMessage("failed", "Import failed", NotificationType.Error));

        Assert.Equal(NotificationUrgency.Critical, Assert.Single(service.Requests).Urgency);
    }

    [Fact]
    public async Task ATransportDuplicateIsStillReportedAsPresented()
    {
        var service = new FakeNotificationService
        {
            Handler = (_, _) => Task.FromResult(
                new NotificationDelivery(NotificationDeliveryState.SuppressedDuplicate, "dedupe", "within 5s")),
        };
        var host = new NotificationServiceHost(service);

        var result = await host.PresentAsync(new NotificationMessage("update", "Ready"));

        Assert.True(result.WasPresented);
        Assert.Contains("duplicate", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnavailableDeliveryIsReportedWithTheServiceDetail()
    {
        var service = new FakeNotificationService
        {
            Handler = (_, _) => Task.FromResult(
                new NotificationDelivery(NotificationDeliveryState.Unavailable, "notify-send", "no session bus")),
        };
        var host = new NotificationServiceHost(service);

        var result = await host.PresentAsync(new NotificationMessage("update", "Ready"));

        Assert.Equal(NotificationHostStatus.Unavailable, result.Status);
        Assert.Equal("no session bus", result.Diagnostic);
    }

    [Fact]
    public async Task DismissReportsThatTheDesktopServiceCannotDismiss()
    {
        var service = new FakeNotificationService();
        var host = new NotificationServiceHost(service);

        var result = await host.DismissAsync("update");

        Assert.Equal(NotificationHostStatus.Unavailable, result.Status);
        Assert.Contains("does not expose dismissal", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringTheServiceCallThrows()
    {
        using var source = new CancellationTokenSource();
        var service = new FakeNotificationService
        {
            Handler = async (_, token) =>
            {
                source.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new NotificationDelivery(NotificationDeliveryState.Delivered, "fake", "sent");
            },
        };
        var host = new NotificationServiceHost(service);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.PresentAsync(new NotificationMessage("update", "Ready"), source.Token));
    }

    [Fact]
    public async Task CancellationBeforeTheServiceCallThrowsWithoutCallingIt()
    {
        var service = new FakeNotificationService();
        var host = new NotificationServiceHost(service);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.PresentAsync(new NotificationMessage("update", "Ready"), source.Token));

        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task DismissThroughTheApiReportsTheUnsupportedHostAndStillRemovesTheMessage()
    {
        var host = new NotificationServiceHost(new FakeNotificationService());
        var api = new NotificationsApi(host);
        await api.PublishAsync(new NotificationMessage("update", "Ready"));

        var result = await api.DismissAsync("update");

        Assert.Equal(NotificationDismissStatus.Dismissed, result.Status);
        Assert.Contains("Removed locally", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("does not expose dismissal", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public void ANotificationServiceIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new NotificationServiceHost(null!));
    }
}
