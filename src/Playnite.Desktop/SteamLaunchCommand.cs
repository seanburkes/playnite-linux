using System.Diagnostics;
using System.Text.Json;
using Playnite.Platform.Sessions;
using Playnite.Steam;

namespace Playnite.Desktop;

/// <summary>
/// Packaged <c>playnite steam-launch &lt;appid&gt;</c> entry. Inside Flatpak it opens the Steam
/// URI through the desktop OpenURI portal and reports tracking UNKNOWN. On native/AppImage it
/// refuses, so the existing launch-plus-tracking path is not replaced by an untracked launch.
/// </summary>
internal static class SteamLaunchCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "steam-launch", StringComparison.Ordinal);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: playnite steam-launch <decimal-appid>");
            return 2;
        }

        if (!FlatpakSteamLauncher.TryParseAppId(args[1], out var appId))
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

        var launcher = new FlatpakSteamLauncher(new ProcessUriOpener());
        var result = await launcher.LaunchAsync(appId, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>The packaged argv-only OpenURI request. It opens one URI and never signals.</summary>
    private sealed class ProcessUriOpener : ISteamUriOpener
    {
        public async Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(request.Executable) { UseShellExecute = false };
            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (request.WorkingDirectory is not null)
            {
                startInfo.WorkingDirectory = request.WorkingDirectory;
            }

            Process process;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new IOException("The OpenURI opener did not start.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The exec boundary is the only place a missing or non-executable opener can
                // surface; report it as an unavailable request, never as a launch.
                throw new IOException("The OpenURI opener could not be started.", exception);
            }

            using (process)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return process.ExitCode;
            }
        }
    }
}
