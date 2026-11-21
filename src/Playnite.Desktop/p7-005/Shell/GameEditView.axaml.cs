using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Playnite.Desktop.Shell;

/// <summary>
/// Real Avalonia controls for the bounded single-game edit and metadata display. Every field is
/// collected from the applied control on Save and pushed through the instance-owned view-model;
/// the host owns the write, the page reload and the grid re-apply. All interactive controls carry
/// an automation name for the headless semantic assertions.
/// </summary>
public sealed partial class GameEditView : UserControl
{
    private GameEditViewModel? model;
    private bool refreshing;

    public event EventHandler? SaveRequested;
    public event EventHandler? CancelRequested;

    public GameEditView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("SaveButton")!.Click += OnSaveClick;
        this.FindControl<Button>("CancelButton")!.Click += OnCancelClick;
        this.FindControl<TextBox>("UserScoreBox")!.TextChanged += OnUserScoreTextChanged;
    }

    public void Apply(GameEditViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        model = viewModel;
        Refresh();
    }

    private void Refresh()
    {
        if (model is null)
        {
            return;
        }

        refreshing = true;
        try
        {
            this.FindControl<TextBlock>("GameIdText")!.Text = model.IsLoaded ? model.GameIdentity : "(none)";
            this.FindControl<TextBox>("NameBox")!.Text = model.Name;
            this.FindControl<TextBox>("DescriptionBox")!.Text = model.Description;
            this.FindControl<TextBox>("VersionBox")!.Text = model.Version;
            this.FindControl<TextBox>("InstallDirectoryBox")!.Text = model.InstallDirectory;
            this.FindControl<CheckBox>("InstalledCheck")!.IsChecked = model.IsInstalled;
            this.FindControl<CheckBox>("HiddenCheck")!.IsChecked = model.Hidden;
            this.FindControl<CheckBox>("FavoriteCheck")!.IsChecked = model.Favorite;
            this.FindControl<TextBox>("UserScoreBox")!.Text = model.UserScoreInput;
            this.FindControl<TextBox>("IconBox")!.Text = model.Icon;
            this.FindControl<TextBox>("CoverImageBox")!.Text = model.CoverImage;
            this.FindControl<TextBox>("BackgroundImageBox")!.Text = model.BackgroundImage;
            this.FindControl<TextBlock>("MetadataText")!.Text = model.MetadataSummary;
        }
        finally
        {
            refreshing = false;
        }

        UpdateUserScoreValidation();
        this.FindControl<TextBlock>("StatusText")!.Text = model.Status;

        var loaded = model.IsLoaded;
        this.FindControl<Button>("SaveButton")!.IsEnabled = loaded;
        this.FindControl<Button>("CancelButton")!.IsEnabled = loaded;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (model is null)
        {
            return;
        }

        if (!CollectFields())
        {
            return;
        }

        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (model is null)
        {
            return;
        }

        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CollectFields()
    {
        var current = model!;
        current.UserScoreInput = this.FindControl<TextBox>("UserScoreBox")!.Text ?? string.Empty;
        if (!current.TryGetValidatedUserScore(out var score))
        {
            UpdateUserScoreValidation();
            return false;
        }

        current.Name = this.FindControl<TextBox>("NameBox")!.Text ?? string.Empty;
        current.Description = this.FindControl<TextBox>("DescriptionBox")!.Text ?? string.Empty;
        current.Version = this.FindControl<TextBox>("VersionBox")!.Text ?? string.Empty;
        current.InstallDirectory = this.FindControl<TextBox>("InstallDirectoryBox")!.Text ?? string.Empty;
        current.IsInstalled = this.FindControl<CheckBox>("InstalledCheck")!.IsChecked ?? false;
        current.Hidden = this.FindControl<CheckBox>("HiddenCheck")!.IsChecked ?? false;
        current.Favorite = this.FindControl<CheckBox>("FavoriteCheck")!.IsChecked ?? false;
        current.UserScore = score;
        current.Icon = this.FindControl<TextBox>("IconBox")!.Text ?? string.Empty;
        current.CoverImage = this.FindControl<TextBox>("CoverImageBox")!.Text ?? string.Empty;
        current.BackgroundImage = this.FindControl<TextBox>("BackgroundImageBox")!.Text ?? string.Empty;
        return true;
    }

    private void OnUserScoreTextChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        if (refreshing || model is null)
        {
            return;
        }

        model.UserScoreInput = this.FindControl<TextBox>("UserScoreBox")!.Text ?? string.Empty;
        model.TryGetValidatedUserScore(out _);
        UpdateUserScoreValidation();
    }

    private void UpdateUserScoreValidation()
    {
        if (model is null)
        {
            return;
        }

        var error = this.FindControl<TextBlock>("UserScoreErrorText")!;
        error.Text = model.UserScoreValidationError ?? string.Empty;
        error.IsVisible = model.UserScoreValidationError is not null;
        this.FindControl<TextBlock>("StatusText")!.Text = model.Status;
    }

    // Smoke-only drivers: they raise the same routed events a user raises, so removing the button
    // wiring or the field collection makes the headless assertions fail instead of passing on a
    // model-only path.
    internal void SetTextForSmoke(string name, string text)
    {
        var box = this.FindControl<TextBox>(name)!;
        box.Text = text;
        box.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent, box));
    }

    internal void SetCheckForSmoke(string name, bool value) =>
        this.FindControl<CheckBox>(name)!.IsChecked = value;

    internal void ClickSaveForSmoke() =>
        this.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal string TextOf(string name) => this.FindControl<TextBlock>(name)!.Text ?? string.Empty;

    internal bool IsVisibleOf(string name) => this.FindControl<Control>(name)!.IsVisible;

    internal string InputTextOf(string name) => this.FindControl<TextBox>(name)!.Text ?? string.Empty;
}
