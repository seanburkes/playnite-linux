namespace Playnite.Desktop.Shell;

/// <summary>Testable dialog boundary for the shell. No static MessageBox and no global locator.</summary>
public interface IDialogService
{
    void ShowAbout(string title, string message);

    void ShowMessageBox(string title, string message);
}
