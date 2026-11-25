using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Playnite.Smoke.Tests;

public sealed class HostSmokeTests
{
    [Fact]
    public async Task Headless_window_opens_and_closes_through_the_desktop_lifetime()
    {
        var result = await RunHost("Playnite.Desktop", "--headless", "--smoke");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal(["Window opened: Playnite", "Window closed"],
            result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_prints_its_assembly_version_and_exits_successfully(bool versionFlag)
    {
        var result = await RunHost("Playnite.Cli", versionFlag ? ["--version"] : []);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal(AssemblyName.GetAssemblyName(HostPath("Playnite.Cli")).Version!.ToString(), result.Output.Trim());
        Assert.True(Version.TryParse(result.Output.Trim(), out _));
    }

    [Fact]
    public async Task Cli_rejects_unsupported_arguments()
    {
        var result = await RunHost("Playnite.Cli", "--unknown");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal("Usage: Playnite.Cli [--version]", result.Error.Trim());
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunHost(string host, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(HostPath(host));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        var profile = Directory.CreateTempSubdirectory("playnite-host-smoke-");
        foreach (var directory in new[] { "DATA", "CONFIG", "STATE", "CACHE" })
        {
            start.Environment[$"XDG_{directory}_HOME"] = Path.Combine(profile.FullName, directory);
        }

        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException($"{host} did not shut down within 30 seconds. stdout: {await output}; stderr: {await error}");
            }

            return (process.ExitCode, await output, await error);
        }
        finally
        {
            profile.Delete(recursive: true);
        }
    }

    private static string HostPath(string host)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Playnite.slnx")))
        {
            root = root.Parent;
        }

        var configuration = typeof(HostSmokeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "BuildConfiguration").Value!;
        return Path.Combine(root?.FullName ?? throw new InvalidOperationException("Cannot locate Playnite.slnx"),
            "src", host, "bin", configuration, "net11.0", host + ".dll");
    }
}
