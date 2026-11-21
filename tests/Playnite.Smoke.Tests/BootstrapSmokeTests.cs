using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Playnite.Smoke.Tests;

/// <summary>Headless UI evidence that the p7-001 composition root runs in the real host.</summary>
public sealed class BootstrapSmokeTests
{
    private const string CatalogTitle = "Localised From Temp Catalog";
    private const string ThemeName = "Default";

    [Fact]
    public async Task Headless_bootstrap_loads_the_temp_theme_and_catalog_in_an_isolated_profile()
    {
        var root = Path.Combine(Path.GetTempPath(), "playnite-p7-001-" + Path.GetRandomFileName());
        var configHome = Path.Combine(root, "config");
        var stateHome = Path.Combine(root, "state");
        var dataHome = Path.Combine(root, "data");
        var cacheHome = Path.Combine(root, "cache");
        var runtimeHome = Path.Combine(root, "runtime");
        var home = Path.Combine(root, "home");
        var realLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "state", "playnite", "playnite.log");

        try
        {
            var appConfig = Path.Combine(configHome, "playnite");
            var themePath = Path.Combine(appConfig, "Themes", ThemeName, "theme.axaml");
            var catalogPath = Path.Combine(appConfig, "Localization", "en.json");
            Directory.CreateDirectory(Path.GetDirectoryName(themePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
            Directory.CreateDirectory(stateHome);
            Directory.CreateDirectory(dataHome);
            Directory.CreateDirectory(cacheHome);
            Directory.CreateDirectory(runtimeHome);
            Directory.CreateDirectory(home);
            File.WriteAllText(themePath, """
                <Styles xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Style Selector="Window">
                    <Setter Property="FontSize" Value="21" />
                  </Style>
                </Styles>
                """);
            File.WriteAllText(catalogPath, $"{{\"LOCAboutWindowTitle\": \"{CatalogTitle}\"}}");

            var result = await RunDesktopAsync(configHome, stateHome, dataHome, cacheHome, runtimeHome, home);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Error);
            var lines = result.Output.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains($"bootstrap: theme={ThemeName}", lines);
            Assert.Contains("bootstrap: theme-applied=true", lines);
            Assert.Contains("bootstrap: theme-style-applied=true", lines);
            Assert.Contains($"bootstrap: theme-source={themePath}", lines);
            Assert.Contains($"bootstrap: localisation={CatalogTitle}", lines);
            Assert.Contains($"bootstrap: localisation-source={catalogPath}", lines);
            Assert.Contains($"bootstrap: config-root={Path.Combine(configHome, "playnite")}", lines);
            Assert.Contains($"bootstrap: state-root={Path.Combine(stateHome, "playnite")}", lines);
            Assert.Contains("bootstrap: single-instance=skipped (headless test host)", lines);
            Assert.Contains("Window opened: Playnite", lines);
            Assert.Contains("Window closed", lines);

            var expectedLog = Path.Combine(stateHome, "playnite", "playnite.log");
            Assert.True(File.Exists(expectedLog), $"expected the startup log at {expectedLog}");
            var log = File.ReadAllText(expectedLog);
            Assert.Contains($"theme: loaded {ThemeName} from {themePath} (FontSize=21)", log);
            Assert.Contains($"localisation: LOCAboutWindowTitle={CatalogTitle}", log);

            var written = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
            Assert.NotEmpty(written);
            Assert.All(written, path => Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(path)));
            Assert.Contains(root, log);
            Assert.Contains(CatalogTitle, log);
            // A sibling smoke test may write its own entries to the real profile. Our child must
            // never leave its unique temp-root marker or catalog title there.
            if (File.Exists(realLog))
            {
                var realProfileLog = File.ReadAllText(realLog);
                Assert.DoesNotContain(root, realProfileLog);
                Assert.DoesNotContain(CatalogTitle, realProfileLog);
            }
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
        string home,
        params string[] extraArgs)
    {
        var args = new List<string> { "--headless", "--bootstrap-smoke" };
        args.AddRange(extraArgs);
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
                $"Desktop bootstrap smoke did not shut down within 30 seconds. stdout: {await output}; stderr: {await error}");
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
