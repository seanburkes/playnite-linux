using System.Globalization;
using Playnite.Platform.DesktopIntegration;

namespace Playnite.Platform.Linux.DesktopIntegration;

/// <summary>
/// Delivers through the first available backend and suppresses an identical request inside the
/// duplicate window. Delivery is asynchronous: no caller thread waits on the external process.
/// </summary>
public sealed class LinuxNotificationService : INotificationService
{
    public static readonly TimeSpan DefaultDuplicateWindow = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<IDesktopNotificationBackend> backends;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan duplicateWindow;
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> lastDelivered = new(StringComparer.Ordinal);

    public LinuxNotificationService(
        IEnumerable<IDesktopNotificationBackend> backends,
        TimeProvider? timeProvider = null,
        TimeSpan? duplicateWindow = null)
    {
        this.backends = (backends ?? throw new ArgumentNullException(nameof(backends))).ToArray();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.duplicateWindow = duplicateWindow ?? DefaultDuplicateWindow;
        if (this.duplicateWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duplicateWindow), "The duplicate window cannot be negative.");
        }
    }

    /// <summary>notify-send first, then a direct org.freedesktop.Notifications call through gdbus.</summary>
    public static LinuxNotificationService CreateDefault(
        IExternalCommandRunner? runner = null,
        IExecutableLocator? locator = null,
        TimeProvider? timeProvider = null)
    {
        var commandRunner = runner ?? new LinuxExternalCommandRunner();
        var executables = locator ?? new LinuxExecutableLocator();
        return new LinuxNotificationService(
            [
                new NotifySendBackend(commandRunner, executables),
                new GdbusNotificationBackend(commandRunner, executables)
            ],
            timeProvider);
    }

    public async Task<NotificationDelivery> ShowAsync(
        NotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var key = DeduplicationKey(request);
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            Prune(now);
            if (lastDelivered.TryGetValue(key, out var previous) && now - previous < duplicateWindow)
            {
                return new NotificationDelivery(
                    NotificationDeliveryState.SuppressedDuplicate,
                    "deduplication",
                    "suppressed a repeat within " +
                    duplicateWindow.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) +
                    "s");
            }

            lastDelivered[key] = now;
        }

        var failures = new List<string>();
        foreach (var backend in backends)
        {
            if (!backend.IsAvailable)
            {
                failures.Add($"{backend.Mechanism}: not available");
                continue;
            }

            try
            {
                var result = await backend.SendAsync(request, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Sent)
                {
                    return new NotificationDelivery(
                        NotificationDeliveryState.Delivered,
                        backend.Mechanism,
                        result.Detail);
                }

                failures.Add($"{backend.Mechanism}: {result.Detail}");
            }
            catch (OperationCanceledException)
            {
                Forget(key);
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"{backend.Mechanism}: {exception.Message}");
            }
        }

        Forget(key);
        return new NotificationDelivery(
            NotificationDeliveryState.Unavailable,
            "none",
            failures.Count == 0
                ? "no notification backend is configured"
                : string.Join("; ", failures));
    }

    private void Forget(string key)
    {
        lock (gate)
        {
            lastDelivered.Remove(key);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (lastDelivered.Count == 0)
        {
            return;
        }

        var expired = lastDelivered
            .Where(item => item.Value > now || now - item.Value >= duplicateWindow)
            .Select(item => item.Key)
            .ToArray();
        foreach (var key in expired)
        {
            lastDelivered.Remove(key);
        }
    }

    private static string DeduplicationKey(NotificationRequest request) => string.Join(
        '\u001f',
        request.Id,
        request.Title,
        request.Body,
        request.Urgency.ToString(),
        request.Expiry?.Ticks.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
}
