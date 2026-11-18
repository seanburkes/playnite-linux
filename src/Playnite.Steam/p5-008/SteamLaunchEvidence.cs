using Playnite.Platform.Sessions;

namespace Playnite.Steam;

public enum SteamExecutionKind { Unknown, Native, Proton }

public sealed record SteamExecutionEvidence(SteamExecutionKind Kind, string Reason);

/// <summary>Evidence describes selection; Steam itself always chooses the runtime and version.</summary>
public static class SteamExecutionSelection
{
    public static SteamExecutionEvidence Evaluate(SteamManifest manifest, KeyValuesObject? platformOverrides,
        bool prefixExists, bool configInfoExists)
    {
        var app = manifest.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var entry = platformOverrides?.Find(app)?.Object;
        if (entry?.Text("src") == "windows" && entry.Text("dest") == "linux")
        {
            return new(SteamExecutionKind.Proton, prefixExists && configInfoExists
                ? "account override and prefix markers" : "account override; prefix not yet verified");
        }

        if (prefixExists && configInfoExists)
        {
            return new(SteamExecutionKind.Proton, "prefix and config_info markers; selected tool/version unknown");
        }

        if (entry?.Text("src") == "linux" && entry.Text("dest") == "linux")
        {
            return new(SteamExecutionKind.Native, "explicit Linux source and destination; not observed in the captured corpus");
        }

        return new(SteamExecutionKind.Unknown, "manifest has no authoritative executable platform; absence of a prefix is not native proof");
    }
}

public sealed record SteamLaunchTarget(SteamApp App, string? ProtonPrefix = null, string? ExecutableOverride = null);

public sealed record SteamLaunchOptions
{
    public required string ProtocolOpener { get; init; }
    public string? SteamExecutable { get; init; }
    public TimeSpan AttributionWindow { get; init; } = TimeSpan.FromSeconds(240);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(5);
    public IProgress<SteamAttributionProgress>? Progress { get; init; }
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record SteamAttributionProgress(TimeSpan Elapsed, TimeSpan Window, string Decision);

public sealed record SteamStopReport(bool IsDegraded, string Reason, IReadOnlyList<ProcessIdentity> HelperSurvivors);

public sealed class SteamAttributionUnknownException(string reason) : Exception(reason);

/// <summary>Pure attribution policy. All paths must already be canonical host paths.</summary>
public static class SteamProcessAttribution
{
    private static readonly HashSet<string> Infrastructure = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steam.sh", "steamwebhelper", "steamservice", "steamerrorreporter", "steammonitor",
        "steam-runtime-launcher-service", "srt-logger", "gameoverlayui", "steam-overlay-ui",
        "pv-adverb", "pressure-vessel-wrap", "pressure-vessel-adverb", "reaper", "srt-bwrap", "wineserver", "wineserver64",
        "wine", "wine64", "wine-preloader", "wine64-preloader", "proton", "bwrap", "pressure-vessel",
        "wineboot.exe", "winedevice.exe", "services.exe", "explorer.exe", "plugplay.exe", "rpcss.exe",
        "conhost.exe", "rundll32.exe", "start.exe", "steamclient_loader", "steam-launch-wrapper",
        "wineboot", "winecfg", "wineconsole", "winedbg", "winepath", "winefile", "winemenubuilder",
        "winecfg.exe", "wineconsole.exe", "winedbg.exe", "winepath.exe", "winefile.exe", "winemenubuilder.exe",
    };

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "dash", "zsh", "ksh", "mksh", "pdksh", "csh", "tcsh", "fish", "yash", "rbash", "busybox",
    };

    public static bool IsInfrastructure(string? executable)
    {
        var name = Path.GetFileName(executable);
        return name is not null && (Infrastructure.Contains(name)
            || name.StartsWith("pressure-vessel-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("steam-runtime-", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsShellOrSystemBinary(string executable) => Shells.Contains(Path.GetFileName(executable))
        || new[] { "/usr", "/bin", "/sbin", "/lib", "/lib64" }.Any(root => Under(executable, root));

    public static bool IsSteamClient(string? executable) => executable is not null
        && new[] { "steam", "steam.sh", "steamwebhelper", "steamservice" }.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase);

    public static bool Matches(SteamLaunchTarget target, string? executable) => executable is not null
        && !IsInfrastructure(executable)
        && !IsShellOrSystemBinary(executable)
        && (Under(executable, target.App.InstallPath) || Under(executable, target.ProtonPrefix))
        && (target.ExecutableOverride is null || executable == target.ExecutableOverride);

    public static bool Under(string path, string? root) => root is not null
        && Path.IsPathFullyQualified(root) && Path.IsPathFullyQualified(path)
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('/') + "/", StringComparison.Ordinal);

    public static ProcessIdentity? Find(SteamLaunchTarget target, ExternalProcessSnapshot before, ExternalProcessSnapshot after)
    {
        if (!before.IsComplete || !after.IsComplete)
        {
            return null;
        }

        var candidates = after.Processes.Where(row => row.Process.IsRunning && Matches(target, row.Executable)
            && !before.Processes.Any(old => SameProcess(old.Process.Identity, row.Process.Identity))).ToArray();
        if (candidates.Length != 1)
        {
            return null;
        }

        var candidate = candidates[0].Process.Identity;
        return GroupIsSafe(target, before, after, candidate.ProcessGroupId) ? candidate : null;
    }

    public static bool SameProcess(ProcessIdentity left, ProcessIdentity right) => left.ProcessId == right.ProcessId
        && left.StartTimeTicks == right.StartTimeTicks;

    public static bool GroupIsSafe(SteamLaunchTarget target, ExternalProcessSnapshot before,
        ExternalProcessSnapshot sample, int group)
    {
        if (group <= 1 || !before.IsComplete || !sample.IsComplete)
        {
            return false;
        }

        // A pre-existing member makes the group shared/unverifiable, including a client whose
        // executable could not be read. An unreadable live group member is never permission to kill.
        if (before.Processes.Any(row => row.Process.IsRunning && row.Process.Identity.ProcessGroupId == group))
        {
            return false;
        }

        var members = sample.Processes.Where(row => row.Process.IsRunning && row.Process.Identity.ProcessGroupId == group).ToArray();
        return members.Length > 0 && members.All(row => row.Executable is not null
            && !IsSteamClient(row.Executable)
            && !before.Processes.Any(old => SameProcess(old.Process.Identity, row.Process.Identity))
            && (Matches(target, row.Executable) || IsInfrastructure(row.Executable)));
    }
}
