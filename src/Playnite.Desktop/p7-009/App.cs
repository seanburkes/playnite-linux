using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Playnite.Desktop.Setup;
using Playnite.Desktop.Theme;

namespace Playnite.Desktop;

public sealed class App : Application
{
    internal bool SmokeSucceeded { get; private set; }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new PlayniteDarkTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var smoke = args.Contains("--smoke", StringComparer.Ordinal);
            if (smoke || (args.Contains("--headless", StringComparer.Ordinal) && !FirstTimeSetupSmoke.IsEnabled(args)))
            {
                var window = new MainWindow();
                desktop.MainWindow = window;
                if (smoke)
                {
                    InstallSmoke(window, desktop);
                }
            }
            else
            {
                // The shell's Loaded handlers can create the live database. Do not
                // even construct the shell until the first-run decision is durable.
                desktop.Startup += StartDesktop;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async void StartDesktop(object? sender, ControlledApplicationLifetimeStartupEventArgs startup)
    {
        if (sender is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }
        // Let the lifetime finish its synchronous startup before showing a window.
        await Dispatcher.Yield(DispatcherPriority.Background);
        var args = desktop.Args ?? [];
        var setup = new FirstTimeSetup();
        try
        {
            if (await setup.IsCompleteAsync())
            {
                if (FirstTimeSetupSmoke.IsEnabled(args))
                {
                    Console.WriteLine("first-run: already complete");
                }
                ShowShell(desktop, args);
                return;
            }
        }
        catch (Exception exception) when (FirstTimeSetup.IsExpectedFailure(exception))
        {
            if (setup.CanKeepLibrary)
            {
                // The shell already reports settings errors without blocking an existing library.
                ShowShell(desktop, args);
                return;
            }
            var failed = new FirstTimeStartupWindow(setup);
            failed.ShowStartupFailure(exception.Message);
            desktop.MainWindow = failed;
            failed.Show();
            return;
        }

        var wizard = new FirstTimeStartupWindow(setup);
        desktop.MainWindow = wizard;
        wizard.SetupComplete += (_, _) =>
        {
            ShowShell(desktop, args);
            wizard.Close();
        };
        FirstTimeSetupSmoke.Attach(wizard, args);
        wizard.Show();
    }

    private static void ShowShell(IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        var window = new MainWindow();
        desktop.MainWindow = window;
        if (FirstTimeSetupSmoke.IsEnabled(args))
        {
            window.Opened += (_, _) =>
            {
                Console.WriteLine($"first-run: shell visible={window.IsVisible}");
                Dispatcher.UIThread.Post(window.Close, DispatcherPriority.ApplicationIdle);
            };
        }
        window.Show();
    }

    private void InstallSmoke(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var opened = false;
        window.Opened += (_, _) =>
        {
            opened = window.IsVisible && desktop.Windows.Count == 1;
            if (opened)
            {
                Console.WriteLine($"Window opened: {window.Title}");
            }

            Dispatcher.UIThread.Post(window.Close, DispatcherPriority.ApplicationIdle);
        };
        window.Closed += (_, _) =>
        {
            SmokeSucceeded = opened;
            Console.WriteLine("Window closed");
        };
    }
}
