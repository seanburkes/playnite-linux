using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Playnite.Desktop.Theme;

namespace Playnite.Desktop.Shell;

public sealed partial class TopPanelView : UserControl
{
    public TopPanelView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ContentControl>("BrandLogoHost")!.Content = PlayniteIcons.CreateBrandLogo(24);
    }
}
