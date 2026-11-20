namespace Playnite.Core.Hosting;

/// <summary>Notification severity. Names and values follow the v10 SDK contract.</summary>
public enum NotificationType
{
    /// <summary>Informational notification.</summary>
    Info = 0,

    /// <summary>Error notification.</summary>
    Error = 1,
}

/// <summary>A notification message whose stable identity is <see cref="Id"/>.</summary>
public sealed record NotificationMessage
{
    /// <summary>Creates a validated message.</summary>
    public NotificationMessage(string id, string text, NotificationType type = NotificationType.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown notification type.");
        }

        Id = id;
        Text = text;
        Type = type;
    }

    /// <summary>Stable identity used for deduplication and dismissal.</summary>
    public string Id { get; }

    /// <summary>Notification text shown to the user.</summary>
    public string Text { get; }

    /// <summary>Severity.</summary>
    public NotificationType Type { get; }
}

/// <summary>How the host handled a presentation request.</summary>
public enum NotificationHostStatus
{
    /// <summary>The host presented the notification (or suppressed it as a transport duplicate).</summary>
    Presented,

    /// <summary>The host cannot present notifications in this session.</summary>
    Unavailable,

    /// <summary>The host was asked to present and failed.</summary>
    Failed,
}

/// <summary>The host's answer to a notification request.</summary>
public sealed record NotificationHostResult(NotificationHostStatus Status, string? Diagnostic = null)
{
    /// <summary>True when the notification reached the host.</summary>
    public bool WasPresented => Status == NotificationHostStatus.Presented;
}

/// <summary>
/// The presentation seam supplied by the UI or CLI. A headless CLI can leave it unregistered;
/// the API then reports <see cref="NotificationPublishStatus.Unavailable"/> with a diagnostic.
/// </summary>
public interface INotificationHost
{
    /// <summary>Presents one notification. Cancellation is observed, never swallowed.</summary>
    Task<NotificationHostResult> PresentAsync(NotificationMessage message, CancellationToken cancellationToken = default);

    /// <summary>Dismisses a notification the host may still be showing.</summary>
    Task<NotificationHostResult> DismissAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of publishing a notification.</summary>
public enum NotificationPublishStatus
{
    /// <summary>The host accepted the notification and it is tracked in the message list.</summary>
    Published,

    /// <summary>A notification with the same id is already tracked; v10 suppresses the repeat.</summary>
    Duplicate,

    /// <summary>No host is registered or the host cannot present notifications here.</summary>
    Unavailable,

    /// <summary>The host was asked to present the notification and failed.</summary>
    Failed,
}

/// <summary>Result of one publish request.</summary>
public sealed record NotificationPublishResult(NotificationPublishStatus Status, string? Diagnostic = null)
{
    /// <summary>True for Published and Duplicate; both leave the tracked list in a consistent state.</summary>
    public bool IsSuccess => Status is NotificationPublishStatus.Published or NotificationPublishStatus.Duplicate;
}

/// <summary>Outcome of dismissing one notification.</summary>
public enum NotificationDismissStatus
{
    /// <summary>The message was removed from the tracked list.</summary>
    Dismissed,

    /// <summary>No tracked message has that id.</summary>
    NotFound,
}

/// <summary>Result of one dismiss request.</summary>
public sealed record NotificationDismissResult(NotificationDismissStatus Status, string? Diagnostic = null)
{
    /// <summary>True when a message was removed.</summary>
    public bool IsDismissed => Status == NotificationDismissStatus.Dismissed;
}

/// <summary>One tracked notification whose host dismissal did not succeed.</summary>
/// <param name="Id">Id of the message that was removed locally but not dismissed by the host.</param>
/// <param name="Status">The status the host reported, or <see cref="NotificationHostStatus.Failed"/> when it threw.</param>
/// <param name="Diagnostic">The host's diagnostic, or the exception message when the host threw.</param>
/// <param name="Threw">True when the host threw instead of returning a result.</param>
public sealed record NotificationDismissFailure(
    string Id,
    NotificationHostStatus Status,
    string? Diagnostic = null,
    bool Threw = false);

/// <summary>
/// Result of dismissing every tracked notification. <see cref="Dismissed"/> counts the messages
/// removed from the tracked list; <see cref="Failures"/> names every one the host did not dismiss,
/// whether it returned an unsuccessful result or threw.
/// </summary>
public sealed record NotificationDismissAllResult(int Dismissed, string? Diagnostic = null)
{
    /// <summary>One entry per tracked message whose host dismissal did not succeed.</summary>
    public IReadOnlyList<NotificationDismissFailure> Failures { get; init; } =
        Array.Empty<NotificationDismissFailure>();

    /// <summary>Number of messages removed locally that the host did not dismiss.</summary>
    public int FailedCount => Failures.Count;

    /// <summary>True when at least one tracked message was not dismissed by the host.</summary>
    public bool HasFailures => Failures.Count > 0;
}

/// <summary>
/// The host-facing notification surface: publish, deduplicate by id and dismiss. The v10 API
/// added and removed in-memory messages directly; this headless layer also asks an injectable
/// host to present them.
/// </summary>
public interface INotificationsAPI
{
    /// <summary>Current messages in publication order. The returned list is a snapshot.</summary>
    IReadOnlyList<NotificationMessage> Messages { get; }

    /// <summary>Number of tracked messages.</summary>
    int Count { get; }

    /// <summary>Raised after a message is added or removed.</summary>
    event EventHandler? Changed;

    /// <summary>Publishes a message, suppressing a repeat of an already tracked id.</summary>
    Task<NotificationPublishResult> PublishAsync(NotificationMessage message, CancellationToken cancellationToken = default);

    /// <summary>Publishes a message from its parts.</summary>
    Task<NotificationPublishResult> PublishAsync(string id, string text, NotificationType type, CancellationToken cancellationToken = default);

    /// <summary>Removes one tracked message and asks the host to dismiss it when possible.</summary>
    Task<NotificationDismissResult> DismissAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every tracked message and asks the host to dismiss each one. Every host outcome is
    /// aggregated: a message the host did not dismiss appears in
    /// <see cref="NotificationDismissAllResult.Failures"/> with its status and diagnostic.
    /// </summary>
    Task<NotificationDismissAllResult> DismissAllAsync(CancellationToken cancellationToken = default);
}

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
