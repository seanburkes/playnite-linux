using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HeadlessSpike;

public sealed partial class SyntheticWindow : Window
{
    public SyntheticWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("Play")!.Click += (_, _) =>
            this.FindControl<TextBlock>("Status")!.Text = "Invoked: Orbit (no process launched)";
    }
}
