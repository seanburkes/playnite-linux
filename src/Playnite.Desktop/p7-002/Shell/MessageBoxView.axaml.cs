using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Playnite.Desktop.Shell;

public sealed partial class MessageBoxView : UserControl
{
    public MessageBoxView() : this(string.Empty, string.Empty)
    {
    }

    public MessageBoxView(string title, string message)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("MessageBoxTitleText")!.Text = title;
        this.FindControl<TextBlock>("MessageBoxMessageText")!.Text = message;
    }
}
