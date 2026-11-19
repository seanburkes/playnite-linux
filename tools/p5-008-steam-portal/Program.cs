using System.Text.Json;
using Playnite.Platform.Linux;
using Playnite.Steam;

namespace Playnite.Steam.Import;

/// <summary>
/// Read-only Steam library import for one packaged format. <c>--direct</c> reads the visible
/// filesystem (AppImage); <c>--portal</c> reads only directories the user grants through the
/// desktop portal (Flatpak). Both print the same JSON shape so the evidence can compare app sets.
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
                            "Usage: playnite-steam-import [--direct|--portal] [--grants PATH] [--json]");
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
}
