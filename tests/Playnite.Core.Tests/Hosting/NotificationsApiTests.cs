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
    public async Task ADuplicateJoinerCanCancelItsOwnWaitWithoutCancellingTheOwner()
    {
        var release = new TaskCompletionSource<NotificationHostResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeNotificationHost { PresentHandler = (_, _) => release.Task };
        var api = new NotificationsApi(host);
        using var source = new CancellationTokenSource();

        var owner = api.PublishAsync(Message("update"));
        var joiner = api.PublishAsync(Message("update"), source.Token);
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner);
        release.SetResult(new NotificationHostResult(NotificationHostStatus.Presented));
        var ownerResult = await owner;

        Assert.Equal(NotificationPublishStatus.Published, ownerResult.Status);
        Assert.Single(api.Messages);
    }

    [Fact]
    public async Task AThrowingChangedSubscriberIsNotMisreportedAsAHostFailure()
    {
        var host = new FakeNotificationHost();
        var api = new NotificationsApi(host);
        api.Changed += (_, _) => throw new InvalidOperationException("subscriber failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.PublishAsync(Message()));

        Assert.Single(api.Messages);
        var retry = await api.PublishAsync(Message());
        Assert.Equal(NotificationPublishStatus.Duplicate, retry.Status);
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
    public async Task AHostThatIgnoresCancellationDoesNotPoisonTheId()
    {
        using var source = new CancellationTokenSource();
        var never = new TaskCompletionSource<NotificationHostResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var host = new FakeNotificationHost
        {
            PresentHandler = (_, _) =>
            {
                calls++;
                return never.Task;
            },
        };
        var api = new NotificationsApi(host);

        var first = api.PublishAsync(Message("stuck"), source.Token);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        host.PresentHandler = (_, _) =>
        {
            calls++;
            return Task.FromResult(new NotificationHostResult(NotificationHostStatus.Presented));
        };
        var retry = await api.PublishAsync(Message("stuck"));

        Assert.Equal(NotificationPublishStatus.Published, retry.Status);
        Assert.Equal(2, calls);
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
    }

    [Fact]
    public async Task DismissAllReportsAHostThatCannotDismissWithoutThrowing()
    {
        var host = new FakeNotificationHost
        {
            DismissHandler = (_, _) => Task.FromResult(
                new NotificationHostResult(NotificationHostStatus.Unavailable, "no dismiss surface")),
        };
        var api = new NotificationsApi(host);
        await api.PublishAsync(Message("one"));

        var result = await api.DismissAllAsync();

        Assert.Equal(1, result.Dismissed);
        Assert.Contains("no dismiss surface", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task DismissAllCancellationThrowsAfterClearingTheLocalList()
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
        await api.PublishAsync(Message("one"));
        await api.PublishAsync(Message("two"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.DismissAllAsync(source.Token));

        Assert.Empty(api.Messages);
    }

    [Fact]
    public async Task DismissDuringAnInFlightPublishReportsNotFoundAndThePublishStillCompletes()
    {
        var release = new TaskCompletionSource<NotificationHostResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeNotificationHost { PresentHandler = (_, _) => release.Task };
        var api = new NotificationsApi(host);

        var pending = api.PublishAsync(Message("late"));
        var dismiss = await api.DismissAsync("late");
        release.SetResult(new NotificationHostResult(NotificationHostStatus.Presented));
        var published = await pending;

        Assert.Equal(NotificationDismissStatus.NotFound, dismiss.Status);
        Assert.Equal(NotificationPublishStatus.Published, published.Status);
        Assert.Single(api.Messages);
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
