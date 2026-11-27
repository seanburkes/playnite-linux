using System.Globalization;
using System.Text;
using Playnite.Core.Models;
using Playnite.Core.Plugins;
using Playnite.Desktop.Plugins;
using Playnite.Platform.Sessions;

namespace Playnite.Desktop.Shell;

internal sealed record GameLaunchSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    bool UseGameMode,
    bool UseMangoHud,
    bool ParseFailed = false,
    string? RawArguments = null);

internal sealed record LaunchPlanResult(LaunchRequest? Request, string? Error);

internal sealed record PlayActionCreationResult(GameAction? Action, LaunchRequest? Request, string? Error);

/// <summary>
/// Pure launch argument parser and argv-vector planner for custom/native Linux games and
/// GameMode/MangoHud wrappers. Shell interpreters, shell symlinks, and env wrappers are rejected.
/// </summary>
internal static class GameLaunchPlanner
{
    private static readonly string[] ToolDirectories = ["/usr/bin", "/usr/local/bin", "/bin"];

    private static readonly HashSet<string> BlockedShellExecutables = new(StringComparer.Ordinal)
    {
        "sh", "bash", "dash", "zsh", "ksh", "mksh", "pdksh", "csh", "tcsh", "fish", "yash", "rbash", "busybox", "ash", "env",
    };

    public static bool TryParseArguments(string? raw, out IReadOnlyList<string> arguments, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            arguments = [];
            error = null;
            return true;
        }

        if (raw.Contains('\0', StringComparison.Ordinal))
        {
            arguments = [];
            error = "Launch arguments cannot contain NUL characters.";
            return false;
        }

