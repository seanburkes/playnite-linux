using Avalonia.Controls;

namespace Playnite.Desktop.Shell;

/// <summary>Desktop-local dialog implementation. It holds only the owning window instance.</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    private readonly Window owner;

    public AvaloniaDialogService(Window owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public void ShowAbout(string title, string message) =>
        Show(new AboutView(title, message), title);

    public void ShowMessageBox(string title, string message) =>
        Show(new MessageBoxView(title, message), title);

    private void Show(Control content, string title) =>
        new Window
        {
            Title = title,
            Width = 480,
            Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content,
        }.Show(owner);
}
