using System.Globalization;
using Playnite.Core;

namespace Playnite.Steam.Sync;

/// <summary>
/// The production discovery host. It composes the merged p5-002 root/library discovery with the
/// p5-001 manifest parser: every available library is enumerated read-only, every declared app
/// without a usable manifest is reported, and only bytes already on disk are read.
/// </summary>
public sealed class SteamDiscoverySource : ISteamSyncDiscovery
{
    private readonly IEnvironment environment;
    private readonly IPaths paths;
    private readonly ISteamFileSystem fileSystem;

    public SteamDiscoverySource(IEnvironment environment, IPaths paths, ISteamFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        this.environment = environment;
        this.paths = paths;
        this.fileSystem = fileSystem;
    }

    public SteamSyncDiscovery Discover(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = SteamDiscovery.Discover(environment, paths, fileSystem);
        var apps = new List<SteamApp>();
        var skipped = new List<SteamSyncSkip>();
        var issues = result.Issues
            .Select(issue => new SteamSyncIssue(issue.Reason, ParseIdentity(issue.Identity), issue.Field))
            .ToList();

        if (result.Root is null)
        {
            issues.Add(new(SteamSyncReasons.SteamRootMissing));
            return new(apps.AsReadOnly(), skipped.AsReadOnly(), issues.AsReadOnly(), false);
        }

        // Without a selected library file the root is the only known library. That is still a
        // complete observation of the root, so reconciliation is safe when every library that
        // was declared could be enumerated.
        var canReconcile = result.LibraryFilePath is not null;
        var seenApps = new HashSet<uint>();
        foreach (var library in result.Libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var declared = library.Apps?.Keys.ToArray() ?? [];
            if (!library.IsAvailable)
            {
                var reason = ReasonFor(library.Status);
                foreach (var appId in declared)
                {
                    skipped.Add(new(appId, reason));
                }

                continue;
            }

            string steamApps;
            string[] files;
            try
            {
                steamApps = Path.Combine(library.ResolvedPath, "steamapps");
                files = Directory.GetFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new(SteamSyncReasons.LibraryEnumerationFailed, null, exception.GetType().Name));
                canReconcile = false;
                foreach (var appId in declared)
                {
                    skipped.Add(new(appId, SteamSyncReasons.ManifestUnreadable));
                }

                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            var seenManifests = new HashSet<uint>();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileId = TryFileAppId(file);
                if (fileId is null)
                {
                    issues.Add(new(SteamSyncReasons.ManifestInvalidName, null, Path.GetFileName(file)));
                    continue;
                }

                var appId = fileId.Value;
                seenManifests.Add(appId);
                if (seenApps.Contains(appId))
                {
                    skipped.Add(new(appId, SteamSyncReasons.AppIdDuplicate));
                    continue;
                }

                var read = Read(file);
                if (read is not null)
                {
                    skipped.Add(new(appId, read));
                    continue;
                }

                SteamParseResult<SteamManifest> parsed;
                try
                {
                    var bytes = fileSystem.ReadFile(file, KeyValues.DefaultMaxBytes);
                    if (bytes.Status != SteamFileReadStatus.Ok || bytes.Bytes is null)
                    {
                        skipped.Add(new(appId, ReasonFor(bytes.Status)));
                        continue;
                    }

                    parsed = SteamManifestParser.Parse(bytes.Bytes, Path.GetFileName(file));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new(appId, SteamSyncReasons.ManifestUnreadable));
                    issues.Add(new(SteamSyncReasons.ManifestUnreadable, appId, exception.GetType().Name));
                    continue;
                }

                if (parsed.Value is null)
                {
                    skipped.Add(new(appId, SteamSyncReasons.ManifestRejected));
                    foreach (var issue in parsed.Issues)
                    {
                        issues.Add(new(SteamSyncReasons.ManifestRejected, appId, issue.Reason));
                    }

                    continue;
                }

                var manifest = parsed.Value;
                var installExists = fileSystem.DirectoryExists(Path.Combine(steamApps, "common", manifest.InstallDir));
                var proton = fileSystem.DirectoryExists(Path.Combine(steamApps, "compatdata",
                    appId.ToString(CultureInfo.InvariantCulture), "pfx"));
                try
                {
                    apps.Add(manifest.ToApp(library.ResolvedPath, true, installExists, proton));
                    seenApps.Add(appId);
                }
                catch (ArgumentException exception)
                {
                    skipped.Add(new(appId, SteamSyncReasons.InvalidInstallPath));
                    issues.Add(new(SteamSyncReasons.InvalidInstallPath, appId, exception.Message));
                }
            }

