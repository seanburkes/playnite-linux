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
        var paths = new LinuxPaths(new FixedHomeEnvironment(environment, home), NoDocumentsFileSystem.Instance);

        var configHome = BaseHome(paths.Config);
        var dataHome = BaseHome(paths.UserData);
        var cacheHome = BaseHome(paths.Cache);
        var stateHome = BaseHome(paths.State);
        RecordRejected(environment, "XDG_CONFIG_HOME", diagnostics);
        RecordRejected(environment, "XDG_DATA_HOME", diagnostics);
        RecordRejected(environment, "XDG_CACHE_HOME", diagnostics);
        RecordRejected(environment, "XDG_STATE_HOME", diagnostics);

        var state = paths.State;
        var runtimeValue = environment.GetVariable("XDG_RUNTIME_DIR");
        string runtime;
        if (Paths.IsFullPath(runtimeValue))
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

    private static string BaseHome(string applicationRoot) =>
        Path.GetDirectoryName(applicationRoot) ?? applicationRoot;

    private static void RecordRejected(IEnvironment environment, string variable, List<string> diagnostics)
    {
        var value = environment.GetVariable(variable);
        if (value is not null && !Paths.IsFullPath(value))
        {
            diagnostics.Add($"{variable} is not an absolute path ({value}); using the documented fallback.");
        }
    }

    private sealed class FixedHomeEnvironment : IEnvironment
    {
        private readonly IEnvironment inner;

        internal FixedHomeEnvironment(IEnvironment inner, string home)
        {
            this.inner = inner;
            Home = home;
        }

        public string Home { get; }
        public string CurrentDirectory => inner.CurrentDirectory;
        public string ProgramDirectory => inner.ProgramDirectory;
        public string? GetVariable(string name) => inner.GetVariable(name);
    }

    /// <summary>The shared resolver only needs to know whether user-dirs.dirs exists.</summary>
    private sealed class NoDocumentsFileSystem : IFileSystem
    {
        internal static readonly NoDocumentsFileSystem Instance = new();

        public bool FileExists(string path) => false;
        public bool DirectoryExists(string path) => false;
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public string ReadAllText(string path) => throw new FileNotFoundException("Not part of XDG base resolution.", path);
    }
}
