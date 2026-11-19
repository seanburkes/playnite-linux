using System.Diagnostics;
using System.Text.Json;
using Playnite.Platform.Linux;
using Playnite.Platform.Sessions;
using Playnite.Steam;

namespace Playnite.Steam.Import;

/// <summary>
/// Read-only Steam library import for one packaged format. <c>--direct</c> reads the visible
/// filesystem (AppImage); <c>--portal</c> reads only directories the user grants through the
/// desktop portal (Flatpak). <c>--flatpak-launch</c> is the launch-only OpenURI entry: it
/// reports tracking UNKNOWN and has no stop or signal path. Import modes print the same JSON
/// shape so the evidence can compare app sets.
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
        if (args.Length == 2 && string.Equals(args[0], "--flatpak-launch", StringComparison.Ordinal))
        {
            return await FlatpakLaunchAsync(args[1]).ConfigureAwait(false);
        }

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        var fileSystem = new SteamFileSystem(new LinuxBoundedFileReader());
        var report = options.Mode == ImportMode.Portal
            ? await PortalAsync(fileSystem, options, CancellationToken.None)
            : DirectAsync(fileSystem);
        Console.Out.WriteLine(JsonSerializer.Serialize(report, Json));
        return report.Status is "complete" or "partial" ? 0 : 2;
    }

    private static async Task<int> FlatpakLaunchAsync(string text)
    {
        if (!FlatpakSteamLauncher.TryParseAppId(text, out var appId))
        {
            Console.Error.WriteLine("The app id must be a decimal uint without a sign, whitespace, prefix or fraction.");
            return 2;
        }

        if (SteamLaunchModes.Select(new LinuxSteamHostFormat()) != SteamLaunchMode.FlatpakLaunchOnly)
        {
            Console.Error.WriteLine("This host is not a Flatpak sandbox. The native/AppImage launch-plus-tracking path is unchanged; use the tracked acceptance entry instead of inventing an untracked launch.");
            return 2;
        }

        var launcher = new FlatpakSteamLauncher(new HostUriOpener());
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

    private static ImportReport DirectAsync(SteamFileSystem fileSystem)
    {
        var environment = new LinuxEnvironment();
        var paths = new LinuxPaths(environment, new LinuxFileSystem());
        var discovery = SteamDiscovery.Discover(environment, paths, fileSystem);
        if (discovery.Root is null)
        {
            return ImportReport.Failed("direct", "unavailable", "No Steam root was found in the visible filesystem.");
        }

        var libraries = new List<ImportLibrary>();
        var issues = new List<string>();
        foreach (var library in discovery.Libraries.Where(library => library.IsAvailable))
        {
            var steamApps = Path.Combine(library.ResolvedPath, "steamapps");
            var manifests = SteamManifestSet.Read(fileSystem, steamApps);
            issues.AddRange(manifests.Issues.Select(issue => $"{steamApps}: {issue}"));
            libraries.Add(new(library.DeclaredPath, steamApps, steamApps,
                manifests.Apps.Select(app => new ImportApp(app.AppId, app.Name)).ToArray()));
        }

        return new ImportReport("direct", "complete", libraries, AppIds(libraries), [], null,
            discovery.Root);
    }

    private static async Task<ImportReport> PortalAsync(SteamFileSystem fileSystem, Options options,
        CancellationToken cancellationToken)
    {
        var store = new JsonSteamPortalGrantStore(options.GrantsPath);
        await using var picker = new DesktopPortalLibraryPicker();
        var importer = new PortalSteamLibraryImporter(picker, store, fileSystem);
        var result = await importer.ImportAsync(cancellationToken).ConfigureAwait(false);
        var libraries = result.Libraries
            .Select(library => new ImportLibrary(library.HostLibraryPath, library.SteamAppsHostPath,
                library.PortalPath, library.Apps.Select(app => new ImportApp(app.AppId, app.Name)).ToArray()))
            .ToList();
        var grants = result.Grants
            .Select(grant => new ImportGrant(grant.HostLibraryPath, grant.SteamAppsHostPath, grant.DocumentId,
                grant.PortalPath))
            .ToList();
        return new ImportReport("portal", Status(result.Status), libraries, AppIds(libraries), result.Issues,
            result.Detail, null, grants);
    }

    private static string Status(SteamPortalImportStatus status) => status switch
    {
        SteamPortalImportStatus.Complete => "complete",
        SteamPortalImportStatus.Partial => "partial",
        SteamPortalImportStatus.Cancelled => "cancelled",
        SteamPortalImportStatus.Rejected => "rejected",
        SteamPortalImportStatus.Unavailable => "unavailable",
        _ => throw new InvalidOperationException("Unknown import status."),
    };

    private static uint[] AppIds(IEnumerable<ImportLibrary> libraries) =>
        libraries.SelectMany(library => library.Apps.Select(app => app.AppId)).Distinct().Order().ToArray();

    private enum ImportMode
    {
        Direct,
        Portal,
    }

    private sealed record Options(ImportMode Mode, string GrantsPath)
    {
        public static Options Parse(string[] args)
        {
            ImportMode? mode = null;
            string? grants = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--direct":
                        mode = SetMode(mode, ImportMode.Direct);
                        break;
                    case "--portal":
                        mode = SetMode(mode, ImportMode.Portal);
                        break;
                    case "--json":
                        break;
                    case "--grants":
                        index++;
                        if (index == args.Length)
                        {
                            throw new ArgumentException("--grants needs a path.");
                        }

                        grants = args[index];
                        break;
                    default:
                        throw new ArgumentException(
                            "Usage: playnite-steam-import [--direct|--portal] [--grants PATH] [--json] | --flatpak-launch APPID");
                }
            }

            var resolved = mode ?? (File.Exists("/.flatpak-info") ? ImportMode.Portal : ImportMode.Direct);
            return new(resolved, Path.GetFullPath(grants ?? DefaultGrantsPath()));
        }

        private static ImportMode SetMode(ImportMode? current, ImportMode mode) => current switch
        {
            null => mode,
            _ when current == mode => mode,
            _ => throw new ArgumentException("--direct and --portal are mutually exclusive."),
        };

        private static string DefaultGrantsPath()
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config))
            {
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }

            return Path.Combine(config, "playnite", "steam-portal-grants.json");
        }
    }

    private sealed record ImportApp(uint AppId, string Name);

    private sealed record ImportLibrary(string HostPath, string SteamAppsHostPath, string PortalPath,
        IReadOnlyList<ImportApp> Apps);

    private sealed record ImportGrant(string HostLibraryPath, string SteamAppsHostPath, string DocumentId,
        string PortalPath);

    private sealed record ImportReport(
        string Mode,
        string Status,
        IReadOnlyList<ImportLibrary> Libraries,
        IReadOnlyList<uint> AppIds,
        IReadOnlyList<string> Issues,
        string? Detail,
        string? Root,
        IReadOnlyList<ImportGrant>? Grants = null)
    {
        public static ImportReport Failed(string mode, string status, string detail) =>
            new(mode, status, [], [], [], detail, null);
    }

    /// <summary>The packaged argv-only OpenURI request. It opens one URI and never signals.</summary>
    private sealed class HostUriOpener : ISteamUriOpener
    {
        public async Task<int> OpenAsync(Playnite.Platform.Sessions.LaunchRequest request,
            CancellationToken cancellationToken)
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
                // The exec boundary is the only place a missing opener can surface; report it
                // as an unavailable request, never as a launch.
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
