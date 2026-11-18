using Playnite.Core;

namespace Playnite.Steam;

/// <summary>
/// Steam root resolution per <c>docs/port/08-STEAM-SPEC.md</c> §1.2: the first candidate in order
/// whose realpath exists and contains a usable <c>steamapps</c>. Every candidate is reported,
/// including later ones, so a duplicate realpath (the usual home <c>.steam/steam</c> link) is
/// visible rather than silently collapsed.
/// </summary>
public static class SteamRootResolver
{
    /// <summary>
    /// Resolves the root from the captured environment. The explicit override is taken verbatim,
    /// so a relative value is reported as <see cref="SteamRootState.InvalidPath"/> instead of being
    /// silently dropped before discovery sees it.
    /// </summary>
    public static SteamRootDiscovery Discover(IEnvironment environment, IPaths paths, ISteamFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        return Probe(Candidates(environment, paths), fileSystem);
    }

    public static IReadOnlyList<SteamRootCandidate> Candidates(IEnvironment environment, IPaths paths)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(paths);
        var candidates = new List<SteamRootCandidate>();
        var overridden = environment.GetVariable("STEAM_ROOT");
        if (!string.IsNullOrEmpty(overridden))
        {
            candidates.Add(new(SteamRootKind.Override, overridden));
        }

        candidates.Add(new(SteamRootKind.HomeSteam, Path.Combine(paths.Home, ".steam", "steam")));
        candidates.Add(new(SteamRootKind.HomeSteamRoot, Path.Combine(paths.Home, ".steam", "root")));
        var homeLocal = Path.Combine(paths.Home, ".local", "share", "Steam");
        var xdg = environment.GetVariable("XDG_DATA_HOME");
        if (Paths.IsFullPath(xdg))
        {
            var xdgSteam = Path.Combine(xdg!, "Steam");
            if (!Paths.AreEqual(xdgSteam, homeLocal, "/"))
            {
                candidates.Add(new(SteamRootKind.XdgData, xdgSteam));
            }
        }

        candidates.Add(new(SteamRootKind.HomeLocalShare, homeLocal));
        candidates.Add(new(SteamRootKind.Flatpak,
            Path.Combine(paths.Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")));
        return candidates.AsReadOnly();
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
        if (!Paths.IsFullPath(candidate.Path))
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

            var steamApps = Path.Combine(real, "steamapps");
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

    /// <summary>True when <paramref name="path"/> is the root itself or a descendant, on a path boundary.</summary>
    internal static bool IsWithin(string root, string path)
    {
        if (string.Equals(root, path, PathComparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
