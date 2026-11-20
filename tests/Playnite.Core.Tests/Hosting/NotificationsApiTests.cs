using Playnite.Core.Hosting;
using Xunit;

namespace Playnite.Core.Tests.Hosting;

public sealed class NotificationsApiTests
{
    private static NotificationMessage Message(string id = "update", string text = "Ready") =>
        new(id, text);

    [Fact]
    public async Task PublishPresentsThroughTheHostAndTracksTheMessage()
    {
        var host = new FakeNotificationHost();
        var api = new NotificationsApi(host);
        var changed = 0;
        api.Changed += (_, _) => changed++;

        var result = await api.PublishAsync(Message());

        Assert.Equal(NotificationPublishStatus.Published, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Single(host.Presented);
        Assert.Equal("update", host.Presented[0].Id);
        Assert.Single(api.Messages);
        Assert.Equal(1, api.Count);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task PublishSuppressesARepeatedIdWithoutCallingTheHostAgain()
    {
        var host = new FakeNotificationHost();
        var api = new NotificationsApi(host);

        await api.PublishAsync(Message("update", "Ready"));
        var repeat = await api.PublishAsync(Message("update", "Different text"));

        Assert.Equal(NotificationPublishStatus.Duplicate, repeat.Status);
        Assert.True(repeat.IsSuccess);
        Assert.Contains("already published", repeat.Diagnostic, StringComparison.Ordinal);
        Assert.Single(host.Presented);
        Assert.Single(api.Messages);
        Assert.Equal("Ready", api.Messages[0].Text);
    }

    [Fact]
    public async Task IdsAreComparedOrdinally()
    {
        var host = new FakeNotificationHost();
        var api = new NotificationsApi(host);

        await api.PublishAsync(Message("Update"));
        await api.PublishAsync(Message("update"));

        Assert.Equal(2, api.Count);
    }

    [Fact]
    public async Task ConcurrentDuplicatePublishesShareOneHostRequest()
    {
        var release = new TaskCompletionSource<NotificationHostResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeNotificationHost { PresentHandler = (_, _) => release.Task };
        var api = new NotificationsApi(host);

        var first = api.PublishAsync(Message("update"));
        var second = api.PublishAsync(Message("update"));
        release.SetResult(new NotificationHostResult(NotificationHostStatus.Presented));

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(NotificationPublishStatus.Published, result.Status));
        Assert.Single(host.Presented);
        Assert.Single(api.Messages);
    }

    [Fact]
    public async Task MissingHostDegradesWithADiagnosticInsteadOfThrowing()
    {
        var api = new NotificationsApi();

        var result = await api.PublishAsync(Message());

        Assert.Equal(NotificationPublishStatus.Unavailable, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Equal(NotificationsApi.MissingHostDiagnostic, result.Diagnostic);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task AnUnavailableHostIsReportedAndARetryIsNotSuppressed()
    {
        var host = new FakeNotificationHost
        {
            PresentHandler = (_, _) => Task.FromResult(
                new NotificationHostResult(NotificationHostStatus.Unavailable, "no session bus")),
        };
        var api = new NotificationsApi(host);

        var first = await api.PublishAsync(Message());
        var second = await api.PublishAsync(Message());

        Assert.Equal(NotificationPublishStatus.Unavailable, first.Status);
        Assert.Equal("no session bus", first.Diagnostic);
        Assert.Equal(NotificationPublishStatus.Unavailable, second.Status);
        Assert.Equal(2, host.Presented.Count);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task AFailedHostIsReportedAndARetryIsNotSuppressed()
    {
        var host = new FakeNotificationHost
        {
            PresentHandler = (_, _) => throw new InvalidOperationException("backend crashed"),
        };
        var api = new NotificationsApi(host);

        var first = await api.PublishAsync(Message());
        var second = await api.PublishAsync(Message());

        Assert.Equal(NotificationPublishStatus.Failed, first.Status);
        Assert.Contains("backend crashed", first.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(NotificationPublishStatus.Failed, second.Status);
        Assert.Equal(2, host.Presented.Count);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task CancellationBeforeTheHostRunsThrowsAndTracksNothing()
    {
        var host = new FakeNotificationHost();
        var api = new NotificationsApi(host);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.PublishAsync(Message(), source.Token));

        Assert.Empty(host.Presented);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task CancellationDuringPresentationThrowsAndAllowsARetry()
    {
        using var source = new CancellationTokenSource();
        var host = new FakeNotificationHost
        {
            PresentHandler = async (_, token) =>
            {
                source.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new NotificationHostResult(NotificationHostStatus.Presented);
            },
        };
        var api = new NotificationsApi(host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.PublishAsync(Message(), source.Token));

        Assert.Empty(api.Messages);
        host.PresentHandler = (_, _) => Task.FromResult(new NotificationHostResult(NotificationHostStatus.Presented));
        var retry = await api.PublishAsync(Message());
        Assert.Equal(NotificationPublishStatus.Published, retry.Status);
        Assert.Single(api.Messages);
    }

    [Fact]
    public async Task InvalidMessagesAreRejectedExplicitly()
    {
        var api = new NotificationsApi();

        await Assert.ThrowsAsync<ArgumentNullException>(() => api.PublishAsync((NotificationMessage)null!));
        await Assert.ThrowsAsync<ArgumentException>(() => api.PublishAsync("", "text", NotificationType.Info));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => api.PublishAsync("id", "text", (NotificationType)99));
    }

    [Fact]
    public async Task DismissRemovesTheMessageAndAsksTheHostToDismissIt()
    {
        var host = new FakeNotificationHost();
        var api = new NotificationsApi(host);
        await api.PublishAsync(Message());

        var result = await api.DismissAsync("update");

        Assert.Equal(NotificationDismissStatus.Dismissed, result.Status);
        Assert.True(result.IsDismissed);
        Assert.Single(host.Dismissed);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task DismissingAnUnknownIdReportsNotFound()
    {
        var api = new NotificationsApi();

        var result = await api.DismissAsync("missing");

        Assert.Equal(NotificationDismissStatus.NotFound, result.Status);
        Assert.False(result.IsDismissed);
    }

    [Fact]
    public async Task DismissAllWithAMissingHostRemovesNothingBecauseNothingWasTracked()
    {
        var api = new NotificationsApi();

        var result = await api.DismissAllAsync();

        Assert.Equal(0, result.Dismissed);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task DismissReportsAHostFailureButKeepsTheLocalRemoval()
    {
        var host = new FakeNotificationHost
        {
            DismissHandler = (_, _) => throw new InvalidOperationException("cannot dismiss"),
        };
        var api = new NotificationsApi(host);
        await api.PublishAsync(Message());

        var result = await api.DismissAsync("update");

        Assert.Equal(NotificationDismissStatus.Dismissed, result.Status);
        Assert.Contains("cannot dismiss", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task DismissCancellationDuringTheHostCallThrowsAfterLocalRemoval()
    {
        using var source = new CancellationTokenSource();
        var host = new FakeNotificationHost
        {
            DismissHandler = async (_, token) =>
            {
                source.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new NotificationHostResult(NotificationHostStatus.Presented);
            },
        };
        var api = new NotificationsApi(host);
        await api.PublishAsync(Message());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.DismissAsync("update", source.Token));

        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task DismissAllRemovesEveryTrackedMessage()
    {
        var host = new FakeNotificationHost();
        var api = new NotificationsApi(host);
        await api.PublishAsync(Message("one"));
        await api.PublishAsync(Message("two"));

        var result = await api.DismissAllAsync();

        Assert.Equal(2, result.Dismissed);
        Assert.Null(result.Diagnostic);
        Assert.Empty(api.Messages);
        Assert.Equal(2, host.Dismissed.Count);
    }

    [Fact]
    public async Task DismissAllReportsHostFailuresButStillRemovesEveryMessage()
    {
        var host = new FakeNotificationHost
        {
            DismissHandler = (_, _) => throw new InvalidOperationException("cannot dismiss"),
        };
        var api = new NotificationsApi(host);
        await api.PublishAsync(Message("one"));

        var result = await api.DismissAllAsync();

        Assert.Equal(1, result.Dismissed);
        Assert.Contains("cannot dismiss", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(api.Messages);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("one", failure.Id);
        Assert.Equal(NotificationHostStatus.Failed, failure.Status);
        Assert.True(failure.Threw);
        Assert.Equal("cannot dismiss", failure.Diagnostic);
    }

    [Theory]
    [InlineData(NotificationHostStatus.Failed)]
    [InlineData(NotificationHostStatus.Unavailable)]
    public async Task DismissAllSurfacesAHostFailureThatDoesNotThrow(NotificationHostStatus status)
    {
        var host = new FakeNotificationHost
        {
            DismissHandler = (_, _) => Task.FromResult(new NotificationHostResult(status, "host says no")),
        };
        var api = new NotificationsApi(host);
        await api.PublishAsync(Message("one"));

        var result = await api.DismissAllAsync();

        Assert.Equal(1, result.Dismissed);
        Assert.True(result.HasFailures);
        Assert.Equal(1, result.FailedCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("one", failure.Id);
        Assert.Equal(status, failure.Status);
        Assert.False(failure.Threw);
        Assert.Equal("host says no", failure.Diagnostic);
        Assert.Contains("host says no", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task DismissAllReportsOnlyTheMessagesTheHostDidNotDismiss()
    {
        var host = new FakeNotificationHost
        {
            DismissHandler = (id, _) => Task.FromResult(id == "two"
                ? new NotificationHostResult(NotificationHostStatus.Unavailable, "no transport")
                : new NotificationHostResult(NotificationHostStatus.Presented)),
        };
        var api = new NotificationsApi(host);
        await api.PublishAsync(Message("one"));
        await api.PublishAsync(Message("two"));

        var result = await api.DismissAllAsync();

        Assert.Equal(2, result.Dismissed);
        Assert.Equal(1, result.FailedCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("two", failure.Id);
        Assert.Equal(NotificationHostStatus.Unavailable, failure.Status);
        Assert.Equal("no transport", failure.Diagnostic);
        Assert.DoesNotContain(result.Failures, item => item.Id == "one");
        Assert.Empty(api.Messages);
        Assert.Equal(new[] { "one", "two" }, host.Dismissed);
    }

    [Fact]
    public async Task DismissAllWithEveryHostSuccessReportsNoFailures()
    {
        var host = new FakeNotificationHost();
        var api = new NotificationsApi(host);
        await api.PublishAsync(Message("one"));
        await api.PublishAsync(Message("two"));

        var result = await api.DismissAllAsync();

        Assert.Equal(2, result.Dismissed);
        Assert.False(result.HasFailures);
        Assert.Equal(0, result.FailedCount);
        Assert.Empty(result.Failures);
        Assert.Null(result.Diagnostic);
        Assert.Empty(api.Messages);
        Assert.Equal(2, host.Dismissed.Count);
    }

    [Fact]
    public async Task ChangedIsRaisedAfterPublishAndDismissButNotForDuplicates()
    {
        var host = new FakeNotificationHost();
        var api = new NotificationsApi(host);
        var changes = new List<string>();
        api.Changed += (_, _) => changes.Add("changed");

        await api.PublishAsync(Message());
        await api.PublishAsync(Message());
        await api.DismissAsync("update");

        Assert.Equal(2, changes.Count);
    }
}
