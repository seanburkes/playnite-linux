using Playnite.Core.Tests.TestDoubles;
using Playnite.Platform.Linux.Integration;
using Xunit;

namespace Playnite.Core.Tests.Integration;

public sealed class XdgDirectoriesTests
{
    [Fact]
    public void AbsoluteOverridesWinForEveryBaseDirectory()
    {
        var environment = new MemoryEnvironment();
        environment.Variables["XDG_CONFIG_HOME"] = "/custom/config/";
        environment.Variables["XDG_DATA_HOME"] = "/custom/data";
        environment.Variables["XDG_CACHE_HOME"] = "/custom/cache";
        environment.Variables["XDG_STATE_HOME"] = "/custom/state";
        environment.Variables["XDG_RUNTIME_DIR"] = "/run/user/1000";

        var directories = XdgDirectories.Resolve(environment);

        Assert.Equal("/custom/config", directories.ConfigHome);
        Assert.Equal("/custom/data", directories.DataHome);
        Assert.Equal("/custom/cache", directories.CacheHome);
        Assert.Equal("/custom/state", directories.StateHome);
        Assert.Equal("/custom/config/playnite", directories.Config);
        Assert.Equal("/custom/data/playnite", directories.Data);
        Assert.Equal("/custom/cache/playnite", directories.Cache);
        Assert.Equal("/custom/state/playnite", directories.State);
        Assert.Equal("/run/user/1000/playnite", directories.Runtime);
        Assert.Empty(directories.Diagnostics);
    }

    [Theory]
    [InlineData("XDG_CONFIG_HOME", "/fallback/.config")]
    [InlineData("XDG_DATA_HOME", "/fallback/.local/share")]
    [InlineData("XDG_CACHE_HOME", "/fallback/.cache")]
    [InlineData("XDG_STATE_HOME", "/fallback/.local/state")]
    public void UnsetEmptyWhitespaceRelativeAndTildeValuesFallBack(string variable, string expected)
    {
        foreach (var value in new string?[] { null, "", " ", "\t", "relative", "./relative", "../relative", "~/relative", "~" })
        {
            var environment = new MemoryEnvironment { Home = "/fallback" };
            environment.Variables[variable] = value;
            var directories = XdgDirectories.Resolve(environment);
            var actual = variable switch
            {
                "XDG_CONFIG_HOME" => directories.ConfigHome,
                "XDG_DATA_HOME" => directories.DataHome,
                "XDG_CACHE_HOME" => directories.CacheHome,
                _ => directories.StateHome,
            };
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative")]
    [InlineData("~/run")]
    public void MissingOrInvalidRuntimeDirectoryFallsBackToStateAndSaysSo(string? value)
    {
        var environment = new MemoryEnvironment();
        environment.Variables["XDG_RUNTIME_DIR"] = value;

        var directories = XdgDirectories.Resolve(environment);

        Assert.Equal("/home/test-user/.local/state/playnite/runtime", directories.Runtime);
        var diagnostic = Assert.Single(directories.Diagnostics);
        Assert.Contains("XDG_RUNTIME_DIR", diagnostic, StringComparison.Ordinal);
        Assert.Contains("State directory", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RootHomeAndRootOverridesStayAbsoluteAndNormalized()
    {
        var environment = new MemoryEnvironment { Home = "/" };
        environment.Variables["XDG_CONFIG_HOME"] = "/";
        environment.Variables["XDG_RUNTIME_DIR"] = "/";

        var directories = XdgDirectories.Resolve(environment);

        Assert.Equal("/playnite", directories.Config);
        Assert.Equal("/.local/share/playnite", directories.Data);
        Assert.Equal("/.cache/playnite", directories.Cache);
        Assert.Equal("/.local/state/playnite", directories.State);
        Assert.Equal("/playnite", directories.Runtime);
    }

    [Fact]
    public void ResolutionNeverCreatesDirectories()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var environment = new MemoryEnvironment { Home = temporary.Path + "/home" };

        var directories = XdgDirectories.Resolve(environment);

        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path));
        Assert.False(Directory.Exists(directories.Config));
        Assert.False(Directory.Exists(directories.Data));
        Assert.False(Directory.Exists(directories.Cache));
        Assert.False(Directory.Exists(directories.State));
        Assert.False(Directory.Exists(directories.Runtime));
    }

    [Fact]
    public void EnsureRuntimeDirectoryCreatesOnlyTheRuntimeTreeAndIsIdempotent()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var environment = new MemoryEnvironment { Home = temporary.Path };
        environment.Variables["XDG_RUNTIME_DIR"] = temporary.Path + "/run";
        var directories = XdgDirectories.Resolve(environment);

        Assert.Null(directories.EnsureRuntimeDirectory());
        Assert.Null(directories.EnsureRuntimeDirectory());
        Assert.True(Directory.Exists(directories.Runtime));
        Assert.False(Directory.Exists(directories.Config));
        Assert.False(Directory.Exists(directories.Data));
        Assert.False(Directory.Exists(directories.Cache));
        Assert.False(Directory.Exists(directories.State));
        var entry = Assert.Single(Directory.GetFileSystemEntries(temporary.Path));
        Assert.Equal(temporary.Path + "/run", entry);
    }

    [Fact]
    public void UnusableRuntimePathReturnsADiagnosticInsteadOfThrowing()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var blocked = temporary.Path + "/blocked";
        File.WriteAllText(blocked, "a file, not a directory");
        var environment = new MemoryEnvironment { Home = temporary.Path };
        environment.Variables["XDG_RUNTIME_DIR"] = blocked;
        var directories = XdgDirectories.Resolve(environment);

        var diagnostic = directories.EnsureRuntimeDirectory();

        Assert.NotNull(diagnostic);
        Assert.Contains("Cannot create the runtime directory", diagnostic, StringComparison.Ordinal);
        Assert.Contains(blocked, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void NonAbsoluteHomeIsRejectedInsteadOfGuessed()
    {
        var environment = new MemoryEnvironment { Home = "relative-home" };
        Assert.Throws<ArgumentException>(() => XdgDirectories.Resolve(environment));
    }
}
