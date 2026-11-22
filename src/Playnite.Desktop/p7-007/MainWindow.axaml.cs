using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Playnite.Desktop.Bootstrap;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Shell;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;

namespace Playnite.Desktop;

/// <summary>
/// The shell window. It owns its own dependencies as instance objects: the p7-001 composition
/// root keeps its ServiceRegistry private to Program and no static locator is introduced here.
/// p7-007 adds in-app keyboard shortcuts and tray lifecycle hooks without altering the visual tree.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string ShellSmokeFlag = "--shell-smoke";

    private readonly IDisposable shortcutSubscription;
    private TrayController? trayController;

    internal ShellViewModel Shell { get; }

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        var probe = new LinuxRegularFileProbe();
        var directories = XdgDirectories.Resolve(new LinuxEnvironment());
        var catalog = BootstrapLocalization.Load(
            Path.Combine(directories.Config, "Localization", "en.json"), probe);
        var dialogs = new AvaloniaDialogService(this);
        Shell = new ShellViewModel(catalog, dialogs);
        DataContext = Shell;

        shortcutSubscription = DesktopShortcutHandler.Attach(
            this,
            new DesktopShortcutCallbacks
            {
                OpenSettings = () => OpenSettingsFromAction(),
                OpenAbout = () => Shell.AboutCommand.Execute(null),
                FocusSearch = FocusSearchFromShortcut,
                Shutdown = RequestShutdownFromShortcut,
            });
        Closed += (_, _) => shortcutSubscription.Dispose();

        if (Environment.GetCommandLineArgs().Contains(ShellSmokeFlag, StringComparer.Ordinal))
        {
            InstallShellSmokeHook();
        }
    }

    public void AttachTray(TrayController controller, bool alreadyOpened = false)
    {
        ArgumentNullException.ThrowIfNull(controller);
        trayController = controller;
        controller.AttachWindow(this, alreadyOpened);
    }

    internal bool OpenSettingsFromAction()
    {
        var button = this.FindControl<ShellView>("ShellRoot")?.FindControl<Button>("SettingsButton");
        if (button is null || !button.IsEnabled)
        {
            return false;
        }

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    internal void FocusSearchFromShortcut()
    {
        foreach (var visual in this.GetVisualDescendants())
        {
            if (visual is LibraryGridView grid)
            {
                var searchBox = grid.FindControl<TextBox>("SearchBox");
                if (searchBox is { IsVisible: true, IsEnabled: true })
                {
                    searchBox.Focus();
                }

                return;
            }
        }
    }

    internal void RequestShutdownFromShortcut()
    {
        if (trayController is not null)
        {
            trayController.RequestExit();
            return;
        }

        Close();
    }

    private void InstallShellSmokeHook()
    {
        Opened += (_, _) =>
        {
            Console.WriteLine("shell: about-title=" + Shell.AboutTitle);
            Console.WriteLine("shell: semantic=" + DescribeSemanticTree(this));

            // Keep both pilot views alive through the assertions. Nothing is shown as a second
            // window; the assertions read the real TextBlocks the views applied.
            var about = new AboutView(Shell.AboutTitle, Shell.AboutMessage);
            var messageBox = new MessageBoxView(Shell.AboutTitle, Shell.AboutMessage);
            var aboutPilot = DescribePilotView(
                about, "AboutTitleText", "AboutMessageText",
                Shell.AboutTitle, Shell.AboutMessage, "About title", "About author");
            var messagePilot = DescribePilotView(
                messageBox, "MessageBoxTitleText", "MessageBoxMessageText",
                Shell.AboutTitle, Shell.AboutMessage, "Message title", "Message text");

            Console.WriteLine("shell: pilot.about=" + aboutPilot.Description);
            Console.WriteLine("shell: pilot.messagebox=" + messagePilot.Description);
            if (aboutPilot.Valid && messagePilot.Valid)
            {
                Console.WriteLine("shell: pilot=about,messagebox");
            }

            GC.KeepAlive(about);
            GC.KeepAlive(messageBox);
            Dispatcher.UIThread.Post(Close, DispatcherPriority.ApplicationIdle);
        };
    }

    /// <summary>Reads a pilot view's real text and semantic names; the success marker is emitted
    /// only when every observed value matches the controlled-catalog expectation.</summary>
    private static (string Description, bool Valid) DescribePilotView(
        Control view,
        string titleName,
        string messageName,
        string expectedTitle,
        string expectedMessage,
        string expectedTitleSemantic,
        string expectedMessageSemantic)
    {
        var title = view.FindControl<TextBlock>(titleName);
        var message = view.FindControl<TextBlock>(messageName);
        var titleText = title?.Text;
        var messageText = message?.Text;
        var titleSemantic = title is null ? null : AutomationProperties.GetName(title);
        var messageSemantic = message is null ? null : AutomationProperties.GetName(message);

        var valid =
            string.Equals(titleText, expectedTitle, StringComparison.Ordinal)
            && string.Equals(messageText, expectedMessage, StringComparison.Ordinal)
            && string.Equals(titleSemantic, expectedTitleSemantic, StringComparison.Ordinal)
            && string.Equals(messageSemantic, expectedMessageSemantic, StringComparison.Ordinal);

        return (
            $"title={titleText ?? "<missing>"};message={messageText ?? "<missing>"};"
                + $"semantic={titleSemantic ?? "<missing>"}|{messageSemantic ?? "<missing>"};"
                + $"valid={valid.ToString().ToLowerInvariant()}",
            valid);
    }

    /// <summary>A name/role projection of the live visual tree, used by the headless smoke.</summary>
    private static string DescribeSemanticTree(Control root)
    {
        var parts = new List<string>();

        void Walk(Visual visual)
        {
            if (visual is Control control)
            {
                var name = control.Name;
                var automationName = AutomationProperties.GetName(control);
                if (!string.IsNullOrEmpty(name))
                {
                    parts.Add(name + ":" + control.GetType().Name + ":x:Name");
                }

                if (!string.IsNullOrEmpty(automationName))
                {
                    parts.Add(automationName + ":" + control.GetType().Name + ":automation");
                }
            }

            foreach (var child in visual.GetVisualChildren())
            {
                Walk(child);
            }
        }

        Walk(root);
        return string.Join(",", parts);
    }
}
