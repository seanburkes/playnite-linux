using System.Globalization;
using System.Text;
using Playnite.Platform.DesktopIntegration;

namespace Playnite.Platform.Linux.DesktopIntegration;

/// <summary>The standard libnotify client for org.freedesktop.Notifications.</summary>
public sealed class NotifySendBackend : IDesktopNotificationBackend
{
    public const string ToolName = "notify-send";

    private readonly IExternalCommandRunner runner;
    private readonly IExecutableLocator locator;
    private readonly TimeSpan timeout;

    public NotifySendBackend(
        IExternalCommandRunner runner,
        IExecutableLocator locator,
        TimeSpan? timeout = null)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public string Mechanism => ToolName;

    public bool IsAvailable => locator.Exists(ToolName);

    public async Task<NotificationBackendResult> SendAsync(
        NotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new List<string>
        {
            "--app-name=Playnite",
            "--urgency=" + UrgencyName(request.Urgency)
        };

        if (request.Expiry is { } expiry)
        {
            var milliseconds = Math.Clamp((long)expiry.TotalMilliseconds, 0, int.MaxValue);
            arguments.Add("--expire-time=" + milliseconds.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("--");
        arguments.Add(request.Title);
        arguments.Add(request.Body);

        var result = await runner
            .RunAsync(new ExternalCommand(ToolName, arguments, timeout), cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded
            ? new NotificationBackendResult(true, "notify-send accepted the notification")
            : new NotificationBackendResult(false, CommandDiagnostics.Summarize(result));
    }

    private static string UrgencyName(NotificationUrgency urgency) => urgency switch
    {
        NotificationUrgency.Low => "low",
        NotificationUrgency.Critical => "critical",
        _ => "normal"
    };
}

/// <summary>
/// Direct org.freedesktop.Notifications.Notify call for systems without libnotify's notify-send.
/// Arguments are GVariant text, so every string is quoted and escaped by <see cref="GdbusText"/>.
/// </summary>
public sealed class GdbusNotificationBackend : IDesktopNotificationBackend
{
    public const string ToolName = "gdbus";

    private readonly IExternalCommandRunner runner;
    private readonly IExecutableLocator locator;
    private readonly TimeSpan timeout;

    public GdbusNotificationBackend(
        IExternalCommandRunner runner,
        IExecutableLocator locator,
        TimeSpan? timeout = null)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public string Mechanism => "gdbus (org.freedesktop.Notifications)";

    public bool IsAvailable => locator.Exists(ToolName);

    public async Task<NotificationBackendResult> SendAsync(
        NotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expiry = request.Expiry is { } value
            ? Math.Clamp((long)value.TotalMilliseconds, -1, int.MaxValue)
            : -1;
        var arguments = new List<string>
        {
            "call",
            "--session",
            "--dest", "org.freedesktop.Notifications",
            "--object-path", "/org/freedesktop/Notifications",
            "--method", "org.freedesktop.Notifications.Notify",
            GdbusText.Quote("Playnite"),
            "0",
            GdbusText.Quote(""),
            GdbusText.Quote(request.Title),
            GdbusText.Quote(request.Body),
            "@as []",
            "@a{sv} {}",
            expiry.ToString(CultureInfo.InvariantCulture)
        };

        var result = await runner
            .RunAsync(new ExternalCommand(ToolName, arguments, timeout), cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded
            ? new NotificationBackendResult(true, "org.freedesktop.Notifications accepted the notification")
            : new NotificationBackendResult(false, CommandDiagnostics.Summarize(result));
    }
}

public static class GdbusText
{
    /// <summary>Quotes one GVariant text string; control characters use GVariant escapes.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\'':
                    builder.Append("\\'");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }
}
