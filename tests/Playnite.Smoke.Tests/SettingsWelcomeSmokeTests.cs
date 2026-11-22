using System.Diagnostics;
using System.Reflection;
using Playnite.Settings;
using Xunit;

namespace Playnite.Smoke.Tests;

public sealed class SettingsWelcomeSmokeTests
{
    [Fact]
    public async Task Welcome_and_settings_use_separate_state_and_apply_the_language_on_relaunch()
    {
        var root = Path.Combine(Path.GetTempPath(), "playnite-settings-smoke-" + Path.GetRandomFileName());
        var config = Path.Combine(root, "config", "playnite");
        var catalogs = Path.Combine(config, "Localization");
        var token = TestContext.Current.CancellationToken;
        try
        {
            Directory.CreateDirectory(catalogs);
            await File.WriteAllTextAsync(Path.Combine(catalogs, "de.json"),
                """{"LOCSettingsLabel":"Einstellungen","LOCLanguageSettingsLabel":"Sprache"}""", token);

            var first = await RunDesktopAsync(root, "--settings-smoke-edit", token);
            Assert.Contains("settings: welcome=True; workspace=False; sidebar=True", first);
            Assert.Contains("settings: welcome-title=Welcome; focused=True", first);
            Assert.Contains("settings: acknowledged=True", first);
            Assert.Contains("settings: title=Settings; sections=General,Appearance", first);
            Assert.Contains("settings: language-label=Playnite language; selected=english; checked=True", first);
            Assert.Contains("settings: after-cancel-sidebar=True", first);
            Assert.Contains("settings: reopened=english/True", first);
            Assert.Contains("settings: after-save-sidebar=False; button=Settings", first);

            using var service = new PlayniteSettingsService(Path.Combine(config, "settings.json"));
            var persisted = await service.LoadAsync(token);
            Assert.Equal("de", persisted.Get(PlayniteSettingsKeys.Language));
            Assert.False(persisted.Get(PlayniteSettingsKeys.ShowSidebar));
            Assert.False(persisted.Get(PlayniteSettingsKeys.FirstTimeWizardComplete));
            Assert.True(File.Exists(Path.Combine(config, "welcome-acknowledged")));

            var second = await RunDesktopAsync(root, "--settings-smoke-observe", token);
            Assert.Contains("settings: welcome=False; workspace=True; sidebar=False", second);
            Assert.Contains("settings: title=Einstellungen; sections=General,Appearance", second);
            Assert.Contains("settings: language-label=Sprache; selected=de; checked=False", second);

            File.Delete(Path.Combine(catalogs, "de.json"));
            var withMissingCatalog = await RunDesktopAsync(root, "--settings-smoke-observe", token);
            Assert.Contains("settings: title=Settings; sections=General,Appearance", withMissingCatalog);
            Assert.Contains("settings: language-label=Playnite language; selected=de; checked=False", withMissingCatalog);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> RunDesktopAsync(string root, string mode, CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(DesktopHostPath());
        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add(mode);
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        foreach (var (key, directory) in new (string, string)[]
        {
            ("XDG_CONFIG_HOME", "config"), ("XDG_STATE_HOME", "state"), ("XDG_DATA_HOME", "data"),
            ("XDG_CACHE_HOME", "cache"), ("XDG_RUNTIME_DIR", "runtime"), ("HOME", "home"),
        })
        {
            var path = Path.Combine(root, directory);
            Directory.CreateDirectory(path);
            start.Environment[key] = path;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException($"Settings smoke timed out. stdout: {await output}; stderr: {await error}");
        }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, await error);
        return await output;
    }

    private static string DesktopHostPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Playnite.slnx")))
        {
            root = root.Parent;
        }

        var configuration = typeof(SettingsWelcomeSmokeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "BuildConfiguration").Value!;
        return Path.Combine(
            root?.FullName ?? throw new InvalidOperationException("Cannot locate Playnite.slnx"),
            "src", "Playnite.Desktop", "bin", configuration, "net11.0", "Playnite.Desktop.dll");
    }
}
