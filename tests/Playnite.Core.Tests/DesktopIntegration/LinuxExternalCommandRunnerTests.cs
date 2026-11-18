using System.Diagnostics;
using Playnite.Platform.DesktopIntegration;
using Playnite.Platform.Linux.DesktopIntegration;
using Xunit;

namespace Playnite.Core.Tests.DesktopIntegration;

public sealed class LinuxExternalCommandRunnerTests
{
    [Fact]
    public async Task ArgumentsWithSpacesAndQuotesReachTheChildVerbatim()
    {
        var runner = new LinuxExternalCommandRunner();

        var result = await runner.RunAsync(
            new ExternalCommand("/bin/echo", new[] { "two words", "it's", "\"quoted\"" }, TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("two words it's \"quoted\"\n", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task TimeoutKillsTheChildAndReturnsAFailureInsteadOfHanging()
    {
        var runner = new LinuxExternalCommandRunner();
        var started = Stopwatch.GetTimestamp();

        var result = await runner.RunAsync(
            new ExternalCommand("/bin/sleep", new[] { "30" }, TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(result.Started);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("timed out", result.Failure!, StringComparison.Ordinal);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"timeout took {elapsed}");
    }

    [Fact]
    public async Task CancellationKillsTheChildAndPropagates()
    {
        var runner = new LinuxExternalCommandRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var started = Stopwatch.GetTimestamp();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ExternalCommand("/bin/sleep", new[] { "30" }, TimeSpan.FromSeconds(30)),
            cancellation.Token));

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MissingExecutableIsAReportedFailure()
    {
        var runner = new LinuxExternalCommandRunner();

        var result = await runner.RunAsync(
            new ExternalCommand("/definitely/not/a/program", Array.Empty<string>(), TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Fact]
    public void LocatorFindsAbsolutePathsAndIgnoresEmptyPathEntries()
    {
        var locator = new LinuxExecutableLocator(pathValue: ":/bin");

        Assert.True(locator.Exists("sh"));
        Assert.True(locator.Exists("/bin/sh"));
        Assert.False(locator.Exists("definitely-not-a-program"));
        Assert.False(locator.Exists(string.Empty));
    }
}
