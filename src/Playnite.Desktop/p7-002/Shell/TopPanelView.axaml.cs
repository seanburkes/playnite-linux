using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Playnite.Desktop.Shell;

public sealed partial class TopPanelView : UserControl
{
    public TopPanelView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
