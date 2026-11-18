using Playnite.Core;

namespace Playnite.Platform.Linux;

public sealed class LinuxPaths : PlaynitePaths
{
    public LinuxPaths(IEnvironment environment, IFileSystem fileSystem)
        : base(environment.Home, environment.ProgramDirectory,
            ApplicationRoot(environment, "XDG_DATA_HOME", ".local/share"),
            ApplicationRoot(environment, "XDG_CONFIG_HOME", ".config"),
            ApplicationRoot(environment, "XDG_CACHE_HOME", ".cache"),
            ApplicationRoot(environment, "XDG_STATE_HOME", ".local/state"),
            DocumentsDirectory(environment, fileSystem), SteamCandidates(environment))
    {
    }

    private static string BaseDirectory(IEnvironment environment, string variable, string fallback)
    {
        var value = environment.GetVariable(variable);
        return Paths.IsFullPath(value) ? value! : Path.Combine(environment.Home, fallback);
    }

    private static string ApplicationRoot(IEnvironment environment, string variable, string fallback) =>
        Path.Combine(BaseDirectory(environment, variable, fallback), ApplicationDirectoryName);

    // Same probes and order as SteamRootResolver.Candidates. The override is kept verbatim so a
    // relative or whitespace value reaches discovery and is reported invalid rather than dropped.
    private static IEnumerable<string> SteamCandidates(IEnvironment environment)
    {
        var root = environment.GetVariable("STEAM_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            yield return root;
        }

        yield return Path.Combine(environment.Home, ".steam", "steam");
        yield return Path.Combine(environment.Home, ".steam", "root");
        var homeLocal = Path.Combine(environment.Home, ".local", "share", "Steam");
        var xdg = environment.GetVariable("XDG_DATA_HOME");
        if (Paths.IsFullPath(xdg) && !Paths.AreEqual(Path.Combine(xdg!, "Steam"), homeLocal, "/"))
        {
            yield return Path.Combine(xdg!, "Steam");
        }

        yield return homeLocal;
        yield return Path.Combine(environment.Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
    }

    // user-dirs.dirs is data, never shell input. Only the documented $HOME expansion is allowed.
    // As in xdg-user-dirs and GLib, the last valid assignment wins.
    private static string DocumentsDirectory(IEnvironment environment, IFileSystem fileSystem)
    {
        var documents = Path.Combine(environment.Home, "Documents");
        var file = Path.Combine(BaseDirectory(environment, "XDG_CONFIG_HOME", ".config"), "user-dirs.dirs");
        if (!fileSystem.FileExists(file))
        {
            return documents;
        }

        foreach (var line in fileSystem.ReadAllText(file).Split('\n'))
        {
            var assignment = line.Trim();
            const string key = "XDG_DOCUMENTS_DIR=";
            if (!assignment.StartsWith(key, StringComparison.Ordinal))
            {
                continue;
            }

            var value = assignment[key.Length..];
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            {
                continue;
            }

            value = value[1..^1];
            if (value == "$HOME" || value.StartsWith("$HOME/", StringComparison.Ordinal))
            {
                value = environment.Home + value[5..];
            }

            if (Paths.IsFullPath(value) && !value.Contains('$') && !value.Contains('`') && !value.Contains('\\'))
            {
                documents = value;
            }
        }

        return documents;
    }
}
