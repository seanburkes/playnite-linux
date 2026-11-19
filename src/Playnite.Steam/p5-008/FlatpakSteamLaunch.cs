using System.Globalization;
using Playnite.Platform.Sessions;

namespace Playnite.Steam;

/// <summary>How much of a Steam session this host format can honestly observe.</summary>
public enum SteamLaunchTracking
{
    /// <summary>Native/AppImage: the same-namespace adapter attributes the game and tracks its exit.</summary>
    Tracked,

    /// <summary>Flatpak: only the OpenURI request is observable; start, exit and playtime are unknown.</summary>
    Unknown,
}

/// <summary>The selected launch contract for one host format.</summary>
public enum SteamLaunchMode
{
    /// <summary>The existing launch-plus-tracking path is unchanged.</summary>
    Tracked,

    /// <summary>Open the Steam URI through the desktop portal; never track or signal.</summary>
    FlatpakLaunchOnly,
}

/// <summary>Host-format probe seam. Production reads the Flatpak marker; tests supply a path or fake.</summary>
public interface ISteamHostFormat
{
    bool IsFlatpak { get; }
}

/// <summary>
/// The real Flatpak probe: <c>/.flatpak-info</c> exists only inside a Flatpak sandbox.
/// When it is absent the host is native or AppImage and keeps the tracked path.
/// </summary>
public sealed class LinuxSteamHostFormat(string markerPath = "/.flatpak-info") : ISteamHostFormat
{
    public bool IsFlatpak => File.Exists(markerPath);
}

public static class SteamLaunchModes
{
    public static SteamLaunchMode Select(ISteamHostFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format.IsFlatpak ? SteamLaunchMode.FlatpakLaunchOnly : SteamLaunchMode.Tracked;
    }
}

/// <summary>The only execution surface launch-only mode has: open a URI. It cannot signal.</summary>
public interface ISteamUriOpener
{
    Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken);
}

public enum FlatpakLaunchStatus
{
    /// <summary>The opener accepted the request. The game start itself is unobserved.</summary>
    RequestAccepted,

    /// <summary>The opener ran but rejected the request; no launch is claimed.</summary>
    Rejected,

    /// <summary>The opener could not be started; no launch is claimed.</summary>
    Unavailable,
}

/// <summary>
/// An honest Flatpak launch result. There is deliberately no playtime, stop or signal surface:
/// Steam owns the process, Playnite cannot see host PIDs, and the owner stops the game in Steam.
/// </summary>
public sealed record FlatpakLaunchResult(FlatpakLaunchStatus Status, uint AppId, string Uri, string Detail)
{
    public SteamLaunchTracking Tracking => SteamLaunchTracking.Unknown;
}

/// <summary>
/// Launch-only Steam integration for the Flatpak sandbox. It builds
/// <c>steam://rungameid/&lt;appid&gt;</c> from a decimal <see cref="uint"/> and asks the desktop
/// OpenURI portal through the sandbox's <c>xdg-open</c>. It never snapshots, attributes, signals
/// or measures a game process, so it cannot claim a tracked start, stop or playtime.
/// </summary>
public sealed class FlatpakSteamLauncher
{
    public const string DefaultOpenerPath = "/usr/bin/xdg-open";

    private readonly ISteamUriOpener opener;
    private readonly string openerPath;

    public FlatpakSteamLauncher(ISteamUriOpener opener, string openerPath = DefaultOpenerPath)
    {
        this.opener = opener ?? throw new ArgumentNullException(nameof(opener));
        if (string.IsNullOrEmpty(openerPath) || !Path.IsPathFullyQualified(openerPath)
            || openerPath.Contains('\0'))
        {
            throw new ArgumentException("The OpenURI opener must be an absolute path.", nameof(openerPath));
        }

        this.openerPath = openerPath;
    }

    /// <summary>Strict decimal <see cref="uint"/>; signs, whitespace, hex, exponents and overflow are rejected.</summary>
    public static bool TryParseAppId(string? text, out uint appId) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out appId);

    public static string BuildUri(uint appId) =>
        "steam://rungameid/" + appId.ToString(CultureInfo.InvariantCulture);

    /// <summary>One absolute opener and one URI argument. No shell parses or expands the request.</summary>
    public LaunchRequest CreateRequest(uint appId) => new(openerPath, [BuildUri(appId)]);

    /// <summary>
    /// Ask the portal to open the game. A zero opener exit means the request was accepted, not
    /// that the game started. Every outcome reports tracking as <see cref="SteamLaunchTracking.Unknown"/>.
    /// </summary>
    public async Task<FlatpakLaunchResult> LaunchAsync(uint appId, CancellationToken cancellationToken)
    {
        var request = CreateRequest(appId);
        var uri = request.Arguments[0];
        int exit;
        try
        {
            exit = await opener.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException)
        {
            return new(FlatpakLaunchStatus.Unavailable, appId, uri,
                $"The OpenURI opener could not be started ({exception.GetType().Name}); no launch is claimed and tracking is UNKNOWN. Start the game in Steam manually.");
        }

        return exit == 0
            ? new(FlatpakLaunchStatus.RequestAccepted, appId, uri,
                "The portal accepted the OpenURI request. Game start and playtime are UNKNOWN; Playnite does not track this Flatpak session. Stop the game manually in Steam.")
            : new(FlatpakLaunchStatus.Rejected, appId, uri,
                $"The OpenURI opener exited {exit.ToString(CultureInfo.InvariantCulture)}; no launch is claimed and tracking is UNKNOWN. Start the game in Steam manually.");
    }
}
