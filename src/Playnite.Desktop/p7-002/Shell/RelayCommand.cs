using System.Windows.Input;

namespace Playnite.Desktop.Shell;

/// <summary>Minimal in-box ICommand adapter for shell actions; carries no service state.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action execute;

    public RelayCommand(Action execute)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
