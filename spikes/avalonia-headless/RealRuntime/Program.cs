using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using HeadlessSpike;

namespace RealRuntime;

public sealed class RealRuntimeApp : Application
{
    public override void Initialize()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new SyntheticWindow();
            desktop.MainWindow = window;
            window.Opened += (_, _) =>
            {
                Report(window, "opened");
                // Mirror the headless test's starting point so keyboard traversal is comparable.
                Dispatcher.UIThread.Post(
                    () => window.FindControl<TextBox>("Search")!.Focus(),
                    DispatcherPriority.Loaded);
            };
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            later.Tick += (_, _) => { later.Stop(); Report(window, "settled"); };
            later.Start();
            window.Closed += (_, _) =>
            {
                Console.WriteLine("window closed");
                Console.Out.Flush();
            };
            window.Resized += (_, e) => Log($"resized clientArea={e.ClientSize}");
            window.FindControl<Button>("Play")!.Click += (_, _) =>
                Log("button-click Play Orbit");
            window.FindControl<CheckBox>("Installed")!.IsCheckedChanged += (_, _) =>
                Log($"checkbox installed={window.FindControl<CheckBox>("Installed")!.IsChecked}");
            window.FindControl<TextBox>("Search")!.TextChanged += (_, _) =>
                Log($"textbox search=\"{window.FindControl<TextBox>("Search")!.Text}\"");
            window.AddHandler(InputElement.GotFocusEvent, (_, e) =>
            {
                var id = (e.Source as Control)?.Name ?? e.Source?.GetType().Name ?? "?";
                Log($"focus {id}");
            }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        }

        var exitAfter = Environment.GetEnvironmentVariable("SPIKE_EXIT_AFTER_SECONDS");
        if (int.TryParse(exitAfter, out var seconds) && seconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Log($"auto-exit after {seconds}s");
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            };
            timer.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void Report(Window window, string when)
    {
        var handle = window.TryGetPlatformHandle();
        var impl = window.PlatformImpl?.GetType().FullName ?? "unknown";
        Log($"window {when} title=\"{window.Title}\" clientSize={window.ClientSize} " +
            $"frameSize={window.FrameSize} scaling={window.RenderScaling} impl={impl} " +
            $"handle={handle?.HandleDescriptor ?? "none"} handleValue=0x{handle?.Handle ?? 0:x}");
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[runtime] {message}");
        Console.Out.Flush();
    }
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        foreach (var key in new[]
                 {
                     "XDG_SESSION_TYPE", "WAYLAND_DISPLAY", "DISPLAY", "DBUS_SESSION_BUS_ADDRESS",
                     "AT_SPI_BUS_ADDRESS", "GTK_A11Y", "ACCESSIBILITY_ENABLED", "NO_AT_BRIDGE",
                 })
        {
            Console.WriteLine($"[env] {key}={Environment.GetEnvironmentVariable(key) ?? "<unset>"}");
        }
        Console.Out.Flush();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<RealRuntimeApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace(LogEventLevel.Warning, LogArea.Platform);
        if (Environment.GetEnvironmentVariable("SPIKE_PLATFORM") == "wayland")
            builder = builder.UseWayland();
        return builder;
    }
}
