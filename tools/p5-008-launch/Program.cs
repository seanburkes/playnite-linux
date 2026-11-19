using System.Diagnostics;
using System.Text.Json;
using Playnite.Platform.Linux;
using Playnite.Platform.Sessions;
using Playnite.Steam;

namespace Playnite.Launch;

/// <summary>
/// Shipped <c>playnite launch &lt;appid&gt;</c> surface. Flatpak selects the OpenURI launch-only
/// mode and reports tracking UNKNOWN; native/AppImage keep the existing tracked launcher with a
/// minimal console reporter. The Flatpak path has no playtime, stop or signal operation.
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
            Console.Error.WriteLine("Usage: playnite launch <decimal-appid>");
            return 2;
        }

        if (!FlatpakSteamLauncher.TryParseAppId(args[0], out var appId))
        {
            Console.Error.WriteLine("The app id must be a decimal uint without a sign, whitespace, prefix or fraction.");
            return 2;
        }

        return SteamLaunchModes.Select(new LinuxSteamHostFormat()) switch
        {
            SteamLaunchMode.FlatpakLaunchOnly => await FlatpakLaunchAsync(appId).ConfigureAwait(false),
            SteamLaunchMode.Tracked => await TrackedLaunchAsync(appId).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown Steam launch mode."),
        };
    }

    private static async Task<int> FlatpakLaunchAsync(uint appId)
    {
        var launcher = new FlatpakSteamLauncher(new HostUriOpener());
        var result = await launcher.LaunchAsync(appId, CancellationToken.None).ConfigureAwait(false);
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "flatpak-launch-only",
            status = result.Status.ToString(),
            appId = result.AppId,
            uri = result.Uri,
            tracking = "unknown",
            stop = "manual-steam",
            detail = result.Detail,
        }, Json));
        return result.Status == FlatpakLaunchStatus.RequestAccepted ? 0 : 1;
    }

    private static async Task<int> TrackedLaunchAsync(uint appId)
    {
        var environment = new LinuxEnvironment();
        var paths = new LinuxPaths(environment, new LinuxFileSystem());
        var files = new SteamFileSystem(new LinuxBoundedFileReader());
        var discovery = SteamDiscovery.Discover(environment, paths, files);
        if (discovery.Root is null)
        {
            Console.Error.WriteLine("No Steam installation was found; the tracked launch cannot start.");
            return 3;
        }

        SteamApp? app = null;
        string? prefix = null;
        foreach (var library in discovery.Libraries.Where(library => library.IsAvailable))
        {
            var manifestPath = Path.Combine(library.ResolvedPath, "steamapps",
                $"appmanifest_{appId.ToString(System.Globalization.CultureInfo.InvariantCulture)}.acf");
            var bytes = files.ReadFile(manifestPath, KeyValues.DefaultMaxBytes).Bytes;
            if (bytes is null)
            {
                continue;
            }

            var parsed = SteamManifestParser.Parse(bytes, manifestPath).Value;
            if (parsed is null || parsed.AppId != appId)
            {
                continue;
            }

            var install = files.GetRealPath(Path.Combine(library.ResolvedPath, "steamapps", "common", parsed.InstallDir));
            var isProton = files.GetRealPath(Path.Combine(library.ResolvedPath, "steamapps", "compatdata",
                appId.ToString(System.Globalization.CultureInfo.InvariantCulture), "pfx")) is not null
                && File.Exists(Path.Combine(library.ResolvedPath, "steamapps", "compatdata",
                    appId.ToString(System.Globalization.CultureInfo.InvariantCulture), "config_info"));
            app = parsed.ToApp(library.ResolvedPath, true, install is not null, isProton);
            prefix = files.GetRealPath(Path.Combine(library.ResolvedPath, "steamapps", "compatdata",
                appId.ToString(System.Globalization.CultureInfo.InvariantCulture), "pfx"));
            break;
        }

        if (app is null)
        {
            Console.Error.WriteLine($"App {appId} is not installed in a discovered Steam library; nothing was launched.");
            return 3;
        }

        var steam = File.Exists("/usr/bin/steam") ? "/usr/bin/steam" : null;
        await using var launcher = new SteamProcessLauncher(new LinuxExternalProcessHost(),
            new SteamLaunchTarget(app, prefix), new SteamLaunchOptions
            {
                SteamExecutable = steam,
                ProtocolOpener = "/usr/bin/xdg-open",
            });

        IGameProcess process;
        try
        {
            process = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (SteamAttributionUnknownException exception)
        {
            Console.Error.WriteLine($"TRACKING UNKNOWN: {exception.Message}");
            Console.Error.WriteLine("Stop or inspect the game manually in Steam; no process was signalled.");
            await launcher.DisposeAsync().ConfigureAwait(false);
            return 3;
        }

        Console.WriteLine($"ATTRIBUTED pid={process.Identity.ProcessId} start_ticks={process.Identity.StartTimeTicks} pgid={process.Identity.ProcessGroupId}");
        await using var tracker = new GameSessionTracker(launcher, launcher);
        tracker.StateChanged += (_, change) => Console.WriteLine(
            $"TRACK {change.State} degraded={change.IsDegraded} reason={change.Reason}");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            await tracker.TrackAsync(process, new SessionOptions(), cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The tracker already attempted its verified cleanup before reporting Cancelled.
        }

        Console.WriteLine($"SESSION {tracker.State} degraded={tracker.IsDegraded}");
        await tracker.DisposeAsync().ConfigureAwait(false);
        await launcher.DisposeAsync().ConfigureAwait(false);
        return tracker.State is GameSessionState.Stopped or GameSessionState.Cancelled ? 0 : 1;
    }

    /// <summary>The packaged argv-only OpenURI request. It opens one URI and never signals.</summary>
    private sealed class HostUriOpener : ISteamUriOpener
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
                process = Process.Start(startInfo) ?? throw new IOException("The OpenURI opener did not start.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
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
