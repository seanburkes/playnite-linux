using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Playnite.Desktop.Bootstrap;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;

namespace Playnite.Desktop;

internal static class Program
{
    private const string InstanceId = "playnite.desktop";

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var headless = args.Contains("--headless", StringComparer.Ordinal);
        var smoke = args.Contains("--smoke", StringComparer.Ordinal)
            || args.Contains("--bootstrap-smoke", StringComparer.Ordinal);
        var bootstrapSmoke = args.Contains("--bootstrap-smoke", StringComparer.Ordinal);

        var directories = XdgDirectories.Resolve(new LinuxEnvironment());
        var services = new ServiceRegistry();
        services.AddSingleton<IAppLogger>(_ =>
            new FileAppLogger(Path.Combine(directories.State, "playnite.log")));
        services.AddSingleton(_ =>
            BootstrapLocalization.Load(Path.Combine(directories.Config, "Localization", "en.json")));
        services.AddSingleton(registry => new DesktopBootstrapper(
            registry.GetRequiredService<IAppLogger>(),
            registry.GetRequiredService<BootstrapLocalization>(),
            ResolveThemePath(directories)));

        using var logger = services.GetRequiredService<IAppLogger>();
        var bootstrapper = services.GetRequiredService<DesktopBootstrapper>();
        SingleInstanceBootstrap? singleInstance = null;
        try
        {
            if (!headless)
            {
                singleInstance = new SingleInstanceBootstrap(
                    SingleInstanceService.Start(InstanceId, directories), logger);
                if (singleInstance.DecideStartup() == StartupDecision.ExitAlreadyRunning)
                {
                    return 2;
                }
            }
            else
            {
                logger.Info("single-instance: skipped (headless test host)");
            }

            var builder = AppBuilder.Configure<App>();
            builder = headless
                ? builder.UseHeadless(new AvaloniaHeadlessPlatformOptions())
                : builder.UsePlatformDetect();

            // App.OnFrameworkInitializationCompleted installs its smoke close hook from Args.
            // The bootstrap variant is a superset of --smoke, so the lifetime must see that flag.
            var lifetimeArgs = bootstrapSmoke && !args.Contains("--smoke", StringComparer.Ordinal)
                ? [.. args, "--smoke"]
                : args;
            var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = lifetimeArgs };
            builder.SetupWithLifetime(lifetime);
            var application = Application.Current
                ?? throw new InvalidOperationException("The Avalonia application was not initialised.");
            bootstrapper.Apply(application);

            if (bootstrapSmoke)
            {
                Console.WriteLine($"bootstrap: theme={bootstrapper.AppliedTheme?.Name ?? "unknown"}");
                Console.WriteLine($"bootstrap: localisation={bootstrapper.LocalisedProbe}");
                Console.WriteLine("bootstrap: single-instance=skipped (headless test host)");
            }

            var exitCode = lifetime.Start();
            return exitCode == 0 && smoke && !((App)application).SmokeSucceeded ? 1 : exitCode;
        }
        finally
        {
            if (singleInstance is not null)
            {
                await singleInstance.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string? ResolveThemePath(XdgDirectories directories)
    {
        var themesRoot = Path.Combine(directories.Config, "Themes");
        return ThemePaths.TryResolve(themesRoot, "Default", out var path, out _) && File.Exists(path)
            ? path
            : null;
    }
}
