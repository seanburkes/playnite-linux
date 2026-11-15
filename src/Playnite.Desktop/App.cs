using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Playnite.Desktop;

public sealed class App : Application
{
    internal bool SmokeSucceeded { get; private set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            if (desktop.Args?.Contains("--smoke", StringComparer.Ordinal) == true)
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

        base.OnFrameworkInitializationCompleted();
    }
}
