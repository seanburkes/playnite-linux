using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Playnite.Desktop.Theme;

namespace Playnite.Desktop.Shell;

public sealed partial class AboutView : UserControl
{
    public AboutView() : this(string.Empty, string.Empty)
    {
    }

    public AboutView(string title, string message)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ContentControl>("AboutLogoHost")!.Content = PlayniteIcons.CreateBrandLogo(32);
        this.FindControl<TextBlock>("AboutTitleText")!.Text = title;
        this.FindControl<TextBlock>("AboutMessageText")!.Text = message;
    }
}
