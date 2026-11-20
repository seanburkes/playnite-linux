using Playnite.SDK;

namespace Playnite.Core.Hosting;

/// <summary>
/// Tracks notifications, deduplicates by id and delegates presentation to an optional host.
/// A missing host is a normal degraded mode; it never throws at construction or on a request.
/// </summary>
public sealed class NotificationsApi : INotificationsAPI
{
    /// <summary>Diagnostic recorded when no host was registered.</summary>
    public const string MissingHostDiagnostic =
        "No notification host is registered; the notification was not published.";

    /// <summary>Diagnostic recorded when a dismissal has no host to notify.</summary>
    public const string MissingHostDismissDiagnostic =
        "No notification host is registered; the tracked message was removed locally.";

    private readonly INotificationHost? host;
    private readonly object gate = new();
    private readonly List<NotificationMessage> messages = new();
    private readonly Dictionary<string, TaskCompletionSource<NotificationPublishResult>> inFlight =
        new(StringComparer.Ordinal);

    /// <summary>Creates the API over an optional host.</summary>
    public NotificationsApi(INotificationHost? host = null) => this.host = host;

    /// <inheritdoc />
    public IReadOnlyList<NotificationMessage> Messages
    {
        get
        {
            lock (gate)
            {
                return messages.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (gate)
            {
                return messages.Count;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public async Task<NotificationPublishResult> PublishAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<NotificationPublishResult>? shared = null;
        TaskCompletionSource<NotificationPublishResult> completion;
        lock (gate)
        {
            if (messages.Exists(item => string.Equals(item.Id, message.Id, StringComparison.Ordinal)))
            {
                return new NotificationPublishResult(
                    NotificationPublishStatus.Duplicate,
                    $"Notification '{message.Id}' is already published.");
            }

            if (inFlight.TryGetValue(message.Id, out var existing))
            {
                shared = existing;
                completion = existing;
            }
            else
            {
                completion = new TaskCompletionSource<NotificationPublishResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                inFlight[message.Id] = completion;
            }
        }

        if (shared is not null)
        {
            return await shared.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (host is null)
        {
            return Complete(
                message.Id,
                completion,
                new NotificationPublishResult(NotificationPublishStatus.Unavailable, MissingHostDiagnostic));
        }

        NotificationPublishResult published;
        try
        {
            var result = await host.PresentAsync(message, cancellationToken)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!result.WasPresented)
            {
                var degraded = result.Status == NotificationHostStatus.Unavailable
                    ? new NotificationPublishResult(
                        NotificationPublishStatus.Unavailable,
                        result.Diagnostic ?? "The notification host cannot present notifications in this session.")
                    : new NotificationPublishResult(
                        NotificationPublishStatus.Failed,
                        result.Diagnostic ?? "The notification host failed to present the notification.");
                return Complete(message.Id, completion, degraded);
            }

            lock (gate)
            {
                inFlight.Remove(message.Id);
                messages.Add(message);
            }

            published = new NotificationPublishResult(NotificationPublishStatus.Published, result.Diagnostic);
            completion.TrySetResult(published);
        }
        catch (OperationCanceledException)
        {
            lock (gate)
            {
                inFlight.Remove(message.Id);
            }

            completion.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            return Complete(
                message.Id,
                completion,
                new NotificationPublishResult(
                    NotificationPublishStatus.Failed,
                    "The notification host failed: " + exception.Message));
        }

        // Subscriber failures are caller bugs, not host results; never fold them into the outcome.
        RaiseChanged();
        return published;
    }

    /// <inheritdoc />
    public Task<NotificationPublishResult> PublishAsync(
        string id,
        string text,
        NotificationType type,
        CancellationToken cancellationToken = default)
        => PublishAsync(new NotificationMessage(id, text, type), cancellationToken);

    /// <inheritdoc />
    public async Task<NotificationDismissResult> DismissAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        bool removed;
        lock (gate)
        {
            removed = messages.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
        }

        if (!removed)
        {
            return new NotificationDismissResult(
                NotificationDismissStatus.NotFound,
                $"No notification with id '{id}' is tracked.");
        }

        RaiseChanged();
        if (host is null)
        {
            return new NotificationDismissResult(NotificationDismissStatus.Dismissed, MissingHostDismissDiagnostic);
        }

        try
        {
            var result = await host.DismissAsync(id, cancellationToken).ConfigureAwait(false);
            if (result.WasPresented)
            {
                return new NotificationDismissResult(NotificationDismissStatus.Dismissed, result.Diagnostic);
            }

            return new NotificationDismissResult(
                NotificationDismissStatus.Dismissed,
                "Removed locally; " + (result.Diagnostic ?? "the host did not dismiss it."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new NotificationDismissResult(
                NotificationDismissStatus.Dismissed,
                "Removed locally; the notification host failed to dismiss it: " + exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task<NotificationDismissAllResult> DismissAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string[] ids;
        lock (gate)
        {
            ids = messages.Select(item => item.Id).ToArray();
            messages.Clear();
        }

        if (ids.Length == 0)
        {
            return new NotificationDismissAllResult(0);
        }

        RaiseChanged();
        if (host is null)
        {
            return new NotificationDismissAllResult(ids.Length, MissingHostDismissDiagnostic);
        }

        var failures = new List<NotificationDismissFailure>();
        var diagnostics = new List<string>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NotificationHostResult result;
            try
            {
                result = await host.DismissAsync(id, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    failures.Add(new NotificationDismissFailure(
                        id,
                        NotificationHostStatus.Failed,
                        "The notification host returned no dismissal result.",
                        Threw: false));
                    diagnostics.Add($"{id}: the notification host returned no dismissal result.");
                    continue;
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A host that cancels its own work is a host failure; only the caller's token aborts.
                failures.Add(new NotificationDismissFailure(
                    id,
                    NotificationHostStatus.Failed,
                    exception.Message,
                    Threw: true));
                diagnostics.Add($"{id}: {exception.Message}");
                continue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(new NotificationDismissFailure(
                    id,
                    NotificationHostStatus.Failed,
                    exception.Message,
                    Threw: true));
                diagnostics.Add($"{id}: {exception.Message}");
                continue;
            }

            if (result.WasPresented)
            {
                continue;
            }

            var diagnostic = result.Diagnostic is null
                ? $"the host did not dismiss it ({result.Status})"
                : $"the host did not dismiss it ({result.Status}): {result.Diagnostic}";
            failures.Add(new NotificationDismissFailure(id, result.Status, result.Diagnostic));
            diagnostics.Add($"{id}: {diagnostic}");
        }

        return new NotificationDismissAllResult(
            ids.Length,
            failures.Count == 0 ? null : "Some host dismissals failed: " + string.Join("; ", diagnostics))
        {
            Failures = failures,
        };
    }

    private NotificationPublishResult Complete(
        string id,
        TaskCompletionSource<NotificationPublishResult> completion,
        NotificationPublishResult result)
    {
        lock (gate)
        {
            inFlight.Remove(id);
        }

        completion.TrySetResult(result);
        return result;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
