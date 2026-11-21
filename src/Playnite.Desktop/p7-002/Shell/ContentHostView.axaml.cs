using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Playnite.Desktop.Shell;

public sealed partial class ContentHostView : UserControl
{
    public ContentHostView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
