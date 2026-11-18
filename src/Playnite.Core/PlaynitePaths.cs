namespace Playnite.Core;

/// <summary>v10 Settings/PlaynitePaths.cs directory derivations, split across XDG roots.</summary>
public class PlaynitePaths : IPaths
{
    public const string ApplicationDirectoryName = "playnite";
    public const string ExtensionsDirName = "Extensions";
    public const string ExtensionsDataDirName = "ExtensionsData";
    public const string ThemesDirName = "Themes";
    public const string ConfigFileName = "config.json";
    public const string FullscreenConfigFileName = "fullscreenConfig.json";
    public const string WindowPositionsFileName = "windowPositions.json";
    public const string LocalizationsDirName = "Localization";

    public string Home { get; }
    public string Documents { get; }
    public string Program { get; }
    public string UserData { get; }
    public string Config { get; }
    public string Cache { get; }
    public string State { get; }
    public string Logs { get; }
    public IReadOnlyList<string> SteamRootCandidates { get; }

    public string ProgramPath => Program;
    public string ConfigRootPath => Config;
    public string DataCachePath => Cache;
    public string LocalizationsPath => Path.Combine(Program, LocalizationsDirName);
    public string ThemesProgramPath => Path.Combine(Program, ThemesDirName);
    public string ExtensionsProgramPath => Path.Combine(Program, ExtensionsDirName);
    public string ThemesUserDataPath => Path.Combine(UserData, ThemesDirName);
    public string ExtensionsUserDataPath => Path.Combine(UserData, ExtensionsDirName);
    public string ExtensionsDataPath => Path.Combine(UserData, ExtensionsDataDirName);
    public string ExtensionQueueFilePath => Path.Combine(Config, "extinstalls.json");
    public string AddonLicenseAgreementsFilePath => Path.Combine(Config, "licenseagreements.json");
    public string BrowserCachePath => Path.Combine(Cache, "browsercache");
    public string LogPath => Path.Combine(Logs, "playnite.log");
    public string ConfigFilePath => Path.Combine(Config, ConfigFileName);
    public string FullscreenConfigFilePath => Path.Combine(Config, FullscreenConfigFileName);
    public string WindowPositionsPath => Path.Combine(Config, WindowPositionsFileName);
    public string BackupConfigFilePath => Path.Combine(Config, "Backup", ConfigFileName);
    public string BackupFullscreenConfigFilePath => Path.Combine(Config, "Backup", FullscreenConfigFileName);
    public string BackupWindowPositionsPath => Path.Combine(Config, "Backup", WindowPositionsFileName);
    public string ImagesCachePath => Path.Combine(Cache, "images");
    public string IconsCachePath => Path.Combine(Cache, "icons");
    public string JitProfilesPath => Path.Combine(Cache, "JITProfiles");
    public string SafeStartupFlagFile => Path.Combine(State, "safestart.flag");
    public string BackupActionFile => Path.Combine(State, "backup.json");
    public string RestoreBackupActionFile => Path.Combine(State, "restoreBackup.json");

    public PlaynitePaths(string home, string program, string userData, string config, string cache,
        string state, string documents, IEnumerable<string> steamRootCandidates)
    {
        Home = RequireAbsolute(home);
        Program = RequireAbsolute(program);
        UserData = RequireAbsolute(userData);
        Config = RequireAbsolute(config);
        Cache = RequireAbsolute(cache);
        State = RequireAbsolute(state);
        Documents = RequireAbsolute(documents);
        Logs = Path.Combine(State, "logs");
        SteamRootCandidates = Array.AsReadOnly(steamRootCandidates.Select(NormalizeCandidate).Distinct(StringComparer.Ordinal).ToArray());
    }

    // A non-absolute candidate is the explicit override discovery must report as invalid; it is
    // never resolved against the working directory and never dropped here.
    private static string NormalizeCandidate(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("A nonempty path is required.", nameof(path));
        }

        return Paths.IsFullPath(path) ? Paths.Normalize(path, "/") : path;
    }

    private static string RequireAbsolute(string path)
    {
        if (!Paths.IsFullPath(path))
        {
            throw new ArgumentException("An absolute path is required.", nameof(path));
        }

        return Paths.Normalize(path, "/");
    }
}
