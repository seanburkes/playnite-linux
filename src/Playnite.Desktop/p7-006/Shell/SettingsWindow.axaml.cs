using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Desktop.Bootstrap;
using Playnite.Settings;

namespace Playnite.Desktop.Shell;

internal sealed partial class SettingsWindow : Window
{
    private readonly DesktopPreferencesStore store;
    private readonly DesktopPreferences original;
    private readonly CancellationToken cancellationToken;
    private bool saving;
    private TabControl SectionTabs => this.FindControl<TabControl>("Sections")!;
    private ComboBox LanguagePicker => this.FindControl<ComboBox>("Language")!;
    private CheckBox SidebarToggle => this.FindControl<CheckBox>("ShowSidebar")!;
    private TextBlock ErrorText => this.FindControl<TextBlock>("SaveError")!;
    private Button SaveAction => this.FindControl<Button>("SaveButton")!;
    private Button CancelAction => this.FindControl<Button>("CancelButton")!;

    internal SettingsWindow(DesktopPreferencesStore store, DesktopPreferences original,
        IReadOnlyList<string> languages, BootstrapLocalization catalog, CancellationToken cancellationToken)
    {
        this.store = store;
        this.original = original;
        this.cancellationToken = cancellationToken;
        AvaloniaXamlLoader.Load(this);

        Title = catalog.Resolve(LOC.SettingsLabel);
        this.FindControl<TabItem>("General")!.Header = catalog.Resolve(LOC.SettingsGeneralLabel);
        this.FindControl<TabItem>("Appearance")!.Header = catalog.Resolve(LOC.SettingsAppearanceLabel);
        this.FindControl<TextBlock>("LanguageLabel")!.Text = catalog.Resolve(LOC.LanguageSettingsLabel);
        this.FindControl<TextBlock>("RestartHint")!.Text = catalog.Resolve(LOC.SettingsRestartNotification);
        SidebarToggle.Content = catalog.Resolve(LOC.ToolsShowOnSidebar);
        SaveAction.Content = catalog.Resolve(LOC.SaveLabel);
        CancelAction.Content = catalog.Resolve(LOC.CancelLabel);
        LanguagePicker.ItemsSource = languages.Contains(original.Language) ? languages : [.. languages, original.Language];
        LanguagePicker.SelectedItem = original.Language;
        SidebarToggle.IsChecked = original.ShowSidebar;
        Closing += (_, args) => args.Cancel = saving && !cancellationToken.IsCancellationRequested;
    }

    private async void OnSave(object? sender, RoutedEventArgs args)
    {
        if (saving || LanguagePicker.SelectedItem is not string language)
        {
            return;
        }

        saving = true;
        SectionTabs.IsEnabled = SaveAction.IsEnabled = CancelAction.IsEnabled = false;
        ErrorText.IsVisible = false;
        DesktopPreferences saved;
        try
        {
            saved = await store.SaveAsync(original, new(language, SidebarToggle.IsChecked == true), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (SettingsException exception)
        {
            Console.Error.WriteLine($"Desktop preferences save failed: {exception.Failure}");
            ErrorText.Text = "Settings could not be saved. Try again or cancel.";
            ErrorText.IsVisible = true;
            return;
        }
        finally
        {
            saving = false;
            SectionTabs.IsEnabled = SaveAction.IsEnabled = CancelAction.IsEnabled = true;
        }

        Close(saved);
    }

    private void OnCancel(object? sender, RoutedEventArgs args) => Close();
}
