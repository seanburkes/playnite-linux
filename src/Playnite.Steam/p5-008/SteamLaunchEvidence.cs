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

/// <summary>
/// Terminal stop evidence. <see cref="IsDegraded"/> is true whenever the group is not proven
/// empty, so a Stopped/Cancelled session with survivors is never reported as a clean stop.
/// <see cref="HelperSurvivors"/> holds only members with provable Steam/Proton ownership
/// evidence; <see cref="UnidentifiedSurvivors"/> holds remaining members whose identity is
/// unknown and which therefore require manual inspection.
/// </summary>
public sealed record SteamStopReport(bool IsDegraded, string Reason,
    IReadOnlyList<ProcessIdentity> HelperSurvivors, IReadOnlyList<ProcessIdentity> UnidentifiedSurvivors);

/// <summary>
/// One fresh game executable matching the target's install directory or app prefix.
/// <see cref="Executable"/> is the host-visible <c>/proc/PID/exe</c>; for a Wine/Proton game
/// that is the runtime preloader. <see cref="GamePath"/> is the identity the policy accepted:
/// the same host executable for a native title, or the Windows binary resolved through the
/// app's own <c>pfx/dosdevices</c> map for a Proton title. <see cref="WindowsArgument"/> is the
/// exact argv entry that supplied that path, rechecked immediately before SIGTERM.
/// </summary>
public sealed record SteamAttributionCandidate(ProcessIdentity Identity, string Executable,
    string GamePath, string? WindowsArgument, IReadOnlyList<string>? CommandLine);

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

