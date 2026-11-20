namespace Playnite.SDK;

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
    // RS0026: both overloads are moved verbatim from the shipped p6-006 Core surface. Splitting
    // the optional token would break callers, so the deliberate overload pair stays.
#pragma warning disable RS0026
    Task<NotificationPublishResult> PublishAsync(NotificationMessage message, CancellationToken cancellationToken = default);

    /// <summary>Publishes a message from its parts.</summary>
    Task<NotificationPublishResult> PublishAsync(string id, string text, NotificationType type, CancellationToken cancellationToken = default);
#pragma warning restore RS0026

    /// <summary>Removes one tracked message and asks the host to dismiss it when possible.</summary>
    Task<NotificationDismissResult> DismissAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every tracked message and asks the host to dismiss each one. Every host outcome is
    /// aggregated: a message the host did not dismiss appears in
    /// <see cref="NotificationDismissAllResult.Failures"/> with its status and diagnostic.
    /// </summary>
    Task<NotificationDismissAllResult> DismissAllAsync(CancellationToken cancellationToken = default);
}
