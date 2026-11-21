using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Playnite.Desktop.Shell;

public sealed partial class SidebarView : UserControl
{
    public SidebarView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
