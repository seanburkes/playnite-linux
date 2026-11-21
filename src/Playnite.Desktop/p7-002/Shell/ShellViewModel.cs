using System.Windows.Input;
using Playnite.Desktop.Bootstrap;

namespace Playnite.Desktop.Shell;

/// <summary>
/// Shell state and commands. Dependencies arrive through the constructor; dialogs are reached
/// only through <see cref="IDialogService"/>, never a static MessageBox.
/// </summary>
public sealed class ShellViewModel
{
    private readonly IDialogService dialogs;

    public ShellViewModel(BootstrapLocalization localization, IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(localization);
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        AboutTitle = localization.Resolve(LOC.AboutWindowTitle);
        AboutMessage = localization.Resolve(LOC.AboutAuthor);
        AboutCommand = new RelayCommand(() => this.dialogs.ShowAbout(AboutTitle, AboutMessage));
        MessageBoxCommand = new RelayCommand(() => this.dialogs.ShowMessageBox(AboutTitle, AboutMessage));
    }

    public string AboutTitle { get; }

    public string AboutMessage { get; }

    public ICommand AboutCommand { get; }

    public ICommand MessageBoxCommand { get; }
}
