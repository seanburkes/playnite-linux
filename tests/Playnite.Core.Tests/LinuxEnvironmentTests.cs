using Playnite.Platform.Linux;
using Xunit;

namespace Playnite.Core.Tests;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

[Collection("Process environment")]
public sealed class LinuxEnvironmentTests
{
    [Fact]
    public void CapturesControlledEnvironmentOnceWithoutReadingDeveloperHome()
    {
        string[] names = ["HOME", "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "TZ", "LANG", "LC_ALL"];
        var original = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("HOME", "/controlled-test-home");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "relative");
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", "/controlled-cache");
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", "/controlled-state");
            Environment.SetEnvironmentVariable("TZ", "UTC");
            Environment.SetEnvironmentVariable("LANG", "C");
            Environment.SetEnvironmentVariable("LC_ALL", "C");
            var environment = new LinuxEnvironment();
            Environment.SetEnvironmentVariable("HOME", "/changed-after-capture");
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", "/changed-after-capture");
            var paths = new LinuxPaths(environment, new TestDoubles.MemoryFileSystem());
            Assert.Equal("/controlled-test-home", paths.Home);
            Assert.Equal("/controlled-test-home/.local/share/playnite", paths.UserData);
            Assert.Equal("/controlled-test-home/.config/playnite", paths.Config);
            Assert.Equal("/controlled-cache/playnite", paths.Cache);
            Assert.Equal("/controlled-state/playnite", paths.State);
            Assert.Equal("UTC", environment.GetVariable("TZ"));
            Assert.Equal("C", environment.GetVariable("LANG"));
            Assert.Equal("C", environment.GetVariable("LC_ALL"));
            Assert.Equal(Environment.CurrentDirectory, environment.CurrentDirectory);
            Assert.Equal(AppContext.BaseDirectory, environment.ProgramDirectory);
        }
        finally
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, original[name]);
            }
        }
    }

    [Fact]
    public void RelativeHomeIsCapturedVerbatimAndRejectedByPaths()
    {
        var original = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", "relative");
            var environment = new LinuxEnvironment();
            Assert.Equal("relative", environment.Home);
            Assert.Throws<ArgumentException>(() => new LinuxPaths(environment, new TestDoubles.MemoryFileSystem()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", original);
        }
    }
}
