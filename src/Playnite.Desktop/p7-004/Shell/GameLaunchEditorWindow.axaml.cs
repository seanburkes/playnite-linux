using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Core.Models;

namespace Playnite.Desktop.Shell;

internal sealed record GameLaunchEditorResult(
    string Name,
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    string Description,
    bool UseGameMode,
    bool UseMangoHud);

/// <summary>Modal editor for adding a custom native Linux game or configuring a game's Play action.</summary>
public sealed partial class GameLaunchEditorWindow : Window
{
    private readonly Func<string, string?>? toolResolver;
    private readonly TextBox gameNameBox;
    private readonly TextBox executablePathBox;
    private readonly TextBox argumentsBox;
    private readonly TextBox workingDirBox;
    private readonly TextBox descriptionBox;
    private readonly CheckBox gameModeCheckBox;
    private readonly CheckBox mangoHudCheckBox;
    private readonly TextBlock previewText;
    private readonly TextBlock validationErrorText;
    private readonly bool isNewGame;

    public GameLaunchEditorWindow()
        : this(isNewGame: true, initialGame: null, toolResolver: null)
    {
    }

    internal GameLaunchEditorWindow(
        bool isNewGame,
        Game? initialGame,
        Func<string, string?>? toolResolver = null)
    {
        this.isNewGame = isNewGame;
        this.toolResolver = toolResolver;
        AvaloniaXamlLoader.Load(this);

        gameNameBox = this.FindControl<TextBox>("GameNameBox")!;
        executablePathBox = this.FindControl<TextBox>("ExecutablePathBox")!;
        argumentsBox = this.FindControl<TextBox>("ArgumentsBox")!;
        workingDirBox = this.FindControl<TextBox>("WorkingDirBox")!;
        descriptionBox = this.FindControl<TextBox>("DescriptionBox")!;
        gameModeCheckBox = this.FindControl<CheckBox>("GameModeCheckBox")!;
        mangoHudCheckBox = this.FindControl<CheckBox>("MangoHudCheckBox")!;
        previewText = this.FindControl<TextBlock>("PreviewText")!;
        validationErrorText = this.FindControl<TextBlock>("ValidationErrorText")!;

        var titleText = this.FindControl<TextBlock>("DialogTitle")!;
        Title = isNewGame ? "Add Custom Game" : "Configure Game Launch";
        titleText.Text = Title;

        if (initialGame is not null)
        {
            gameNameBox.Text = initialGame.Name ?? string.Empty;
            descriptionBox.Text = initialGame.Description ?? string.Empty;

            var playAction = GameLaunchPlanner.FindFilePlayAction(initialGame);
            if (playAction is not null)
            {
                var spec = GameLaunchPlanner.InspectPlayAction(playAction);
                executablePathBox.Text = spec.Executable;
                argumentsBox.Text = spec.ParseFailed
                    ? spec.RawArguments ?? string.Empty
                    : GameLaunchPlanner.FormatArguments(spec.Arguments);
                workingDirBox.Text = spec.WorkingDirectory ?? string.Empty;
                gameModeCheckBox.IsChecked = spec.UseGameMode;
                mangoHudCheckBox.IsChecked = spec.UseMangoHud;
            }
            else if (!string.IsNullOrWhiteSpace(initialGame.InstallDirectory))
            {
                workingDirBox.Text = initialGame.InstallDirectory;
            }
        }

        gameNameBox.TextChanged += OnInputChanged;
        executablePathBox.TextChanged += OnInputChanged;
        argumentsBox.TextChanged += OnInputChanged;
        workingDirBox.TextChanged += OnInputChanged;
        gameModeCheckBox.IsCheckedChanged += OnCheckChanged;
        mangoHudCheckBox.IsCheckedChanged += OnCheckChanged;

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        this.FindControl<Button>("SaveButton")!.Click += OnSaveClicked;

        UpdatePreview(showValidationError: false);
    }

    internal GameLaunchEditorResult? Result { get; private set; }

    private void OnInputChanged(object? sender, TextChangedEventArgs args) =>
        UpdatePreview(showValidationError: validationErrorText.IsVisible);

    private void OnCheckChanged(object? sender, RoutedEventArgs args) =>
        UpdatePreview(showValidationError: true);

    private PlayActionCreationResult ValidateInputs(out string trimmedName, out string? nameError)
    {
        trimmedName = gameNameBox.Text?.Trim() ?? string.Empty;
        nameError = isNewGame || string.IsNullOrWhiteSpace(trimmedName)
            ? string.IsNullOrWhiteSpace(trimmedName) ? "Game name is required." : null
            : null;

        return GameLaunchPlanner.CreatePlayAction(
            executablePathBox.Text,
            argumentsBox.Text,
            workingDirBox.Text,
            gameModeCheckBox.IsChecked == true,
            mangoHudCheckBox.IsChecked == true,
            toolResolver);
    }

    private void UpdatePreview(bool showValidationError)
    {
        var plan = ValidateInputs(out _, out var nameError);
        var error = nameError ?? plan.Error;
        if (plan.Request is { } req)
        {
            var argSuffix = req.Arguments.Count > 0
                ? " " + GameLaunchPlanner.FormatArguments(req.Arguments)
                : string.Empty;
            previewText.Text = req.Executable + argSuffix;
        }
        else
        {
            previewText.Text = "(invalid configuration)";
        }

        if (showValidationError && !string.IsNullOrWhiteSpace(error))
        {
            validationErrorText.Text = error;
            validationErrorText.IsVisible = true;
        }
        else if (string.IsNullOrWhiteSpace(error))
        {
            validationErrorText.Text = string.Empty;
            validationErrorText.IsVisible = false;
        }
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs args)
    {
        var plan = ValidateInputs(out var trimmedName, out var nameError);
        var error = nameError ?? plan.Error;
        if (!string.IsNullOrWhiteSpace(error) || plan.Action is null)
        {
            validationErrorText.Text = error ?? "Invalid launch configuration.";
            validationErrorText.IsVisible = true;
            return;
        }

        Result = new GameLaunchEditorResult(
            trimmedName,
            executablePathBox.Text?.Trim() ?? string.Empty,
            argumentsBox.Text?.Trim() ?? string.Empty,
            workingDirBox.Text?.Trim() ?? string.Empty,
            descriptionBox.Text?.Trim() ?? string.Empty,
            gameModeCheckBox.IsChecked == true,
            mangoHudCheckBox.IsChecked == true);
        Close(Result);
    }
}
