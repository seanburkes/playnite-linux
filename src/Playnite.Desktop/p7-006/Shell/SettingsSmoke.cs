using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Playnite.Desktop.Shell;

public sealed partial class ShellView
{
    private async Task RunSettingsSmokeAsync(Window owner, CancellationToken token)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--headless", StringComparer.Ordinal))
        {
            return;
        }
        var edit = args.Contains("--settings-smoke-edit", StringComparer.Ordinal);
        if (!edit && !args.Contains("--settings-smoke-observe", StringComparer.Ordinal))
        {
            return;
        }

        try
        {
            Console.WriteLine($"settings: welcome={WelcomeScreen.IsVisible}; workspace={ShellWorkspace.IsVisible}; sidebar={SidebarRegion.IsVisible}");
            if (edit)
            {
                Console.WriteLine($"settings: welcome-title={WelcomeHeading.Text}; focused={AcknowledgeAction.IsFocused}");
                Click(AcknowledgeAction);
                await acknowledging;
                Console.WriteLine($"settings: acknowledged={ShellWorkspace.IsVisible && !WelcomeScreen.IsVisible}");
            }

            var dialog = await OpenSettingsForSmokeAsync(token);
            var sections = dialog.FindControl<TabControl>("Sections")!;
            var language = dialog.FindControl<ComboBox>("Language")!;
            var sidebar = dialog.FindControl<CheckBox>("ShowSidebar")!;
            Console.WriteLine($"settings: title={dialog.Title}; sections={string.Join(",", sections.Items.Cast<TabItem>().Select(tab => tab.Header))}");
            Console.WriteLine($"settings: language-label={(AutomationProperties.GetLabeledBy(language) as TextBlock)?.Text}; selected={language.SelectedItem}; checked={sidebar.IsChecked}");
            if (edit)
            {
                language.SelectedItem = "de";
                sections.SelectedIndex = 1;
                sidebar.IsChecked = false;
            }
            Click(dialog.FindControl<Button>("CancelButton")!);
            await editing;
            Console.WriteLine($"settings: after-cancel-sidebar={SidebarRegion.IsVisible}");

            if (edit)
            {
                dialog = await OpenSettingsForSmokeAsync(token);
                language = dialog.FindControl<ComboBox>("Language")!;
                sidebar = dialog.FindControl<CheckBox>("ShowSidebar")!;
                Console.WriteLine($"settings: reopened={language.SelectedItem}/{sidebar.IsChecked}");
                language.SelectedItem = "de";
                dialog.FindControl<TabControl>("Sections")!.SelectedIndex = 1;
                sidebar.IsChecked = false;
                Click(dialog.FindControl<Button>("SaveButton")!);
                await editing;
                Console.WriteLine($"settings: after-save-sidebar={SidebarRegion.IsVisible}; button={OpenSettingsAction.Content}");
            }
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Settings smoke failed: {exception}");
        }
        finally
        {
            owner.Close();
        }
    }

    private async Task<SettingsWindow> OpenSettingsForSmokeAsync(CancellationToken token)
    {
        smokeDialog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(OpenSettingsAction);
        return await smokeDialog.Task.WaitAsync(token)
            ?? throw new InvalidOperationException("Settings dialog did not open.");
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
