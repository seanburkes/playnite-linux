using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Playnite.Core;
using Playnite.Core.UriHandling;
using Playnite.Desktop.Bootstrap;
using Playnite.Desktop.Integration;
using Playnite.Platform.Integration;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.DesktopIntegration;
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
        DesktopActivationBridge.TryExtractUriArgument(args, out var activationUri);

        var env = new LinuxEnvironment();
        var directories = XdgDirectories.Resolve(env);
        var services = new ServiceRegistry();
        services.AddSingleton<IRegularFileProbe>(_ => new LinuxRegularFileProbe());
        services.AddSingleton<IAppLogger>(registry =>
            new FileAppLogger(
                Path.Combine(directories.State, "playnite.log"),
                registry.GetRequiredService<IRegularFileProbe>()));
        services.AddSingleton(registry =>
            BootstrapLocalization.Load(
                Path.Combine(directories.Config, "Localization", "en.json"),
                registry.GetRequiredService<IRegularFileProbe>()));
        services.AddSingleton(registry => new DesktopBootstrapper(
            registry.GetRequiredService<IAppLogger>(),
            registry.GetRequiredService<BootstrapLocalization>(),
            ResolveThemePath(directories, services.GetRequiredService<IRegularFileProbe>()),
            registry.GetRequiredService<IRegularFileProbe>()));

        using var logger = services.GetRequiredService<IAppLogger>();
        var bootstrapper = services.GetRequiredService<DesktopBootstrapper>();
        SingleInstanceBootstrap? singleInstance = null;
        TrayController? tray = null;
        DesktopActivationBridge? activationBridge = null;
        try
        {
            SingleInstanceService? singleInstanceService = null;
            if (!headless)
            {
                singleInstanceService = SingleInstanceService.Start(InstanceId, directories);
                singleInstance = new SingleInstanceBootstrap(singleInstanceService, logger);
                if (singleInstance.DecideStartup() == StartupDecision.ExitAlreadyRunning)
                {
                    if (activationUri is not null)
                    {
                        var forwarded = await singleInstanceService.ActivateAsync(activationUri).ConfigureAwait(false);
                        logger.Info($"single-instance: activate status={forwarded.Status} ({forwarded.Diagnostic ?? "no diagnostic"})");
                        return forwarded.Status == SingleInstanceStatus.Forwarded ? 0 : 2;
                    }

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

            if (!headless)
            {
                await RegisterProtocolAsync(directories, env, logger).ConfigureAwait(false);
                tray = await InitializeTrayAsync(
                    application,
                    lifetime,
                    directories,
                    env,
                    services.GetRequiredService<BootstrapLocalization>(),
                    logger).ConfigureAwait(false);

                var commandHost = new DesktopUriCommandHost(
                    () => lifetime.MainWindow as MainWindow,
                    () => tray?.RestoreWindow());
                var uriHandler = new UriHandlerApi(commandHost);
                activationBridge = new DesktopActivationBridge(singleInstanceService, uriHandler, logger);
                activationBridge.Start();

                var pendingInitialUri = activationUri;
                Window.WindowOpenedEvent.AddClassHandler<MainWindow>((window, routedArgs) =>
                {
                    if (tray is not null)
                    {
                        window.AttachTray(tray, alreadyOpened: true);
                    }
                    if (pendingInitialUri is not null)
                    {
                        var uriToDispatch = Interlocked.Exchange(ref pendingInitialUri, null);
                        if (uriToDispatch is not null)
                        {
                            Dispatcher.UIThread.Post(() => { _ = activationBridge.DispatchUriAsync(uriToDispatch); });
                        }
                    }
                });
            }

            double? observedFontSize = null;
            if (bootstrapSmoke && lifetime.MainWindow is Window smokeWindow)
            {
                smokeWindow.Opened += (_, _) => observedFontSize = smokeWindow.FontSize;
            }

            if (bootstrapSmoke)
            {
                var theme = bootstrapper.AppliedTheme;
                Console.WriteLine($"bootstrap: theme={theme?.Name ?? "unknown"}");
                Console.WriteLine($"bootstrap: theme-applied={theme?.Applied.ToString().ToLowerInvariant() ?? "false"}");
                Console.WriteLine($"bootstrap: theme-style-registered={theme?.StyleRegistered.ToString().ToLowerInvariant() ?? "false"}");
                Console.WriteLine($"bootstrap: theme-source={theme?.Source ?? "none"}");
                Console.WriteLine($"bootstrap: localisation={bootstrapper.LocalisedProbe}");
                Console.WriteLine($"bootstrap: localisation-source={bootstrapper.LocalisationSource}");
                Console.WriteLine($"bootstrap: config-root={directories.Config}");
                Console.WriteLine($"bootstrap: state-root={directories.State}");
                Console.WriteLine("bootstrap: single-instance=skipped (headless test host)");
            }

            var exitCode = lifetime.Start();
            if (bootstrapSmoke)
            {
                Console.WriteLine("bootstrap: window-font-size="
                    + (observedFontSize?.ToString(CultureInfo.InvariantCulture) ?? "none"));
            }

            return exitCode == 0 && smoke && !((App)application).SmokeSucceeded ? 1 : exitCode;
        }
        finally
        {
            activationBridge?.Dispose();
            if (tray is not null)
            {
                await tray.DisposeAsync().ConfigureAwait(false);
            }

            if (singleInstance is not null)
            {
                await singleInstance.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task RegisterProtocolAsync(
        XdgDirectories directories,
        IEnvironment environment,
        IAppLogger logger)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            logger.Info("protocol: skipped (process executable path unavailable)");
            return;
        }

        var handler = new PlayniteProtocolHandler(
            directories,
            executablePath,
            environment,
            new SystemProtocolFileSystem());
        var result = await handler.RegisterAsync().ConfigureAwait(false);
        logger.Info($"protocol: status={result.Status} ({result.Diagnostic ?? "no diagnostic"})");
    }

    private static async Task<TrayController> InitializeTrayAsync(
        Application application,
        ClassicDesktopStyleApplicationLifetime lifetime,
        XdgDirectories directories,
        IEnvironment environment,
        BootstrapLocalization localization,
        IAppLogger logger)
    {
        var preferences = await TrayPreferences
            .LoadAsync(Path.Combine(directories.Config, "settings.json"), logger)
            .ConfigureAwait(false);
        TrayController? controller = null;
        var presenter = new AvaloniaTrayIconPresenter(
            application,
            id => Dispatcher.UIThread.Post(() => controller?.HandleMenuItem(id)),
            () => Dispatcher.UIThread.Post(() => controller?.HandleTrayClick()));
        var hostProbe = new LinuxTrayHostProbe(
            new LinuxDesktopSessionProbe(environment),
            new LinuxExternalCommandRunner(),
            new LinuxExecutableLocator());
        var trayService = new LinuxTrayService(hostProbe, presenter);
        controller = new TrayController(
            trayService,
            preferences,
            logger,
            () => lifetime.MainWindow,
            () => Dispatcher.UIThread.Post(() => lifetime.Shutdown()));
        await controller.InitializeAsync(localization: localization).ConfigureAwait(false);
        return controller;
    }

    private static string? ResolveThemePath(XdgDirectories directories, IRegularFileProbe probe)
    {
        var themesRoot = Path.Combine(directories.Config, "Themes");
        return ThemePaths.TryResolve(themesRoot, "Default", out var path, out _)
            && BootstrapFiles.ExistingRegular(path, probe) is not null
            ? path
            : null;
    }
}
