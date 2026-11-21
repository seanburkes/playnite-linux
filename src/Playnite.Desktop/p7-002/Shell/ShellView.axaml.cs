using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Playnite.Desktop.Shell;

public sealed partial class ShellView : UserControl
{
    public ShellView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
