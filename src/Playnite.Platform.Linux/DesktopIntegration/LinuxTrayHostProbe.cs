using Playnite.Platform.DesktopIntegration;

namespace Playnite.Platform.Linux.DesktopIntegration;

/// <summary>
/// Probes for a StatusNotifierItem host (org.kde.StatusNotifierWatcher). gdbus, busctl and dbus-send
/// are alternatives for the same session-bus query; the first installed tool that answers decides.
/// </summary>
public sealed class LinuxTrayHostProbe : ITrayHostProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly TrayProbe[] Probes =
    [
        new(
            "gdbus",
            [
                "call",
                "--session",
                "--dest", "org.freedesktop.DBus",
                "--object-path", "/org/freedesktop/DBus",
                "--method", "org.freedesktop.DBus.NameHasOwner",
                "org.kde.StatusNotifierWatcher"
            ]),
        new(
            "busctl",
            [
                "--user",
                "call",
                "org.freedesktop.DBus",
                "/org/freedesktop/DBus",
                "org.freedesktop.DBus",
                "NameHasOwner",
                "s",
                "org.kde.StatusNotifierWatcher"
            ]),
        new(
            "dbus-send",
            [
                "--session",
                "--print-reply",
                "--dest=org.freedesktop.DBus",
                "/org/freedesktop/DBus",
                "org.freedesktop.DBus.NameHasOwner",
                "string:org.kde.StatusNotifierWatcher"
            ])
    ];

    private readonly IDesktopSessionProbe sessionProbe;
    private readonly IExternalCommandRunner runner;
    private readonly IExecutableLocator locator;

    public LinuxTrayHostProbe(
        IDesktopSessionProbe sessionProbe,
        IExternalCommandRunner runner,
        IExecutableLocator locator)
    {
        this.sessionProbe = sessionProbe ?? throw new ArgumentNullException(nameof(sessionProbe));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    public async Task<TrayHostStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var session = sessionProbe.Probe();
        if (!session.IsGraphical)
        {
            return new TrayHostStatus(false, $"no graphical session ({session.Evidence})");
        }

        var failures = new List<string>();
        foreach (var probe in Probes)
        {
            if (!locator.Exists(probe.Tool))
            {
                failures.Add($"{probe.Tool}: not installed");
                continue;
            }

            var result = await runner
                .RunAsync(new ExternalCommand(probe.Tool, probe.Arguments, ProbeTimeout), cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                failures.Add($"{probe.Tool}: {CommandDiagnostics.Summarize(result)}");
                continue;
            }

            if (result.StandardOutput.Contains("true", StringComparison.Ordinal))
            {
                return new TrayHostStatus(
                    true,
                    $"{probe.Tool}: org.kde.StatusNotifierWatcher is owned");
            }

            if (result.StandardOutput.Contains("false", StringComparison.Ordinal))
            {
                return new TrayHostStatus(
                    false,
                    $"{probe.Tool}: no org.kde.StatusNotifierWatcher. {SessionHint(session)}");
            }

            failures.Add($"{probe.Tool}: unrecognized NameHasOwner output");
        }

        return new TrayHostStatus(
            false,
            $"no D-Bus probe succeeded ({string.Join("; ", failures)}). {SessionHint(session)}");
    }

    private static string SessionHint(DesktopSession session) =>
        session.Kind == DesktopSessionKind.Wayland
            ? "Wayland has no XEmbed tray; a StatusNotifierItem host is required."
            : "X11 XEmbed trays are not supported in v1; a StatusNotifierItem host is required.";

    private sealed record TrayProbe(string Tool, IReadOnlyList<string> Arguments);
}
