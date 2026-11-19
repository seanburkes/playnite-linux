using Avalonia;
using Avalonia.Headless;

namespace Playnite.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (SteamLaunchCommand.IsRequested(args))
        {
            return SteamLaunchCommand.RunAsync(args).GetAwaiter().GetResult();
        }

        var builder = AppBuilder.Configure<App>();
        builder = args.Contains("--headless", StringComparer.Ordinal)
            ? builder.UseHeadless(new AvaloniaHeadlessPlatformOptions())
            : builder.UsePlatformDetect();

        var exitCode = builder.StartWithClassicDesktopLifetime(args);
        return exitCode == 0 && args.Contains("--smoke", StringComparer.Ordinal)
            && !((App)Application.Current!).SmokeSucceeded ? 1 : exitCode;
    }
}
