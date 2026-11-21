using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Playnite.Desktop.Bootstrap;
using Playnite.Desktop.Shell;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;

namespace Playnite.Desktop;

/// <summary>
/// The shell window. It owns its own dependencies as instance objects: the p7-001 composition
/// root keeps its ServiceRegistry private to Program and no static locator is introduced here.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string ShellSmokeFlag = "--shell-smoke";

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

        if (Environment.GetCommandLineArgs().Contains(ShellSmokeFlag, StringComparer.Ordinal))
        {
            InstallShellSmokeHook();
        }
    }

    private void InstallShellSmokeHook()
    {
        Opened += (_, _) =>
        {
            Console.WriteLine("shell: regions=" + string.Join(",", RegionNames));
            Console.WriteLine("shell: about-title=" + Shell.AboutTitle);
            Console.WriteLine("shell: semantic=" + DescribeSemanticTree(this));
            _ = new AboutView(Shell.AboutTitle, Shell.AboutMessage);
            _ = new MessageBoxView("Playnite", "Shell ready");
            Console.WriteLine("shell: pilot=about,messagebox");
            Dispatcher.UIThread.Post(Close, DispatcherPriority.ApplicationIdle);
        };
    }

    private static readonly string[] RegionNames =
        ["TopPanel", "Sidebar", "ContentHost", "Notifications"];

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
                else if (!string.IsNullOrEmpty(automationName))
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
