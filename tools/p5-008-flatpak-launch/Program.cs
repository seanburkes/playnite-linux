using System.Text.Json;
using Playnite.Platform.Linux;
using Playnite.Platform.Sessions;
using Playnite.Steam;

namespace Playnite.Steam.Launch;

/// <summary>
/// Packaged launch entry. Inside Flatpak it opens <c>steam://rungameid/&lt;appid&gt;</c> through the
/// desktop OpenURI portal and reports tracking UNKNOWN. Every other format keeps the existing
/// tracked acceptance path, so this command deliberately does not invent a second tracked launch.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: playnite steam-launch <decimal-appid>");
            return 2;
        }

        if (!FlatpakSteamLauncher.TryParseAppId(args[0], out var appId))
        {
            Console.Error.WriteLine("The app id must be a decimal uint without a sign, whitespace, prefix or fraction.");
            return 2;
        }

        var mode = SteamLaunchModes.Select(new LinuxSteamHostFormat());
        if (mode != SteamLaunchMode.FlatpakLaunchOnly)
        {
            Console.Error.WriteLine("This host is not a Flatpak sandbox. The native/AppImage launch-plus-tracking path is unchanged; use the tracked acceptance entry instead of inventing an untracked launch.");
            return 2;
        }

        var launcher = new FlatpakSteamLauncher(new HostUriOpener());
        var result = await launcher.LaunchAsync(appId, CancellationToken.None);
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "flatpak-launch-only",
            status = result.Status.ToString(),
            appId = result.AppId,
            uri = result.Uri,
            tracking = result.Tracking == SteamLaunchTracking.Unknown ? "unknown" : "tracked",
            stop = "manual-steam",
            detail = result.Detail,
        }, Json));
        return result.Status == FlatpakLaunchStatus.RequestAccepted ? 0 : 1;
    }

    /// <summary>The reviewed argv-only host runner. It opens one URI and never signals.</summary>
    private sealed class HostUriOpener : ISteamUriOpener
    {
        private readonly LinuxExternalProcessHost host = new();

        public Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken) =>
            host.RunAsync(request, cancellationToken);
    }
}
