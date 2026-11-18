using Playnite.Core.Tests.TestDoubles;
using Playnite.Platform.Integration;
using Playnite.Platform.Linux.Integration;
using Xunit;

namespace Playnite.Core.Tests.Integration;

public sealed class ProtocolHandlerTests
{
    [Fact]
    public async Task RegistrationWritesADesktopEntryAndADefaultApplicationWithoutAnyChildProcess()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var environment = new MemoryEnvironment { Home = temporary.Path };
        environment.Variables["XDG_DATA_HOME"] = temporary.Path + "/data";
        environment.Variables["XDG_CONFIG_HOME"] = temporary.Path + "/config";
        var directories = XdgDirectories.Resolve(environment);
        var fileSystem = new RecordingFileSystem();

        var handler = new PlayniteProtocolHandler(directories, "/opt/Playnite/playnite", environment, fileSystem);
        var result = await handler.RegisterAsync();

        Assert.True(result.IsRegistered, result.Diagnostic);
        Assert.Equal("playnite", handler.Scheme);
        Assert.Equal(temporary.Path + "/data/applications/playnite-handler.desktop", handler.DesktopEntryPath);
        Assert.Equal(temporary.Path + "/config/mimeapps.list", handler.MimeAppsPath);
        var entry = fileSystem.Files[handler.DesktopEntryPath];
        Assert.Contains("[Desktop Entry]", entry, StringComparison.Ordinal);
        Assert.Contains("Exec=\"/opt/Playnite/playnite\" --uridata %u", entry, StringComparison.Ordinal);
        Assert.Contains("MimeType=x-scheme-handler/playnite;", entry, StringComparison.Ordinal);
        Assert.Contains("NoDisplay=true", entry, StringComparison.Ordinal);
        Assert.Equal(
            "[Default Applications]\nx-scheme-handler/playnite=playnite-handler.desktop\n",
            fileSystem.Files[handler.MimeAppsPath]);
    }

    [Fact]
    public async Task RegistrationPreservesOtherMimeAppsSectionsAndEntries()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var environment = new MemoryEnvironment { Home = temporary.Path };
        environment.Variables["XDG_CONFIG_HOME"] = temporary.Path + "/config";
        var directories = XdgDirectories.Resolve(environment);
        var fileSystem = new RecordingFileSystem();
        fileSystem.Files[temporary.Path + "/config/mimeapps.list"] = string.Join('\n',
            "[Default Applications]",
            "x-scheme-handler/playnite=old-handler.desktop",
            "text/plain=editor.desktop",
            string.Empty,
            "[Added Associations]",
            "image/png=viewer.desktop",
            string.Empty);

        var handler = new PlayniteProtocolHandler(directories, "/opt/playnite", environment, fileSystem);
        var result = await handler.RegisterAsync();

        Assert.True(result.IsRegistered, result.Diagnostic);
        Assert.Equal(string.Join('\n',
            "[Default Applications]",
            "x-scheme-handler/playnite=playnite-handler.desktop",
            "text/plain=editor.desktop",
            string.Empty,
            "[Added Associations]",
            "image/png=viewer.desktop",
            string.Empty), fileSystem.Files[handler.MimeAppsPath]);
    }

    [Fact]
    public async Task ExistingIdenticalFilesAreNotRewritten()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var environment = new MemoryEnvironment { Home = temporary.Path };
        var directories = XdgDirectories.Resolve(environment);
        var fileSystem = new RecordingFileSystem();
        var handler = new PlayniteProtocolHandler(directories, "/opt/Playnite/playnite", environment, fileSystem);
        await handler.RegisterAsync();
        Assert.Equal(2, fileSystem.Writes);

        await handler.RegisterAsync();

        Assert.Equal(2, fileSystem.Writes);
    }

    [Fact]
    public async Task ExecutablePathIsQuotedAsDesktopEntryData()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var environment = new MemoryEnvironment { Home = temporary.Path };
        var directories = XdgDirectories.Resolve(environment);
        var fileSystem = new RecordingFileSystem();
        var handler = new PlayniteProtocolHandler(
            directories,
            "/opt/Play nite/$bin/\"play%nite\"",
            environment,
            fileSystem);

        await handler.RegisterAsync();

        var entry = fileSystem.Files[handler.DesktopEntryPath];
        Assert.Contains(
            "Exec=\"/opt/Play nite/\\$bin/\\\"play%%nite\\\"\" --uridata %u",
            entry,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FLATPAK_ID", "com.example.Playnite")]
    [InlineData("SNAP", "/snap/playnite/1")]
    [InlineData("SNAP_NAME", "playnite")]
    public async Task SandboxedRunsSkipRegistrationWithoutWriting(string variable, string value)
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var environment = new MemoryEnvironment { Home = temporary.Path };
        environment.Variables[variable] = value;
        var directories = XdgDirectories.Resolve(environment);
        var fileSystem = new RecordingFileSystem();
        var handler = new PlayniteProtocolHandler(directories, "/opt/playnite", environment, fileSystem);

        var result = await handler.RegisterAsync();

        Assert.Equal(ProtocolRegistrationStatus.Skipped, result.Status);
        Assert.Contains("host desktop", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(fileSystem.Files);
    }

    [Fact]
    public async Task UnwritableDataDirectoryDegradesToFailedWithoutThrowing()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var environment = new MemoryEnvironment { Home = temporary.Path };
        var directories = XdgDirectories.Resolve(environment);
        var fileSystem = new RecordingFileSystem { FailWrites = true };
        var handler = new PlayniteProtocolHandler(directories, "/opt/playnite", environment, fileSystem);

        var result = await handler.RegisterAsync();

        Assert.Equal(ProtocolRegistrationStatus.Failed, result.Status);
        Assert.Contains("Cannot register playnite://", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelativeExecutableIsRejectedBeforeAnyWrite()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var environment = new MemoryEnvironment { Home = temporary.Path };
        var directories = XdgDirectories.Resolve(environment);
        var fileSystem = new RecordingFileSystem();
        var handler = new PlayniteProtocolHandler(directories, "playnite", environment, fileSystem);

        var result = await handler.RegisterAsync();

        Assert.Equal(ProtocolRegistrationStatus.Failed, result.Status);
        Assert.Contains("not absolute", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(fileSystem.Files);
    }

    [Theory]
    [InlineData("", "[Default Applications]\nx-scheme-handler/playnite=playnite-handler.desktop\n")]
    [InlineData("[Added Associations]\nimage/png=viewer.desktop\n",
        "[Added Associations]\nimage/png=viewer.desktop\n\n[Default Applications]\nx-scheme-handler/playnite=playnite-handler.desktop\n")]
    [InlineData("[Default Applications]\ntext/plain=editor.desktop",
        "[Default Applications]\nx-scheme-handler/playnite=playnite-handler.desktop\ntext/plain=editor.desktop\n")]
    public void MimeAppsUpdateKeepsUnrelatedContent(string original, string expected)
    {
        Assert.Equal(expected, PlayniteProtocolHandler.UpdateMimeApps(original));
        Assert.Equal(expected, PlayniteProtocolHandler.UpdateMimeApps(expected));
    }

    private sealed class RecordingFileSystem : IProtocolFileSystem
    {
        internal Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        internal int Writes { get; private set; }
        internal bool FailWrites { get; init; }

        public bool FileExists(string path) => Files.ContainsKey(path);

        public void CreateDirectory(string path)
        {
        }

        public string ReadAllText(string path) => Files[path];

        public void WriteAllText(string path, string contents)
        {
            if (FailWrites)
            {
                throw new IOException("denied by test");
            }

            Writes++;
            Files[path] = contents;
        }
    }
}