            foreach (var appId in declared)
            {
                if (!seenManifests.Contains(appId))
                {
                    skipped.Add(new(appId, SteamSyncReasons.ManifestMissing));
                }
            }
        }

        apps.Sort((left, right) => left.AppId.CompareTo(right.AppId));
        skipped.Sort((left, right) =>
        {
            var id = left.AppId.CompareTo(right.AppId);
            return id != 0 ? id : string.CompareOrdinal(left.Reason, right.Reason);
        });
        return new(apps.AsReadOnly(), skipped.AsReadOnly(), issues.AsReadOnly(), canReconcile);
    }

    // A pre-read check keeps a missing or non-regular entry from being reported as a generic read
    // failure. The bounded reader repeats the authoritative type/size check on one handle.
    private string? Read(string file)
    {
        if (!fileSystem.FileExists(file))
        {
            return fileSystem.EntryExists(file) ? SteamSyncReasons.ManifestUnreadable : SteamSyncReasons.ManifestMissing;
        }

        try
        {
            return fileSystem.GetRealPath(file) is null ? SteamSyncReasons.ManifestUnreadable : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SteamSyncReasons.ManifestUnreadable;
        }
    }

    private static string ReasonFor(SteamLibraryStatus status) => status switch
    {
        SteamLibraryStatus.MissingPath => SteamSyncReasons.LibraryMissing,
        SteamLibraryStatus.NotDirectory => SteamSyncReasons.LibraryNotDirectory,
        SteamLibraryStatus.SteamAppsMissing => SteamSyncReasons.LibrarySteamAppsMissing,
        SteamLibraryStatus.SteamAppsOutsideLibrary => SteamSyncReasons.LibrarySteamAppsOutside,
        SteamLibraryStatus.StaleMountpoint => SteamSyncReasons.LibraryStaleMountpoint,
        SteamLibraryStatus.DuplicateLibrary => SteamSyncReasons.LibraryDuplicate,
        SteamLibraryStatus.Unreadable => SteamSyncReasons.LibraryUnreadable,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "An available library has no skip reason."),
    };

    private static string ReasonFor(SteamFileReadStatus status) => status switch
    {
        SteamFileReadStatus.Missing => SteamSyncReasons.ManifestMissing,
        SteamFileReadStatus.NotRegularFile => SteamSyncReasons.ManifestNotRegular,
        SteamFileReadStatus.TooLarge => SteamSyncReasons.ManifestTooLarge,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A successful read has no skip reason."),
    };

    private static uint? TryFileAppId(string file)
    {
        var name = Path.GetFileName(file);
        const string prefix = "appmanifest_";
        const string suffix = ".acf";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal) ||
            name.Length <= prefix.Length + suffix.Length)
        {
            return null;
        }

        var digits = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        if (digits.Length > 10)
        {
            return null;
        }

        foreach (var character in digits)
        {
            if (character is < '0' or > '9')
            {
                return null;
            }
        }

        return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) ? appId : null;
    }

    private static uint? ParseIdentity(string identity)
    {
        if (identity.Length is 0 or > 10)
        {
            return null;
        }

        foreach (var character in identity)
        {
            if (character is < '0' or > '9')
            {
                return null;
            }
        }

        return uint.TryParse(identity, CultureInfo.InvariantCulture, out var appId) ? appId : null;
    }
}
