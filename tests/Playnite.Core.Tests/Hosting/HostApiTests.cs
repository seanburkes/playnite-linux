using Playnite.Core.Hosting;
using Xunit;

namespace Playnite.Core.Tests.Hosting;

public sealed class HostApiTests
{
    [Fact]
    public async Task CreateWithNoHostsDoesNotThrowAndNamesEveryMissingHost()
    {
        var apis = HostApis.Create();

        Assert.Equal(3, apis.Diagnostics.Count);
        Assert.Contains(apis.Diagnostics, line => line.Contains("notification host", StringComparison.Ordinal));
        Assert.Contains(apis.Diagnostics, line => line.Contains("dialog host", StringComparison.Ordinal));
        Assert.Contains(apis.Diagnostics, line => line.Contains("main-view host", StringComparison.Ordinal));

        var publish = await apis.Notifications.PublishAsync("update", "Ready", NotificationType.Info);
        var dialog = await apis.Dialogs.ShowMessageAsync(new MessageDialogRequest("Text"));
        var view = await apis.MainView.GetStateAsync();

        Assert.Equal(NotificationPublishStatus.Unavailable, publish.Status);
        Assert.Equal(HostOperationStatus.Unavailable, dialog.Status);
        Assert.Equal(HostOperationStatus.Unavailable, view.Status);
    }

    [Fact]
    public async Task CreateWithHostsWiresEverySurfaceAndLeavesNoDiagnostic()
    {
        var notificationHost = new FakeNotificationHost();
        var dialogHost = new FakeDialogHost();
        var mainViewHost = new FakeMainViewHost();

        var apis = HostApis.Create(notificationHost, dialogHost, mainViewHost);

        Assert.Empty(apis.Diagnostics);
        var publish = await apis.Notifications.PublishAsync("update", "Ready", NotificationType.Info);
        var dialog = await apis.Dialogs.ShowMessageAsync(new MessageDialogRequest("Text"));
        var view = await apis.MainView.GetStateAsync();

        Assert.Equal(NotificationPublishStatus.Published, publish.Status);
        Assert.Equal(HostOperationStatus.Completed, dialog.Status);
        Assert.Equal(HostOperationStatus.Completed, view.Status);
        Assert.Single(notificationHost.Presented);
        Assert.Single(dialogHost.Messages);
    }

    [Fact]
    public async Task APartiallyRegisteredSetOnlyReportsTheMissingSurface()
    {
        var apis = HostApis.Create(notificationHost: new FakeNotificationHost());

        Assert.Equal(2, apis.Diagnostics.Count);
        var publish = await apis.Notifications.PublishAsync("update", "Ready", NotificationType.Info);
        var view = await apis.MainView.GetStateAsync();

        Assert.Equal(NotificationPublishStatus.Published, publish.Status);
        Assert.Equal(HostOperationStatus.Unavailable, view.Status);
    }
}
