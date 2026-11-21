using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Playnite.Desktop.Shell;

public sealed partial class NotificationsView : UserControl
{
    public NotificationsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
