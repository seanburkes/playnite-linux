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

/// <summary>One fresh executable matching the target's install directory or app prefix.</summary>
public sealed record SteamAttributionCandidate(ProcessIdentity Identity, string? Executable);

/// <summary>
/// Per-snapshot attribution evidence. A null <see cref="Rejection"/> with candidates in one
/// group means the group is eligible for game-only stop. Rejection names the exact predicate
/// and the process table values that produced it, so a wait can be explained, not guessed.
/// </summary>
public sealed record SteamAttributionDecision(
    IReadOnlyList<SteamAttributionCandidate> Candidates,
    IReadOnlyList<string> ToleratedMembers,
    string? Rejection)
{
    public ProcessIdentity? Identity => Rejection is null && Candidates.Count > 0 ? Candidates[0].Identity : null;

    public int? ProcessGroup => Rejection is null && Candidates.Count > 0 ? Candidates[0].Identity.ProcessGroupId : null;
}

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

    public static bool IsShell(string? executable) => executable is not null && Shells.Contains(Path.GetFileName(executable));

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
        => Evaluate(target, before, after).Identity;

    /// <summary>
    /// Decide ownership from one snapshot pair. Candidates may hold more than one executable
    /// (a Proton game commonly runs both a prefix .exe and an install-dir launcher), but every
    /// candidate must share one fresh group. Ownership is then the group's game identities;
    /// helpers are never signalled and their presence alone never withholds ownership.
    /// </summary>
    public static SteamAttributionDecision Evaluate(SteamLaunchTarget target, ExternalProcessSnapshot before,
        ExternalProcessSnapshot after)
    {
        var candidates = after.Processes
            .Where(row => row.Process.IsRunning && Matches(target, row.Executable)
                && !before.Processes.Any(old => SameProcess(old.Process.Identity, row.Process.Identity)))
            .Select(row => new SteamAttributionCandidate(row.Process.Identity, row.Executable))
            .ToArray();
        if (!before.IsComplete || !after.IsComplete)
        {
            return new(candidates, [], "process coverage incomplete");
        }

        if (candidates.Length == 0)
        {
            return new(candidates, [], "no fresh installdir/prefix game process");
        }

        var groups = candidates.Select(candidate => candidate.Identity.ProcessGroupId).Distinct().ToArray();
        if (groups.Length != 1)
        {
            return new(candidates, [], $"candidates span {groups.Length} process groups ({string.Join(", ", groups)})");
        }

        var tolerated = new List<string>();
        var rejection = GroupRejection(target, before, after, groups[0], tolerated);
        return new(candidates, tolerated, rejection);
    }

    public static bool SameProcess(ProcessIdentity left, ProcessIdentity right) => left.ProcessId == right.ProcessId
        && left.StartTimeTicks == right.StartTimeTicks;

    /// <summary>
    /// Null when the group may be used for game-only stop. A group is eligible when it did not
    /// exist before this launch and every readable member is a game binary or a launch helper.
    /// A Steam client or any pre-existing member makes it shared/unverifiable. A live member
    /// whose executable the host cannot read (a Steam runtime helper running in another mount
    /// namespace) is fresh, is never signalled, and is reported as tolerated evidence instead
    /// of poisoning the whole result.
    /// </summary>
    public static string? GroupRejection(SteamLaunchTarget target, ExternalProcessSnapshot before,
        ExternalProcessSnapshot sample, int group, List<string>? tolerated = null)
    {
        if (group <= 1 || !before.IsComplete || !sample.IsComplete)
        {
            return $"group {group} is not a fresh process group";
        }

        var preexisting = before.Processes.FirstOrDefault(row => row.Process.IsRunning
            && row.Process.Identity.ProcessGroupId == group);
        if (preexisting is not null)
        {
            return $"group {group} contains pre-existing pid={preexisting.Process.Identity.ProcessId}";
        }

        var members = sample.Processes.Where(row => row.Process.IsRunning
            && row.Process.Identity.ProcessGroupId == group).ToArray();
        if (members.Length == 0)
        {
            return $"group {group} has no live members";
        }

        foreach (var member in members)
        {
            var pid = member.Process.Identity.ProcessId;
            if (member.Executable is null)
            {
                tolerated?.Add($"pid={pid} exe=<unreadable>");
                continue;
            }

            if (IsSteamClient(member.Executable))
            {
                return $"group {group} contains Steam client pid={pid} exe={member.Executable}";
            }

            if (!Matches(target, member.Executable) && !IsInfrastructure(member.Executable)
                && !IsShell(member.Executable))
            {
                return $"group {group} contains unrelated member pid={pid} exe={member.Executable}";
            }
        }

        return null;
    }
}
