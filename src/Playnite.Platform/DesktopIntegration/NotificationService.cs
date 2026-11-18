namespace Playnite.Platform.DesktopIntegration;

public enum NotificationUrgency
{
    Low,
    Normal,
    Critical
}

public sealed record NotificationRequest(
    string Id,
    string Title,
    string Body,
    NotificationUrgency Urgency = NotificationUrgency.Normal,
    TimeSpan? Expiry = null);

public enum NotificationDeliveryState
{
    Delivered,
    SuppressedDuplicate,
    Unavailable
}

public sealed record NotificationDelivery(
    NotificationDeliveryState State,
    string Mechanism,
    string Detail)
{
    public bool WasDelivered => State == NotificationDeliveryState.Delivered;
}

public sealed record NotificationBackendResult(bool Sent, string Detail);

/// <summary>
/// One transport to the desktop notification service. A backend reports availability before the
/// service tries it, and a failed send degrades to the next backend.
/// </summary>
public interface IDesktopNotificationBackend
{
    string Mechanism { get; }

    bool IsAvailable { get; }

    Task<NotificationBackendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default);
}

public interface INotificationService
{
    Task<NotificationDelivery> ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default);
}
