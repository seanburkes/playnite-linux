using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Playnite.Smoke.Tests;

/// <summary>Headless UI evidence that the p7-001 composition root runs in the real host.</summary>
public sealed class BootstrapSmokeTests
{
    [Fact]
    public async Task Headless_bootstrap_smoke_reports_theme_localisation_and_single_instance()
    {
        var result = await RunDesktopAsync("--headless", "--bootstrap-smoke");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        var lines = result.Output.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("bootstrap: theme=Default", lines);
        Assert.Contains("bootstrap: localisation=About Playnite", lines);
        Assert.Contains("bootstrap: single-instance=skipped (headless test host)", lines);
        Assert.Contains("Window opened: Playnite", lines);
        Assert.Contains("Window closed", lines);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunDesktopAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(DesktopHostPath());
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
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
            throw new TimeoutException($"Desktop bootstrap smoke did not shut down within 30 seconds. stdout: {await output}; stderr: {await error}");
        }

        return (process.ExitCode, await output, await error);
    }

    private static string DesktopHostPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Playnite.slnx")))
        {
            root = root.Parent;
        }

        var configuration = typeof(BootstrapSmokeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "BuildConfiguration").Value!;
        return Path.Combine(
            root?.FullName ?? throw new InvalidOperationException("Cannot locate Playnite.slnx"),
            "src", "Playnite.Desktop", "bin", configuration, "net11.0", "Playnite.Desktop.dll");
    }
}
