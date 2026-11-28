using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Core.Plugins;

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
    private CancellationTokenSource? metadataSearch;

    public event EventHandler? SaveRequested;
    public event EventHandler? CancelRequested;

    public GameEditView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("SaveButton")!.Click += OnSaveClick;
        this.FindControl<Button>("CancelButton")!.Click += OnCancelClick;
        this.FindControl<TextBox>("UserScoreBox")!.TextChanged += OnUserScoreTextChanged;
        this.FindControl<TextBox>("NameBox")!.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty && !refreshing)
            {
                ResetMetadataSearch();
            }
        };
        this.FindControl<Button>("SearchMetadataButton")!.Click += OnSearchMetadata;
        this.FindControl<ComboBox>("MetadataResults")!.SelectionChanged += (_, _) => ShowMetadataResult();
        this.FindControl<Button>("ImportMetadataButton")!.Click += OnImportMetadata;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && !IsVisible)
            {
                ResetMetadataSearch();
            }
        };
        Unloaded += (_, _) => ResetMetadataSearch();
    }

    public void Apply(GameEditViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ResetMetadataSearch();
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
        this.FindControl<StackPanel>("MetadataActions")!.IsVisible = model.CanSearchMetadata;
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

        ResetMetadataSearch();
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (model is null)
        {
            return;
        }

        ResetMetadataSearch();
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnSearchMetadata(object? sender, RoutedEventArgs args)
    {
        if (model is not { CanSearchMetadata: true } current || metadataSearch is not null)
        {
            return;
        }

        var name = this.FindControl<TextBox>("NameBox")!.Text ?? string.Empty;
        var status = this.FindControl<TextBlock>("MetadataStatus")!;
        ResetMetadataSearch();
        if (string.IsNullOrWhiteSpace(name))
        {
            status.Text = "Enter a game name before searching.";
            return;
        }

        using var search = new CancellationTokenSource();
        metadataSearch = search;
        var searchButton = this.FindControl<Button>("SearchMetadataButton")!;
        searchButton.IsEnabled = false;
        status.Text = "Searching metadata providers…";
        try
        {
            var results = await current.SearchMetadataAsync(name, search.Token);
            if (search.IsCancellationRequested)
            {
                return;
            }

            var resultsBox = this.FindControl<ComboBox>("MetadataResults")!;
            resultsBox.ItemsSource = results;
            resultsBox.IsVisible = results.Count > 0;
            resultsBox.SelectedIndex = results.Count > 0 ? 0 : -1;
            status.Text = results.Count == 0 ? "No metadata found." : "Select a result and choose fields to import.";
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"game-edit: metadata-search-failed={exception.Message}");
            if (!search.IsCancellationRequested)
            {
                status.Text = "Metadata search failed.";
            }
        }
        finally
        {
            if (ReferenceEquals(metadataSearch, search))
            {
                metadataSearch = null;
                searchButton.IsEnabled = true;
            }
        }
    }

    private void ResetMetadataSearch()
    {
        var search = metadataSearch;
        metadataSearch = null;
        search?.Cancel();
        this.FindControl<Button>("SearchMetadataButton")!.IsEnabled = true;
        var results = this.FindControl<ComboBox>("MetadataResults")!;
        results.ItemsSource = null;
        results.IsVisible = false;
        this.FindControl<TextBlock>("MetadataStatus")!.Text = string.Empty;
        ShowMetadataResult();
    }

    private void ShowMetadataResult()
    {
        var result = this.FindControl<ComboBox>("MetadataResults")!.SelectedItem as PluginGameMetadata;
        var summary = this.FindControl<CheckBox>("ImportSummary")!;
        var score = this.FindControl<CheckBox>("ImportScore")!;
        summary.IsChecked = false;
        score.IsChecked = false;
        summary.IsVisible = result?.Summary is not null;
        score.IsVisible = result?.CommunityScore is not null;
        this.FindControl<Button>("ImportMetadataButton")!.IsVisible = summary.IsVisible || score.IsVisible;

        var lines = new List<string>();
        if (result?.Summary is { } text)
        {
            lines.Add(text);
        }
        if (result?.CommunityScore is int value)
        {
            lines.Add($"Community score: {value}");
        }
        if (result?.Attributes is { } attributes)
        {
            lines.AddRange(attributes.Select(pair => $"{pair.Key}: {pair.Value}"));
        }
        this.FindControl<TextBlock>("MetadataPreview")!.Text = string.Join(Environment.NewLine, lines);
    }

    private void OnImportMetadata(object? sender, RoutedEventArgs args)
    {
        if (model is not { IsLoaded: true } current
            || this.FindControl<ComboBox>("MetadataResults")!.SelectedItem is not PluginGameMetadata result)
        {
            return;
        }

        var importSummary = this.FindControl<CheckBox>("ImportSummary")!.IsChecked == true;
        var importScore = this.FindControl<CheckBox>("ImportScore")!.IsChecked == true;
        var status = this.FindControl<TextBlock>("MetadataStatus")!;
        if (!importSummary && !importScore)
        {
            status.Text = "Choose at least one field to import.";
            return;
        }

        if (importSummary && result.Summary is { } summary)
        {
            this.FindControl<TextBox>("DescriptionBox")!.Text = summary;
        }
        if (importScore)
        {
            current.ImportCommunityScore(result);
            this.FindControl<TextBlock>("MetadataText")!.Text = current.MetadataSummary;
        }
        status.Text = "Imported into this draft. Save to keep the changes.";
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
