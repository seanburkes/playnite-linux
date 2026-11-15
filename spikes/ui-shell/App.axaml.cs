using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;

namespace UiShellSpike;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        AvaloniaXamlLoader.Load(this);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .WithInterFont().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