        var parsed = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var quote = '\0';

        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (quote == '\'')
            {
                if (ch == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (quote == '"')
            {
                if (ch == '"')
                {
                    quote = '\0';
                }
                else if (ch == '\\')
                {
                    if (i + 1 >= raw.Length)
                    {
                        arguments = [];
                        error = "Launch arguments contain a trailing escape inside quotes.";
                        return false;
                    }

                    var next = raw[i + 1];
                    if (next is '"' or '\\')
                    {
                        current.Append(next);
                        i++;
                    }
                    else
                    {
                        current.Append(ch);
                    }
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (inToken)
                {
                    parsed.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                inToken = true;
                continue;
            }

            if (ch == '\\')
            {
                if (i + 1 >= raw.Length)
                {
                    arguments = [];
                    error = "Launch arguments cannot end with a bare backslash.";
                    return false;
                }

                current.Append(raw[++i]);
                inToken = true;
                continue;
            }

            current.Append(ch);
            inToken = true;
        }

        if (quote != '\0')
        {
            arguments = [];
            error = "Launch arguments have an unclosed quote.";
            return false;
        }

        if (inToken)
        {
            parsed.Add(current.ToString());
        }

        arguments = parsed;
        error = null;
        return true;
    }

    public static string FormatArguments(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var parts = new List<string>();
        foreach (var arg in arguments)
        {
            if (arg.Length == 0 || arg.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'' or '\\'))
            {
                var builder = new StringBuilder(arg.Length + 4);
                builder.Append('"');
                foreach (var ch in arg)
                {
                    if (ch is '"' or '\\')
                    {
                        builder.Append('\\');
                    }

                    builder.Append(ch);
                }

                builder.Append('"');
                parts.Add(builder.ToString());
            }
            else
            {
                parts.Add(arg);
            }
        }

        return string.Join(' ', parts);
    }

    public static string? ResolveToolPath(string toolName, Func<string, string?>? resolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (resolver is not null)
        {
            var resolved = resolver(toolName);
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }

        foreach (var dir in ToolDirectories)
        {
            var candidate = Path.Combine(dir, toolName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static PlayActionCreationResult CreatePlayAction(
        string? executablePath,
        string? arguments,
        string? workingDirectory,
        bool useGameMode,
        bool useMangoHud,
        Func<string, string?>? toolResolver = null)
    {
        var trimmedExe = executablePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedExe))
        {
            return new PlayActionCreationResult(null, null, "Executable path is required.");
        }

        if (!TryParseArguments(arguments, out var parsedArgs, out var argError))
        {
            return new PlayActionCreationResult(null, null, argError);
        }

        var trimmedWorkDir = workingDirectory?.Trim() ?? string.Empty;
        string? effectiveWorkDir = string.IsNullOrEmpty(trimmedWorkDir) ? null : trimmedWorkDir;

        var innerValidationError = ValidateExecutableAndTarget(trimmedExe, parsedArgs, effectiveWorkDir);
        if (innerValidationError is not null)
        {
            return new PlayActionCreationResult(null, null, innerValidationError);
        }

        var command = new List<string>(parsedArgs.Count + 3) { trimmedExe };
        command.AddRange(parsedArgs);

        if (useMangoHud)
        {
            var mangoHud = ResolveToolPath("mangohud", toolResolver);
            if (mangoHud is null)
            {
                return new PlayActionCreationResult(
                    null,
                    null,
                    "MangoHud ('mangohud') was not found in /usr/bin, /usr/local/bin, or /bin.");
            }

            command.Insert(0, mangoHud);
        }

        if (useGameMode)
        {
            var gameMode = ResolveToolPath("gamemoderun", toolResolver);
            if (gameMode is null)
            {
                return new PlayActionCreationResult(
                    null,
                    null,
                    "GameMode ('gamemoderun') was not found in /usr/bin, /usr/local/bin, or /bin.");
            }

            command.Insert(0, gameMode);
        }

        LaunchRequest request;
        try
        {
            request = new LaunchRequest(command[0], command.Skip(1), effectiveWorkDir);
        }
        catch (ArgumentException ex)
        {
            return new PlayActionCreationResult(null, null, ex.Message);
        }

        var action = new GameAction
        {
            Type = GameActionType.File,
            IsPlayAction = true,
            Name = "Play",
            Path = request.Executable,
            Arguments = FormatArguments(request.Arguments),
            WorkingDir = trimmedWorkDir,
        };
        return new PlayActionCreationResult(action, request, null);
    }

    public static GameLaunchSpec InspectPlayAction(GameAction? action)
    {
        if (action is null || action.Type != GameActionType.File || string.IsNullOrWhiteSpace(action.Path))
        {
            return new GameLaunchSpec(string.Empty, [], null, false, false);
        }

        var rawArgs = action.Arguments;
        var workDir = string.IsNullOrWhiteSpace(action.WorkingDir) ? null : action.WorkingDir.Trim();
        if (!TryParseArguments(rawArgs, out var parsedArgs, out _))
        {
            return new GameLaunchSpec(
                action.Path.Trim(),
                string.IsNullOrWhiteSpace(rawArgs) ? [] : [rawArgs.Trim()],
                workDir,
                UseGameMode: false,
                UseMangoHud: false,
                ParseFailed: true,
                RawArguments: rawArgs?.Trim());
        }

        var tokens = new List<string>(parsedArgs.Count + 1) { action.Path.Trim() };
        tokens.AddRange(parsedArgs);

        var useGameMode = false;
        var useMangoHud = false;

        if (CanPeelWrapper(tokens, "gamemoderun"))
        {
            useGameMode = true;
            tokens.RemoveAt(0);
        }

        if (CanPeelWrapper(tokens, "mangohud"))
        {
            useMangoHud = true;
            tokens.RemoveAt(0);
        }

        return new GameLaunchSpec(
            tokens[0],
            tokens.Skip(1).ToArray(),
            workDir,
            useGameMode,
            useMangoHud);
    }

    public static GameAction? FindFilePlayAction(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return game.GameActions?.FirstOrDefault(action =>
            action is not null
            && action.IsPlayAction
            && action.Type == GameActionType.File
            && !string.IsNullOrWhiteSpace(action.Path));
    }

    public static LaunchPlanResult TryBuildLaunchRequest(Game game, Func<string, string?>? toolResolver = null) =>
        TryBuildLaunchRequest(game, toolResolver, DesktopPluginHostFactory.DefaultBuiltInHost);

    internal static LaunchPlanResult TryBuildLaunchRequest(
        Game game,
        Func<string, string?>? toolResolver,
        PluginHost? pluginHost)
    {
        ArgumentNullException.ThrowIfNull(game);
        var host = pluginHost ?? DesktopPluginHostFactory.DefaultBuiltInHost;
        if (game.PluginId != Guid.Empty && host.TryGetLaunchProvider(game.PluginId, out var launchProvider))
        {
            PluginLaunchSpec pluginSpec;
            try
            {
                pluginSpec = launchProvider.ResolveLaunch(game, toolResolver);
            }
            catch (Exception exception)
            {
                return new LaunchPlanResult(null, $"Plugin launch resolution failed: {exception.Message}");
            }

            if (pluginSpec.Error is not null)
            {
                return new LaunchPlanResult(null, pluginSpec.Error);
            }

            if (pluginSpec.Kind == PluginLaunchKind.TrackedProcess)
            {
                if (string.IsNullOrWhiteSpace(pluginSpec.Executable))
                {
                    return new LaunchPlanResult(null, $"'{game.Name}' plugin launch did not specify an executable.");
                }

                var args = pluginSpec.Arguments ?? [];
                var validationError = ValidateExecutableAndTarget(pluginSpec.Executable.Trim(), args, pluginSpec.WorkingDirectory);
                if (validationError is not null)
                {
                    return new LaunchPlanResult(null, validationError);
                }

                try
                {
                    return new LaunchPlanResult(
                        new LaunchRequest(pluginSpec.Executable.Trim(), args, pluginSpec.WorkingDirectory),
                        null);
                }
                catch (ArgumentException ex)
                {
                    return new LaunchPlanResult(null, ex.Message);
                }
            }

            var isCloudStreaming = host.TryGetSource(game.PluginId, out var sourceInfo) && sourceInfo.IsCloudStreaming;
            if (pluginSpec.Kind == PluginLaunchKind.BrowserHandoff || isCloudStreaming)
            {
                return new LaunchPlanResult(null, $"'{game.Name}' uses {pluginSpec.Kind} and cannot run as a tracked process.");
            }

            if (pluginSpec.Kind == PluginLaunchKind.UriHandoff && FindFilePlayAction(game) is null)
            {
                return new LaunchPlanResult(null, $"'{game.Name}' uses {pluginSpec.Kind} and cannot run as a tracked process.");
            }
        }

        var action = FindFilePlayAction(game);
        if (action is null)
        {
            return new LaunchPlanResult(null, $"'{game.Name}' has no executable play action configured.");
        }

        if (!TryParseArguments(action.Arguments, out var parsedArgs, out var argError))
        {
            return new LaunchPlanResult(null, argError);
        }

        var spec = InspectPlayAction(action);
        string? workingDir = null;
        if (!string.IsNullOrWhiteSpace(action.WorkingDir))
        {
            workingDir = action.WorkingDir.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(game.InstallDirectory)
            && Path.IsPathFullyQualified(game.InstallDirectory)
            && Directory.Exists(game.InstallDirectory))
        {
            workingDir = game.InstallDirectory;
        }
        else if (Path.GetDirectoryName(spec.Executable) is { Length: > 0 } parent && Directory.Exists(parent))
        {
            workingDir = parent;
        }

        var innerValidationError = ValidateExecutableAndTarget(spec.Executable, spec.Arguments, workingDir);
        if (innerValidationError is not null)
        {
            return new LaunchPlanResult(null, innerValidationError);
        }

        var outerValidationError = ValidateExecutableSafety(action.Path.Trim());
        if (outerValidationError is not null)
        {
            return new LaunchPlanResult(null, outerValidationError);
        }

        try
        {
            var request = new LaunchRequest(action.Path.Trim(), parsedArgs, workingDir);
            return new LaunchPlanResult(request, null);
        }
        catch (ArgumentException ex)
        {
            return new LaunchPlanResult(null, ex.Message);
        }
    }

    public static string DescribeLaunchTarget(Game game, bool isSteam, uint? steamPlayAppId) =>
        DescribeLaunchTarget(game, isSteam ? StoreCatalog.SteamPluginId : Guid.Empty, steamPlayAppId);

    public static string DescribeLaunchTarget(Game game, Guid pluginId, uint? steamPlayAppId) =>
        DescribeLaunchTarget(game, pluginId, steamPlayAppId, null);

    internal static string DescribeLaunchTarget(Game game, Guid pluginId, uint? steamPlayAppId, PluginHost? pluginHost)
    {
        ArgumentNullException.ThrowIfNull(game);
        var host = pluginHost ?? DesktopPluginHostFactory.DefaultBuiltInHost;
        PluginLaunchSpec? pluginSpec = null;
        if (pluginId != Guid.Empty && host.TryGetLaunchProvider(pluginId, out var launchProvider))
        {
            try
            {
                pluginSpec = launchProvider.DescribeLaunch(game);
                if (pluginSpec.Kind is PluginLaunchKind.TrackedProcess or PluginLaunchKind.BrowserHandoff
                    && (pluginSpec.CanLaunch || pluginSpec.Error is not null))
                {
                    return pluginSpec.TargetDisplay;
                }
            }
            catch (Exception)
            {
                return "Plugin error";
            }
        }

        var action = FindFilePlayAction(game);
        if (action is not null)
        {
            var spec = InspectPlayAction(action);
            if (!spec.UseGameMode && !spec.UseMangoHud)
            {
                return spec.Executable;
            }

            var tags = (spec.UseGameMode, spec.UseMangoHud) switch
            {
                (true, true) => "GameMode + MangoHud",
                (true, false) => "GameMode",
                _ => "MangoHud",
            };
            return $"[{tags}] {spec.Executable}";
        }

        if (pluginSpec is not null && (pluginSpec.CanLaunch || pluginSpec.Error is not null))
        {
            return pluginSpec.TargetDisplay;
        }

        return "Not configured";
    }

    private static string? ValidateExecutableAndTarget(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory)
    {
        try
        {
            _ = new LaunchRequest(executable, arguments, workingDirectory);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        var exeError = ValidateExecutableSafety(executable);
        if (exeError is not null)
        {
            return exeError;
        }

        if (IsToolExecutable(executable, "gamemoderun") || IsToolExecutable(executable, "mangohud"))
        {
            if (arguments.Count == 0 || !Path.IsPathFullyQualified(arguments[0]))
            {
                return "Wrapped launch actions must specify an absolute target executable.";
            }

            return ValidateExecutableAndTarget(arguments[0], arguments.Skip(1).ToArray(), workingDirectory);
        }

        return null;
    }

    private static string? ValidateExecutableSafety(string executable)
    {
        var leaf = Path.GetFileName(executable);
        if (BlockedShellExecutables.Contains(leaf))
        {
            return $"'{leaf}' cannot be used as a game executable; select the game binary directly.";
        }

        try
        {
            if (File.Exists(executable))
            {
                var target = new FileInfo(executable).ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    var targetLeaf = Path.GetFileName(target.FullName);
                    if (BlockedShellExecutables.Contains(targetLeaf))
                    {
                        return $"Executable resolves to blocked interpreter '{targetLeaf}'; select the game binary directly.";
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Executable path could not be validated ({exception.Message}).";
        }

        return null;
    }

    private static bool CanPeelWrapper(List<string> tokens, string wrapperName) =>
        tokens.Count >= 2
        && IsToolExecutable(tokens[0], wrapperName)
        && !string.IsNullOrWhiteSpace(tokens[1])
        && !tokens[1].StartsWith('-')
        && Path.IsPathFullyQualified(tokens[1]);

    private static bool IsToolExecutable(string path, string toolName) =>
        string.Equals(Path.GetFileName(path), toolName, StringComparison.Ordinal);
}
