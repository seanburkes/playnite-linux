using System.Text.Json;
using Playnite.Core.Tests.TestDoubles;
using Playnite.Platform.Linux;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class LinuxPathsTests
{
    [Theory]
    [InlineData("XDG_DATA_HOME", "UserData", ".local/share")]
    [InlineData("XDG_CONFIG_HOME", "Config", ".config")]
    [InlineData("XDG_CACHE_HOME", "Cache", ".cache")]
    [InlineData("XDG_STATE_HOME", "State", ".local/state")]
    public void XdgBaseDirectoriesIgnoreUnsetEmptyAndRelativeValues(string variable, string property, string fallback)
    {
        foreach (var value in new string?[] { null, "", " ", "\t", "relative", "relative/", "./relative", "../relative", "~/relative", "~" })
        {
            var environment = new MemoryEnvironment();
            environment.Variables[variable] = value;
            var paths = new LinuxPaths(environment, new MemoryFileSystem());
            Assert.Equal($"/home/test-user/{fallback}/playnite", typeof(IPaths).GetProperty(property)!.GetValue(paths));
        }
    }

    [Theory]
    [InlineData("XDG_DATA_HOME", "UserData")]
    [InlineData("XDG_CONFIG_HOME", "Config")]
    [InlineData("XDG_CACHE_HOME", "Cache")]
    [InlineData("XDG_STATE_HOME", "State")]
    public void AbsoluteXdgValueWinsAndIsCaptured(string variable, string property)
    {
        var environment = new MemoryEnvironment();
        environment.Variables[variable] = "/custom/../space name/これはユニコード/";
        var paths = new LinuxPaths(environment, new MemoryFileSystem());
        environment.Variables[variable] = "/changed";
        Assert.Equal("/space name/これはユニコード/playnite", typeof(IPaths).GetProperty(property)!.GetValue(paths));
    }

    [Fact]
    public void LegacyDerivedApplicationPathsMatchGoldenWithoutCreatingAnything()
    {
        var fileSystem = new MemoryFileSystem();
        var paths = new LinuxPaths(new MemoryEnvironment(), fileSystem);
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "application.json")));
        foreach (var item in golden.RootElement.GetProperty("paths").EnumerateObject())
        {
            Assert.Equal(item.Value.GetString(), typeof(PlaynitePaths).GetProperty(item.Name)!.GetValue(paths));
        }

        Assert.False(fileSystem.DirectoryExists(paths.UserData));
        Assert.False(fileSystem.DirectoryExists(paths.Config));
        Assert.False(fileSystem.DirectoryExists(paths.Cache));
        Assert.False(fileSystem.DirectoryExists(paths.State));
        Assert.Equal(paths.State + "/logs", paths.Logs);
        Assert.Equal(paths.Logs + "/playnite.log", paths.LogPath);
    }

    [Fact]
    public void SteamRootCandidatesFollowSpecifiedOrderWithoutProbingHost()
    {
        var environment = new MemoryEnvironment();
        environment.Variables["STEAM_ROOT"] = "/custom/Steam/";
        var paths = new LinuxPaths(environment, new MemoryFileSystem());
        Assert.Equal(new[] { "/custom/Steam", "/home/test-user/.steam/steam", "/home/test-user/.local/share/Steam",
            "/home/test-user/.var/app/com.valvesoftware.Steam/.local/share/Steam" }, paths.SteamRootCandidates);
        environment.Variables["STEAM_ROOT"] = "relative";
        Assert.Equal(3, new LinuxPaths(environment, new MemoryFileSystem()).SteamRootCandidates.Count);
        environment.Variables["STEAM_ROOT"] = "/home/test-user/.steam/steam";
        Assert.Equal(3, new LinuxPaths(environment, new MemoryFileSystem()).SteamRootCandidates.Count);
    }

    [Theory]
    [InlineData("XDG_DOCUMENTS_DIR=\"$HOME/Documents on disk\"", "/home/test-user/Documents on disk")]
    [InlineData("XDG_DOCUMENTS_DIR=\"/disk/文書\"", "/disk/文書")]
    [InlineData("XDG_DOCUMENTS_DIR=\"$HOME\"", "/home/test-user")]
    [InlineData("XDG_DOCUMENTS_DIR=\"relative\"", "/home/test-user/Documents")]
    [InlineData("XDG_DOCUMENTS_DIR=\"$(touch /danger)\"", "/home/test-user/Documents")]
    [InlineData("# XDG_DOCUMENTS_DIR=\"/ignored\"", "/home/test-user/Documents")]
    public void DocumentsUsesUserDirsDataWithoutExecutingShell(string content, string expected)
    {
        var environment = new MemoryEnvironment();
        environment.Variables["XDG_CONFIG_HOME"] = "/configuration";
        var fileSystem = new MemoryFileSystem();
        fileSystem.AddFile("/configuration/user-dirs.dirs", content);
        Assert.Equal(expected, new LinuxPaths(environment, fileSystem).Documents);
    }

    [Fact]
    public void EachXdgOverrideOnlyMovesItsOwnRoot()
    {
        var environment = new MemoryEnvironment();
        environment.Variables["XDG_CACHE_HOME"] = "/custom-cache";
        var paths = new LinuxPaths(environment, new MemoryFileSystem());
        Assert.Equal("/custom-cache/playnite", paths.Cache);
        Assert.Equal("/home/test-user/.local/share/playnite", paths.UserData);
        Assert.Equal("/home/test-user/.config/playnite", paths.Config);
        Assert.Equal("/home/test-user/.local/state/playnite", paths.State);
        Assert.Equal("/home/test-user/.local/state/playnite/logs", paths.Logs);
    }

    [Fact]
    public void RootHomeAndRootOverridesStayAbsoluteAndNormalized()
    {
        var environment = new MemoryEnvironment { Home = "/" };
        environment.Variables["XDG_STATE_HOME"] = "/";
        var paths = new LinuxPaths(environment, new MemoryFileSystem());
        Assert.Equal("/", paths.Home);
        Assert.Equal("/.local/share/playnite", paths.UserData);
        Assert.Equal("/.config/playnite", paths.Config);
        Assert.Equal("/.cache/playnite", paths.Cache);
        Assert.Equal("/playnite", paths.State);
        Assert.Equal("/playnite/logs", paths.Logs);
        Assert.Equal("/Documents", paths.Documents);
    }

    [Fact]
    public void SubdirectoriesAreDerivedFromTheResolvedXdgRoots()
    {
        var environment = new MemoryEnvironment();
        environment.Variables["XDG_DATA_HOME"] = "/d";
        environment.Variables["XDG_CONFIG_HOME"] = "/c";
        environment.Variables["XDG_CACHE_HOME"] = "/k";
        environment.Variables["XDG_STATE_HOME"] = "/s";
        var paths = new LinuxPaths(environment, new MemoryFileSystem());
        Assert.Equal("/d/playnite/Extensions", paths.ExtensionsUserDataPath);
        Assert.Equal("/d/playnite/ExtensionsData", paths.ExtensionsDataPath);
        Assert.Equal("/d/playnite/Themes", paths.ThemesUserDataPath);
        Assert.Equal("/c/playnite/config.json", paths.ConfigFilePath);
        Assert.Equal("/c/playnite/Backup/config.json", paths.BackupConfigFilePath);
        Assert.Equal("/k/playnite/images", paths.ImagesCachePath);
        Assert.Equal("/k/playnite/JITProfiles", paths.JitProfilesPath);
        Assert.Equal("/s/playnite/safestart.flag", paths.SafeStartupFlagFile);
        Assert.Equal("/s/playnite/logs/playnite.log", paths.LogPath);
        Assert.Equal("/opt/playnite/Localization", paths.LocalizationsPath);
    }

    [Fact]
    public void DocumentsFallsBackToHomeDocumentsWhenUserDirsIsAbsent()
    {
        Assert.Equal("/home/test-user/Documents", new LinuxPaths(new MemoryEnvironment(), new MemoryFileSystem()).Documents);
    }

    [Fact]
    public void DocumentsIgnoresRelativeConfigOverrideWhenLocatingUserDirs()
    {
        var environment = new MemoryEnvironment();
        environment.Variables["XDG_CONFIG_HOME"] = "relative-config";
        var fileSystem = new MemoryFileSystem();
        fileSystem.AddFile("/home/test-user/.config/user-dirs.dirs", "XDG_DOCUMENTS_DIR=\"/from-home-config\"");
        fileSystem.AddFile("/working/relative-config/user-dirs.dirs", "XDG_DOCUMENTS_DIR=\"/from-cwd\"");
        Assert.Equal("/from-home-config", new LinuxPaths(environment, fileSystem).Documents);
    }

    [Theory]
    [InlineData("XDG_DOCUMENTS_DIR=\"/first\"\nXDG_DOCUMENTS_DIR=\"/second\"", "/second")]
    [InlineData("XDG_DOCUMENTS_DIR=\"/first\"\nXDG_DOCUMENTS_DIR=\"relative\"", "/first")]
    [InlineData("XDG_DOCUMENTS_DIR=\"/first\"\r\nXDG_DOWNLOAD_DIR=\"/download\"\r\n", "/first")]
    public void DocumentsUsesTheLastValidUserDirsAssignment(string content, string expected)
    {
        var fileSystem = new MemoryFileSystem();
        fileSystem.AddFile("/home/test-user/.config/user-dirs.dirs", content);
        Assert.Equal(expected, new LinuxPaths(new MemoryEnvironment(), fileSystem).Documents);
    }

    [Theory]
    [InlineData("XDG_DOCUMENTS_DIR=\"$HOME/Documents/../Docs/\"", "/home/test-user/Docs")]
    [InlineData("XDG_DOCUMENTS_DIR=\"/disk/sub/../文書//\"", "/disk/文書")]
    [InlineData("XDG_DOCUMENTS_DIR=\"$HOMEFOO/Documents\"", "/home/test-user/Documents")]
    [InlineData("XDG_DOCUMENTS_DIR=\"$HOME/Sub\\Dir\"", "/home/test-user/Documents")]
    [InlineData("XDG_DOCUMENTS_DIR=\"$HOME/Sub`Dir\"", "/home/test-user/Documents")]
    public void DocumentsExpandsOnlyHomeAndNormalizesTheAcceptedValue(string content, string expected)
    {
        var fileSystem = new MemoryFileSystem();
        fileSystem.AddFile("/home/test-user/.config/user-dirs.dirs", content);
        Assert.Equal(expected, new LinuxPaths(new MemoryEnvironment(), fileSystem).Documents);
    }

    [Fact]
    public void RejectsRelativeHomeRatherThanConsultingDeveloperHome()
    {
        Assert.Throws<ArgumentException>(() => new LinuxPaths(new MemoryEnvironment { Home = "relative" }, new MemoryFileSystem()));
    }

    [Fact]
    public void UserDirsParserFuzzKeepsAllResultsAbsoluteWithoutCreatingDirectories()
    {
        var random = new Random(3001);
        const string alphabet = "abc/.$HOME=\"\\`# \n\r\0";
        for (int i = 0; i < 1000; i++)
        {
            var content = new string(Enumerable.Range(0, random.Next(80))
                .Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var fileSystem = new MemoryFileSystem();
            fileSystem.AddFile("/home/test-user/.config/user-dirs.dirs", i % 2 == 0
                ? "XDG_DOCUMENTS_DIR=\"" + content + "\"" : content);
            var paths = new LinuxPaths(new MemoryEnvironment(), fileSystem);
            Assert.True(Paths.IsFullPath(paths.Documents));
            Assert.False(fileSystem.DirectoryExists(paths.UserData));
        }
    }
}
