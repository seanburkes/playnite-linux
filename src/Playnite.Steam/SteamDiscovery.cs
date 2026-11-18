using Playnite.Core;

namespace Playnite.Steam;

/// <summary>
/// Read-only Steam root and library discovery for <c>docs/port/08-STEAM-SPEC.md</c> §§1–2.
/// A call only reads: it never creates, modifies or repairs anything under a Steam tree, never
/// touches the network or UI, and never follows a <c>steamapps</c> link outside its library root.
/// The entry list from the merged p5-001 parser is reused; this unit adds filesystem observation.
/// </summary>
public static class SteamDiscovery
{
    public const string RootLibraryIndex = "root";

    /// <summary>
    /// Resolves the root, reads <c>steamapps/libraryfolders.vdf</c> (falling back to
    /// <c>config/libraryfolders.vdf</c> when the primary copy is absent or rejected), and joins
    /// every parsed entry with its availability. Re-run after a mount or refresh; a missing
    /// library is kept.
    /// </summary>
    public static SteamDiscoveryResult Discover(IEnvironment environment, IPaths paths, ISteamFileSystem fileSystem,
        int maxBytes = KeyValues.DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var roots = SteamRootResolver.Discover(environment, paths, fileSystem);
        var issues = new List<SteamParseIssue>();
        var libraries = new List<SteamDiscoveredLibrary>();
        if (roots.Root is not { } root)
        {
            return new(roots, null, libraries.AsReadOnly(), issues.AsReadOnly());
        }

        var primary = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        var secondary = Path.Combine(root, "config", "libraryfolders.vdf");
        string? file = null;
        var parsed = TryReadAndParse(primary, root, fileSystem, maxBytes, issues, out var primarySeen);
        if (primarySeen)
        {
            file = primary;
        }

        if (parsed is null)
        {
            var fallbackIssues = new List<SteamParseIssue>();
            parsed = TryReadAndParse(secondary, root, fileSystem, maxBytes, fallbackIssues, out var secondarySeen);
            if (secondarySeen)
            {
                file = secondary;
                issues.AddRange(fallbackIssues);
            }
        }

        // Unknown root fields are intentionally discarded here; only typed discovery state escapes.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declared in parsed?.Libraries ?? [])
        {
            libraries.Add(Inspect(declared, file ?? throw new IOException("Parsed library file has no path."), fileSystem, seen, issues));
        }

        if (!seen.ContainsKey(root))
        {
            var declared = new SteamLibrary(RootLibraryIndex, root, null, null, null, null, null,
                new Dictionary<uint, ulong>(), []);
            libraries.Insert(0, new SteamDiscoveredLibrary(RootLibraryIndex, declared, root, SteamLibraryStatus.Available));
        }

        return new(roots, file, libraries.AsReadOnly(), issues.AsReadOnly());
    }

    private static SteamLibraries? TryReadAndParse(string file, string root, ISteamFileSystem fileSystem, int maxBytes,
        List<SteamParseIssue> issues, out bool observed)
    {
        observed = false;
        var name = Path.GetFileName(file);
        try
        {
            // Resolution checks containment; the reader must additionally bind type/size/data to
            // one handle and refuse links in every component of this canonical path.
            var real = fileSystem.GetRealPath(file);
            if (real is null)
            {
                issues.Add(new(name, "libraryfolders", "library-file-missing", null, 0));
                return null;
            }

            observed = true;
            if (!SteamRootResolver.IsWithin(root, real))
            {
                issues.Add(new(name, "libraryfolders", "library-file-outside-root", null, 0));
                return null;
            }

            var read = fileSystem.ReadFile(real, maxBytes);
            switch (read.Status)
            {
                case SteamFileReadStatus.Missing:
                    issues.Add(new(name, "libraryfolders", "library-file-missing", null, 0));
                    return null;
                case SteamFileReadStatus.NotRegularFile:
                    issues.Add(new(name, "libraryfolders", "not-a-regular-file", null, 0));
                    return null;
                case SteamFileReadStatus.TooLarge:
                    issues.Add(new(name, "libraryfolders", "size-limit", null, 0));
                    return null;
            }

            var bytes = read.Bytes ?? throw new IOException("Successful library read has no bytes.");
            var result = SteamLibraryParser.Parse(bytes, name, maxBytes);
            issues.AddRange(result.Issues);
            return result.Value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            observed = true;
            issues.Add(new(name, "libraryfolders", "unreadable-library-file", null, 0));
            return null;
        }
    }

    private static SteamDiscoveredLibrary Inspect(SteamLibrary declared, string file, ISteamFileSystem fileSystem,
        Dictionary<string, string> seen, List<SteamParseIssue> issues)
    {
        string? real = null;
        var resolved = declared.Path;
        SteamLibraryStatus status;
        try
        {
            real = fileSystem.GetRealPath(declared.Path);
            resolved = real ?? declared.Path;
            if (seen.TryGetValue(resolved, out var first))
            {
                issues.Add(new(Path.GetFileName(file), declared.Index, "duplicate-library", first, 0));
                return new(declared.Index, declared, resolved, SteamLibraryStatus.DuplicateLibrary);
            }

            seen.Add(resolved, declared.Index);
            status = real is null ? SteamLibraryStatus.MissingPath : Availability(real, fileSystem);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status = SteamLibraryStatus.Unreadable;
        }

        return new(declared.Index, declared, resolved, status);
    }

    private static SteamLibraryStatus Availability(string real, ISteamFileSystem fileSystem)
    {
        if (!fileSystem.DirectoryExists(real))
        {
            return SteamLibraryStatus.NotDirectory;
        }

        var steamApps = Path.Combine(real, "steamapps");
        if (!fileSystem.DirectoryExists(steamApps))
        {
            return fileSystem.IsMountPoint(real) ? SteamLibraryStatus.StaleMountpoint : SteamLibraryStatus.SteamAppsMissing;
        }

        var steamAppsReal = fileSystem.GetRealPath(steamApps);
        if (steamAppsReal is null)
        {
            return SteamLibraryStatus.SteamAppsMissing;
        }

        return SteamRootResolver.IsWithin(real, steamAppsReal)
            ? SteamLibraryStatus.Available
            : SteamLibraryStatus.SteamAppsOutsideLibrary;
    }
}
