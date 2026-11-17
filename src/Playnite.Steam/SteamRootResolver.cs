using Playnite.Core;
using CorePaths = Playnite.Core.Paths;

namespace Playnite.Steam;

/// <summary>
/// Steam root resolution per <c>docs/port/08-STEAM-SPEC.md</c> §1.2: the first candidate in order
/// whose realpath exists and contains a usable <c>steamapps</c>. Every candidate is reported,
/// including later ones, so a duplicate realpath (the usual <c>~/.steam/steam</c> symlink) is
/// visible rather than silently collapsed.
/// </summary>
public static class SteamRootResolver
{
    /// <summary>
    /// Resolves the root from <see cref="IPaths.SteamRootCandidates"/>. The explicit
    /// <c>$STEAM_ROOT</c> override is recognised as the one candidate that is not one of the three
    /// standard derivations documented by p3-001; if the override happens to equal a standard
    /// path, the standard kind wins and the result is unchanged.
    /// </summary>
    public static SteamRootDiscovery Discover(IPaths paths, ISteamFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        return Probe(Candidates(paths), fileSystem);
    }

    public static IReadOnlyList<SteamRootCandidate> Candidates(IPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var standard = new[]
        {
            (SteamRootKind.HomeSteam, System.IO.Path.Combine(paths.Home, ".steam", "steam")),
            (SteamRootKind.HomeLocalShare, System.IO.Path.Combine(paths.Home, ".local", "share", "Steam")),
            (SteamRootKind.Flatpak, System.IO.Path.Combine(paths.Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")),
        };
        return paths.SteamRootCandidates
            .Select(path => new SteamRootCandidate(Kind(path, standard), path))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Probes an explicit ordered candidate list. Later usable roots are reported as shadowed.</summary>
    public static SteamRootDiscovery Probe(IReadOnlyList<SteamRootCandidate> candidates, ISteamFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(fileSystem);
        var probes = new List<SteamRootProbe>(candidates.Count);
        var seen = new Dictionary<string, SteamRootKind>(StringComparer.Ordinal);
        string? root = null;
        foreach (var candidate in candidates)
        {
            var probe = Inspect(candidate, seen, root, fileSystem);
            if (probe.RealPath is { } real)
            {
                seen.TryAdd(real, candidate.Kind);
            }

            if (probe.State == SteamRootState.Accepted)
            {
                root = probe.RealPath;
            }

            probes.Add(probe);
        }

        return new(root, probes.AsReadOnly());
    }

    private static SteamRootProbe Inspect(SteamRootCandidate candidate, Dictionary<string, SteamRootKind> seen, string? root, ISteamFileSystem fileSystem)
    {
        if (!CorePaths.IsFullPath(candidate.Path))
        {
            return new(candidate.Kind, candidate.Path, null, SteamRootState.InvalidPath);
        }

        string? real = null;
        try
        {
            real = fileSystem.GetRealPath(candidate.Path);
            if (real is null)
            {
                return new(candidate.Kind, candidate.Path, null, SteamRootState.Missing);
            }

            if (seen.ContainsKey(real))
            {
                return new(candidate.Kind, candidate.Path, real, SteamRootState.DuplicateRealPath);
            }

            if (!fileSystem.DirectoryExists(real))
            {
                return new(candidate.Kind, candidate.Path, real, SteamRootState.NotDirectory);
            }

            var steamApps = System.IO.Path.Combine(real, "steamapps");
            if (!fileSystem.DirectoryExists(steamApps))
            {
                return new(candidate.Kind, candidate.Path, real, SteamRootState.SteamAppsMissing);
            }

            var steamAppsReal = fileSystem.GetRealPath(steamApps);
            if (steamAppsReal is null)
            {
                return new(candidate.Kind, candidate.Path, real, SteamRootState.SteamAppsMissing);
            }

            if (!IsWithin(real, steamAppsReal))
            {
                return new(candidate.Kind, candidate.Path, real, SteamRootState.SteamAppsOutsideRoot);
            }

            return new(candidate.Kind, candidate.Path, real,
                root is null ? SteamRootState.Accepted : SteamRootState.Shadowed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(candidate.Kind, candidate.Path, real, SteamRootState.Unreadable);
        }
    }

    private static SteamRootKind Kind(string path, IEnumerable<(SteamRootKind Kind, string Path)> standard)
    {
        foreach (var (kind, known) in standard)
        {
            if (CorePaths.AreEqual(known, path, "/"))
            {
                return kind;
            }
        }

        return SteamRootKind.Override;
    }

    /// <summary>True when <paramref name="path"/> is the root itself or a descendant, on a path boundary.</summary>
    internal static bool IsWithin(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
