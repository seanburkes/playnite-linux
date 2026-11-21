using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Playnite.Smoke.Tests;

/// <summary>Headless UI evidence that the p7-002 shell runs in the real host.</summary>
public sealed class ShellSmokeTests
{
    private const string ControlledTitle = "About From Controlled Catalog";
    private const string ControlledAuthor = "Author From Controlled Catalog";
    private static readonly string[] Regions = ["TopPanel", "Sidebar", "ContentHost", "Notifications"];

    [Fact]
    public async Task Headless_shell_exposes_named_regions_and_the_about_messagebox_pilot()
    {
        var root = Path.Combine(Path.GetTempPath(), "playnite-p7-002-" + Path.GetRandomFileName());
        var configHome = Path.Combine(root, "config");
        var stateHome = Path.Combine(root, "state");
        var dataHome = Path.Combine(root, "data");
        var cacheHome = Path.Combine(root, "cache");
        var runtimeHome = Path.Combine(root, "runtime");
        var home = Path.Combine(root, "home");

        try
        {
            var appConfig = Path.Combine(configHome, "playnite");
            var catalogPath = Path.Combine(appConfig, "Localization", "en.json");
            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
            Directory.CreateDirectory(stateHome);
            Directory.CreateDirectory(dataHome);
            Directory.CreateDirectory(cacheHome);
            Directory.CreateDirectory(runtimeHome);
            Directory.CreateDirectory(home);
            File.WriteAllText(catalogPath,
                $"{{\"LOCAboutWindowTitle\":\"{ControlledTitle}\",\"LOCAboutAuthor\":\"{ControlledAuthor}\"}}");

            var result = await RunDesktopAsync(configHome, stateHome, dataHome, cacheHome, runtimeHome, home);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Error);

            var lines = result.Output.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains("Window opened: Playnite", lines);
            Assert.Contains("Window closed", lines);
            Assert.Contains("shell: about-title=" + ControlledTitle, lines);
            Assert.Contains("shell: pilot=about,messagebox", lines);

            var regions = Assert.Single(lines.Where(line => line.StartsWith("shell: regions=", StringComparison.Ordinal)));
            foreach (var region in Regions)
            {
                Assert.Contains(region, regions);
            }

            var semantic = Assert.Single(lines.Where(line => line.StartsWith("shell: semantic=", StringComparison.Ordinal)));
            foreach (var region in Regions)
            {
                Assert.Contains(region + ":", semantic);
            }
            Assert.Contains("AboutButton:Button:", semantic);
            Assert.Contains("MessageBoxButton:Button:", semantic);

            // Every write stays under the isolated root; no temp marker reaches the real profile.
            var written = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
            Assert.NotEmpty(written);
            Assert.All(written, path => Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(path)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunDesktopAsync(
        string configHome,
        string stateHome,
        string dataHome,
        string cacheHome,
        string runtimeHome,
        string home)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(DesktopHostPath());
        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--smoke");
        start.ArgumentList.Add("--shell-smoke");

        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        start.Environment["XDG_CONFIG_HOME"] = configHome;
        start.Environment["XDG_STATE_HOME"] = stateHome;
        start.Environment["XDG_DATA_HOME"] = dataHome;
        start.Environment["XDG_CACHE_HOME"] = cacheHome;
        start.Environment["XDG_RUNTIME_DIR"] = runtimeHome;
        start.Environment["HOME"] = home;
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
            throw new TimeoutException(
                $"Desktop shell smoke did not shut down within 30 seconds. stdout: {await output}; stderr: {await error}");
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

        var configuration = typeof(ShellSmokeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "BuildConfiguration").Value!;
        return Path.Combine(
            root?.FullName ?? throw new InvalidOperationException("Cannot locate Playnite.slnx"),
            "src", "Playnite.Desktop", "bin", configuration, "net11.0", "Playnite.Desktop.dll");
    }
}
