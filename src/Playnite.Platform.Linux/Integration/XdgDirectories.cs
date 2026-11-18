using Playnite.Core;

namespace Playnite.Platform.Linux.Integration;

/// <summary>
/// XDG base directories used by the Linux integration services. Resolution is pure: it reads
/// the captured environment only and never creates a directory.
/// </summary>
public sealed class XdgDirectories
{
    public const string ApplicationDirectoryName = PlaynitePaths.ApplicationDirectoryName;

    private XdgDirectories(
        string configHome,
        string dataHome,
        string cacheHome,
        string stateHome,
        string runtime,
        IReadOnlyList<string> diagnostics)
    {
        ConfigHome = configHome;
        DataHome = dataHome;
        CacheHome = cacheHome;
        StateHome = stateHome;
        Config = Path.Combine(configHome, ApplicationDirectoryName);
        Data = Path.Combine(dataHome, ApplicationDirectoryName);
        Cache = Path.Combine(cacheHome, ApplicationDirectoryName);
        State = Path.Combine(stateHome, ApplicationDirectoryName);
        Runtime = runtime;
        Diagnostics = diagnostics;
    }

    public string ConfigHome { get; }
    public string DataHome { get; }
    public string CacheHome { get; }
    public string StateHome { get; }

    /// <summary><c>$XDG_CONFIG_HOME/playnite</c>.</summary>
    public string Config { get; }

    /// <summary><c>$XDG_DATA_HOME/playnite</c>.</summary>
    public string Data { get; }

    /// <summary><c>$XDG_CACHE_HOME/playnite</c>.</summary>
    public string Cache { get; }

    /// <summary><c>$XDG_STATE_HOME/playnite</c>.</summary>
    public string State { get; }

    /// <summary>
    /// <c>$XDG_RUNTIME_DIR/playnite</c>, or <c>State/runtime</c> when XDG_RUNTIME_DIR is unset or
    /// not an absolute path. A runtime root must be per-user and disposable; the fallback is
    /// persistent but still private to the user.
    /// </summary>
    public string Runtime { get; }

    /// <summary>Fallbacks and rejected values, recorded for startup diagnostics.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>
    /// Resolves the directories with the documented precedence:
    /// <list type="bullet">
    /// <item>config: non-empty absolute <c>XDG_CONFIG_HOME</c>, else <c>HOME/.config</c>;</item>
    /// <item>data: non-empty absolute <c>XDG_DATA_HOME</c>, else <c>HOME/.local/share</c>;</item>
    /// <item>cache: non-empty absolute <c>XDG_CACHE_HOME</c>, else <c>HOME/.cache</c>;</item>
    /// <item>state: non-empty absolute <c>XDG_STATE_HOME</c>, else <c>HOME/.local/state</c>;</item>
    /// <item>runtime: non-empty absolute <c>XDG_RUNTIME_DIR</c>, else <c>State/runtime</c>.</item>
    /// </list>
    /// Empty, whitespace, relative and tilde-prefixed values are rejected and fall back.
    /// The application directory name is appended to every base except the runtime fallback,
    /// which already descends from State.
    /// </summary>
    public static XdgDirectories Resolve(IEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var diagnostics = new List<string>();
        var home = ResolveHome(environment, diagnostics);

        var configHome = BaseDirectory(environment, "XDG_CONFIG_HOME", home, ".config", diagnostics);
        var dataHome = BaseDirectory(environment, "XDG_DATA_HOME", home, ".local/share", diagnostics);
        var cacheHome = BaseDirectory(environment, "XDG_CACHE_HOME", home, ".cache", diagnostics);
        var stateHome = BaseDirectory(environment, "XDG_STATE_HOME", home, ".local/state", diagnostics);

        var state = Path.Combine(stateHome, ApplicationDirectoryName);
        var runtimeValue = environment.GetVariable("XDG_RUNTIME_DIR");
        string runtime;
        if (LinuxPaths.IsUsableBase(runtimeValue))
        {
            runtime = Path.Combine(Paths.Normalize(runtimeValue!, "/"), ApplicationDirectoryName);
        }
        else
        {
            runtime = Path.Combine(state, "runtime");
            diagnostics.Add(runtimeValue is null or ""
                ? "XDG_RUNTIME_DIR is unset; using the State directory for the single-instance runtime."
                : $"XDG_RUNTIME_DIR is not an absolute path ({runtimeValue}); using the State directory for the single-instance runtime.");
        }

        return new XdgDirectories(configHome, dataHome, cacheHome, stateHome, runtime, diagnostics.AsReadOnly());
    }

    /// <summary>
    /// Creates the runtime directory on demand and keeps it private to the user (0700). An
    /// existing world/group-accessible directory is tightened when this process owns it.
    /// Failures return a diagnostic instead of escaping into startup; no other XDG directory is
    /// created.
    /// </summary>
    public string? EnsureRuntimeDirectory()
    {
        try
        {
            Directory.CreateDirectory(Runtime);
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                var mode = File.GetUnixFileMode(Runtime);
                if (mode != privateMode)
                {
                    File.SetUnixFileMode(Runtime, privateMode);
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return $"Cannot create the runtime directory {Runtime}: {exception.Message}";
        }
    }

    private static string ResolveHome(IEnvironment environment, List<string> diagnostics)
    {
        if (Paths.IsFullPath(environment.Home))
        {
            return Paths.Normalize(environment.Home, "/");
        }

        var fallback = Paths.IsFullPath(environment.CurrentDirectory)
            ? Paths.Normalize(environment.CurrentDirectory, "/")
            : "/";
        diagnostics.Add($"HOME is not an absolute path ({environment.Home}); using {fallback} for XDG resolution.");
        return fallback;
    }

    private static string BaseDirectory(
        IEnvironment environment,
        string variable,
        string home,
        string fallback,
        List<string> diagnostics)
    {
        var value = environment.GetVariable(variable);
        if (LinuxPaths.IsUsableBase(value))
        {
            return Paths.Normalize(value!, "/");
        }

        if (value is not null)
        {
            diagnostics.Add($"{variable} is not an absolute path ({value}); using the fallback under {home}.");
        }

        return Path.Combine(home, fallback);
    }
}
