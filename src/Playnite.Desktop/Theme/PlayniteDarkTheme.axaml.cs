using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Playnite.Desktop.Theme;

/// <summary>
/// Shared dark palette and control styles for Playnite Desktop.
/// </summary>
public sealed class PlayniteDarkTheme : Styles
{
    public PlayniteDarkTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
