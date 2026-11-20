using Playnite.Core.Hosting;
using Playnite.SDK;

namespace Playnite.Platform.DesktopIntegration;

/// <summary>
/// Adapts the p5-007a desktop notification service to the host-facing
/// <see cref="INotificationHost"/> seam. The service owns transport fallback and its own
/// duplicate window; this adapter only maps message and delivery types.
/// </summary>
public sealed class NotificationServiceHost : INotificationHost
{
    private const string ApplicationName = "Playnite";

    private readonly INotificationService service;

    /// <summary>Creates the adapter over a desktop notification service.</summary>
    public NotificationServiceHost(INotificationService service)
        => this.service = service ?? throw new ArgumentNullException(nameof(service));

    /// <inheritdoc />
    public async Task<NotificationHostResult> PresentAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var request = new NotificationRequest(
            message.Id,
            ApplicationName,
            message.Text,
            message.Type == NotificationType.Error ? NotificationUrgency.Critical : NotificationUrgency.Normal);

        var delivery = await service.ShowAsync(request, cancellationToken).ConfigureAwait(false);
        return delivery.State switch
        {
            NotificationDeliveryState.Delivered =>
                new NotificationHostResult(NotificationHostStatus.Presented, $"delivered via {delivery.Mechanism}"),
            NotificationDeliveryState.SuppressedDuplicate =>
                new NotificationHostResult(
                    NotificationHostStatus.Presented,
                    $"transport-level duplicate suppressed via {delivery.Mechanism}"),
            _ => new NotificationHostResult(NotificationHostStatus.Unavailable, delivery.Detail),
        };
    }

    /// <inheritdoc />
    public Task<NotificationHostResult> DismissAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new NotificationHostResult(
            NotificationHostStatus.Unavailable,
            "The desktop notification service does not expose dismissal."));
    }
}
