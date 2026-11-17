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
    /// <c>config/libraryfolders.vdf</c> when the primary copy is absent), and joins every parsed
    /// entry with its availability. Re-run after a mount or refresh; a missing library is kept.
    /// </summary>
    public static SteamDiscoveryResult Discover(IPaths paths, ISteamFileSystem fileSystem, int maxBytes = KeyValues.DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var roots = SteamRootResolver.Discover(paths, fileSystem);
        var issues = new List<SteamParseIssue>();
        var libraries = new List<SteamDiscoveredLibrary>();
        if (roots.Root is not { } root)
        {
            return new(roots, null, libraries.AsReadOnly(), issues.AsReadOnly());
        }

        var file = SelectFile(root, fileSystem);
        if (file is null)
        {
            issues.Add(new(System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf"), "libraryfolders", "library-file-missing", null, 0));
        }

        var parsed = file is null ? null : ReadAndParse(file, root, fileSystem, maxBytes, issues);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declared in parsed?.Libraries ?? [])
        {
            libraries.Add(Inspect(declared, file!, fileSystem, seen, issues));
        }

        if (!seen.ContainsKey(root))
        {
            var declared = new SteamLibrary(RootLibraryIndex, root, null, null, null, null, null,
                new Dictionary<uint, ulong>(), []);
            libraries.Insert(0, new SteamDiscoveredLibrary(RootLibraryIndex, declared, root, SteamLibraryStatus.Available));
        }

        return new(roots, file, libraries.AsReadOnly(), issues.AsReadOnly());
    }

    private static string? SelectFile(string root, ISteamFileSystem fileSystem)
    {
        var primary = System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (fileSystem.FileExists(primary))
        {
            return primary;
        }

        var secondary = System.IO.Path.Combine(root, "config", "libraryfolders.vdf");
        return fileSystem.FileExists(secondary) ? secondary : null;
    }

    private static SteamLibraries? ReadAndParse(string file, string root, ISteamFileSystem fileSystem, int maxBytes, List<SteamParseIssue> issues)
    {
        try
        {
            var real = fileSystem.GetRealPath(file);
            if (real is null)
            {
                issues.Add(new(file, "libraryfolders", "library-file-missing", null, 0));
                return null;
            }

            if (!SteamRootResolver.IsWithin(root, real))
            {
                issues.Add(new(file, "libraryfolders", "library-file-outside-root", null, 0));
                return null;
            }

            if (fileSystem.GetFileLength(file) > maxBytes)
            {
                issues.Add(new(file, "libraryfolders", "size-limit", null, 0));
                return null;
            }

            var result = SteamLibraryParser.Parse(fileSystem.ReadAllBytes(file), file, maxBytes);
            issues.AddRange(result.Issues);
            return result.Value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(new(file, "libraryfolders", "unreadable-library-file", null, 0));
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
                issues.Add(new(file, declared.Index, "duplicate-library", first, 0));
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

        var steamApps = System.IO.Path.Combine(real, "steamapps");
        if (!fileSystem.DirectoryExists(steamApps))
        {
            return SteamLibraryStatus.SteamAppsMissing;
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