/// <summary>
/// Resolve a Wine/Proton game's Windows binary through the app's own prefix drive map. A Proton
/// game's host executable is the runtime preloader inside the Proton distribution, never a path
/// under the installdir or prefix, so <c>/proc/PID/exe</c> alone can never identify it. The
/// command line names the Windows image; <c>&lt;prefix&gt;/dosdevices/&lt;drive&gt;:</c> is a
/// symlink the app's own prefix provides, and the resolved host path must still be under this
/// target's installdir or prefix. Nothing is guessed: a missing drive link, a relative or UNC
/// path, a wrapper identity, or a path outside those roots yields no candidate.
/// </summary>
public static class ProtonCommandLine
{
    // Steam/Proton/Wine wrapper identities. These run inside a prefix or beside a game and can
    // carry a game path in argv; they are never the game. The runtime preloaders are
    // deliberately absent: a Proton game's own process executes through them.
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steam.sh", "steam.exe", "steamwebhelper", "steamservice", "steamerrorreporter",
        "steammonitor", "steam-launch-wrapper", "steamclient_loader", "gameoverlayui", "steam-overlay-ui",
        "python", "python2", "python3", "python.exe", "pythonw", "pythonw.exe",
        "pv-adverb", "pressure-vessel", "pressure-vessel-wrap", "pressure-vessel-adverb",
        "reaper", "srt-bwrap", "bwrap", "proton", "wineserver", "wineserver64",
        "winedevice", "winedevice.exe", "explorer", "explorer.exe", "services", "services.exe",
        "plugplay", "plugplay.exe", "rpcss", "rpcss.exe", "conhost", "conhost.exe",
        "rundll32", "rundll32.exe", "start", "start.exe", "winemenubuilder", "winemenubuilder.exe",
        "wineboot", "wineboot.exe", "winecfg", "winecfg.exe", "wineconsole", "wineconsole.exe",
        "winedbg", "winedbg.exe", "regsvr32", "regsvr32.exe", "msiexec", "msiexec.exe",
    };

    public static bool IsWrapper(string? executable)
    {
        var name = WindowsBaseName(executable);
        return name is not null && (Wrappers.Contains(name)
            || name.StartsWith("pressure-vessel-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("steam-runtime-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolve the process image path (argv[0]) when it is an absolute Windows drive path to a
    /// real host file under the target's installdir or prefix. Only argv[0] is the image: a
    /// wrapper that merely carries the game path in a later argument is never the game.
    /// Returns false without touching the filesystem when the target has no Proton prefix.
    /// </summary>
    public static bool TryResolve(SteamLaunchTarget target, IReadOnlyList<string>? commandLine,
        out string gamePath, out string? windowsArgument)
    {
        gamePath = string.Empty;
        windowsArgument = null;
        if (target.ProtonPrefix is not { } prefix || commandLine is not { Count: > 0 })
        {
            return false;
        }

        var argument = commandLine[0];
        if (!TryParseDrivePath(argument, out var drive, out var relative) || IsWrapper(argument))
        {
            return false;
        }

        var mapped = Map(prefix, drive, relative);
        if (mapped is null || IsWrapper(mapped) || !SteamProcessAttribution.Matches(target, mapped))
        {
            return false;
        }

        gamePath = mapped;
        windowsArgument = argument;
        return true;
    }

    private static string? Map(string prefix, char drive, string relative)
    {
        var link = Path.Combine(prefix, "dosdevices", $"{char.ToLowerInvariant(drive)}:");
        var root = Canonical(link);
        if (root is null)
        {
            return null;
        }

        var combined = root;
        foreach (var part in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                combined = Path.GetDirectoryName(combined);
                if (combined is null)
                {
                    return null;
                }

                continue;
            }

            combined = Path.Combine(combined, part);
        }

        // Only a real, canonical host file is evidence. A drive mapped to "/" must not let a
        // Windows path escape into the host once resolved.
        var canonical = Canonical(combined);
        return canonical is not null && File.Exists(canonical) ? canonical : null;
    }

    /// <summary>Absolute drive-letter path with either separator, e.g. <c>S:\a\b.exe</c>.</summary>
    private static bool TryParseDrivePath(string argument, out char drive, out string relative)
    {
        drive = '\0';
        relative = string.Empty;
        if (argument.Length < 3 || !char.IsAsciiLetter(argument[0]) || argument[1] != ':'
            || (argument[2] != '\\' && argument[2] != '/'))
        {
            return false;
        }

        drive = argument[0];
        relative = argument[2..];
        return true;
    }

    private static string? WindowsBaseName(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    /// <summary>Resolve every path component so a symlinked directory cannot alias the roots.</summary>
    private static string? Canonical(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var current = root;
            foreach (var part in full[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Path.Combine(current, part);
                var target = File.ResolveLinkTarget(next, true);
                current = target?.FullName ?? next;
            }

            return current;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

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

    /// <summary>A Steam/Proton wrapper identity that must never be taken as the game.</summary>
    public static bool IsProtonWrapper(string? executable) => ProtonCommandLine.IsWrapper(executable);

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
    /// The host-visible executable is the primary identity. Only when it matches nothing does
    /// a Proton target resolve the Windows argv path through this app's own prefix drive map.
    /// </summary>
    public static SteamAttributionDecision Evaluate(SteamLaunchTarget target, ExternalProcessSnapshot before,
        ExternalProcessSnapshot after)
    {
        var candidates = new List<SteamAttributionCandidate>();
        foreach (var row in after.Processes)
        {
            if (!row.Process.IsRunning
                || before.Processes.Any(old => SameProcess(old.Process.Identity, row.Process.Identity))
                || !TryGetGame(target, row, out var gamePath, out var windowsArgument))
            {
                continue;
            }

            candidates.Add(new(row.Process.Identity, row.Executable!, gamePath, windowsArgument, row.CommandLine));
        }

        if (!before.IsComplete || !after.IsComplete)
        {
            return new(candidates, [], "process coverage incomplete");
        }

        // A candidate whose own row changed group mid-read is not stable evidence. Unrelated
        // moved rows are irrelevant here and must not reject an otherwise fresh group.
        if (candidates.Any(candidate => after.Moved.Any(moved => SameProcess(moved, candidate.Identity))))
        {
            return new(candidates, [], "a candidate changed process group between reads");
        }

        if (candidates.Count == 0)
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
    /// The accepted game identity for a live row, if any. <c>/proc/PID/exe</c> is authoritative
    /// for native titles. For a Proton target whose host executable is a runtime path, the
    /// Windows argv path resolved through the app's own prefix is the only executable evidence.
    /// A wrapper identity is never accepted even when its argv names the game.
    /// </summary>
    public static bool IsGame(SteamLaunchTarget target, ExternalProcessEntry row) =>
        TryGetGame(target, row, out _, out _);

    /// <summary>
    /// A live tracked identity whose evidence cannot be read at all is unverifiable, never an
    /// exit and never a changed executable. For a Proton identity argv is the evidence, so an
    /// unreadable or empty command line is torn proof, not a contradiction.
    /// </summary>
    public static bool IsUnverifiableEvidence(SteamLaunchTarget target, ExternalProcessEntry row) =>
        row.Executable is null
        || (target.ProtonPrefix is not null && row.CommandLine is not { Count: > 0 }
            && !Matches(target, row.Executable));

    /// <summary>The accepted game identity and its exact argv evidence, if any.</summary>
    public static bool TryGetGame(SteamLaunchTarget target, ExternalProcessEntry row,
        out string gamePath, out string? windowsArgument)
    {
        windowsArgument = null;
        if (row.Executable is { } executable && Matches(target, executable)
            && !IsProtonWrapper(executable) && !IsSteamClient(executable))
        {
            gamePath = executable;
            return true;
        }

        gamePath = string.Empty;
        if (target.ProtonPrefix is null || row.Executable is null
            || IsProtonWrapper(row.Executable) || IsSteamClient(row.Executable) || IsShell(row.Executable))
        {
            return false;
        }

        return ProtonCommandLine.TryResolve(target, row.CommandLine, out gamePath, out windowsArgument);
    }

    /// <summary>
    /// Null when the group may be used for game-only stop. A group is eligible when it did not
    /// exist before this launch and every readable member is a game binary or a launch helper.
    /// A Steam client or any pre-existing member makes it shared/unverifiable. A live member
    /// whose executable the host cannot read is never assumed, never signalled, and reported as
    /// tolerated group evidence. If such a member outlives the game, it is identified as a
    /// Steam/Proton helper survivor only when parent evidence proves it: an observed parent that
    /// is the baseline Steam client, an attributed game identity, or an already identified
    /// helper. Without that parent evidence it remains an unidentified survivor requiring
    /// manual inspection.
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

            if (!IsGame(target, member) && !IsInfrastructure(member.Executable)
                && !IsShell(member.Executable))
            {
                return $"group {group} contains unrelated member pid={pid} exe={member.Executable}";
            }
        }

        return null;
    }
}
