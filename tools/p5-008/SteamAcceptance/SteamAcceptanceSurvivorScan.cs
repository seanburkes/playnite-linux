using Playnite.Platform.Sessions;
using Playnite.Steam;

public sealed record SteamAcceptanceSurvivor(ExternalProcessEntry Entry, string Reason);

/// <summary>Read-only acceptance check for processes the session must leave gone.</summary>
public static class SteamAcceptanceSurvivorScan
{
    private static readonly HashSet<string> WineNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "wine", "wine64", "wine-preloader", "wine64-preloader", "wineserver", "wineserver64",
        "winedevice", "services", "explorer", "rpcss", "plugplay", "conhost", "rundll32",
    };

    public static IReadOnlyList<SteamAcceptanceSurvivor> Find(SteamLaunchTarget target, ISteamFileSystem files,
        ExternalProcessSnapshot before, ExternalProcessSnapshot after, int? group,
        IReadOnlyCollection<ProcessIdentity> observed)
    {
        var baseline = before.Processes.Where(row => row.Process.IsRunning)
            .Select(row => Key(row.Process.Identity)).ToHashSet();
        var tracked = observed.Select(Key).ToHashSet();
        var install = target.App.InstallPath;
        var survivors = new List<SteamAcceptanceSurvivor>();
        foreach (var row in after.Processes.Where(row => row.Process.IsRunning))
        {
            var identity = row.Process.Identity;
            var key = Key(identity);
            if (identity.ProcessGroupId == group || tracked.Contains(key))
            {
                survivors.Add(new(row, "game group or tracked identity"));
            }
            else if (HasAppPath(row.Executable)
                || row.CommandLine?.Any(argument => HasAppPath(ArgumentValue(argument), row.WorkingDirectory)) == true)
            {
                survivors.Add(new(row, "process with app install or prefix path"));
            }
            else if (!baseline.Contains(key) && (IsWine(row.ProcessName) || IsWine(row.Executable)
                || row.CommandLine is { Count: > 0 } argv && IsWine(argv[0])))
            {
                // Shared Wine binaries can daemonize with no app path in argv. Fresh Wine
                // processes make this isolated acceptance run inconclusive even then.
                survivors.Add(new(row, "fresh Wine process; inspect the app prefix"));
            }
        }

        return survivors;

        bool HasAppPath(string? path, string? workingDirectory = null)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var absolute = Path.IsPathFullyQualified(path);
            if (!absolute)
            {
                if (ProtonCommandLine.ResolveAppPath(target, path) is not null)
                {
                    return true;
                }

                if (workingDirectory is null)
                {
                    return false;
                }

                path = Path.Combine(workingDirectory, path);
            }

            string? resolved;
            try
            {
                resolved = files.GetRealPath(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                resolved = null;
            }

            return InAppRoots(resolved ?? (absolute ? path : null));
        }

        bool InAppRoots(string? path) => path is not null && (path == install || path == target.ProtonPrefix
            || SteamProcessAttribution.Under(path, install) || SteamProcessAttribution.Under(path, target.ProtonPrefix));
    }

    private static (int Pid, long StartTicks) Key(ProcessIdentity identity) => (identity.ProcessId, identity.StartTimeTicks);

    private static bool IsWine(string? path)
    {
        var name = Path.GetFileName(path?.Replace('\\', '/'));
        return WineNames.Contains(name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
            ? name[..^4]
            : name ?? string.Empty);
    }

    private static string ArgumentValue(string argument)
    {
        var separator = argument.IndexOf('=');
        return separator > 2 && argument.StartsWith("--", StringComparison.Ordinal)
            ? argument[(separator + 1)..]
            : argument;
    }
}
