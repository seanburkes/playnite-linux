using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Playnite.Core.Import;
using Playnite.Steam.Sync;

namespace Playnite.Desktop.Setup;

public sealed partial class FirstTimeStartupWindow : Window
{
    private readonly FirstTimeSetup setup;
    private CancellationTokenSource? operation;
    private bool closeRequested;
    private bool applyImportedSettings;
    private bool ready;
    private bool startupFailed;
    private StackPanel ChoicePanel => this.FindControl<StackPanel>("Choices")!;
    private Border SourceOptions => this.FindControl<Border>("ImportSource")!;
    private TextBox SourceInput => this.FindControl<TextBox>("SourcePath")!;
    private TextBox OriginalPathInput => this.FindControl<TextBox>("WindowsPath")!;
    private TextBlock Status => this.FindControl<TextBlock>("StatusText")!;
    private SelectableTextBlock Details => this.FindControl<SelectableTextBlock>("DetailsText")!;
    private Button KeepAction => this.FindControl<Button>("KeepButton")!;
    private Button FinishAction => this.FindControl<Button>("FinishButton")!;
    private Button CancelAction => this.FindControl<Button>("CancelButton")!;
    private ProgressBar OperationProgress => this.FindControl<ProgressBar>("ProgressBar")!;

    public FirstTimeStartupWindow() : this(new FirstTimeSetup())
    {
    }

    internal FirstTimeStartupWindow(FirstTimeSetup setup)
    {
        this.setup = setup;
        AvaloniaXamlLoader.Load(this);
        Refresh();
        if (setup.HasImportReport)
        {
            Status.Text = "A previous import was published. Keep the existing library to finish setup.";
            Details.Text = Artifacts();
        }
        else if (setup.HasLibrary)
        {
            Status.Text = "A Linux library already exists. Setup will not overwrite it.";
        }
        Opened += (_, _) =>
        {
            if (SourceOptions.IsVisible)
            {
                SourceInput.Focus();
            }
            else if (KeepAction.IsVisible)
            {
                KeepAction.Focus();
            }
        };
        Closing += (_, args) =>
        {
            if (operation is not null)
            {
                args.Cancel = true;
                closeRequested = true;
                operation.Cancel();
            }
        };
    }

    internal Task Pending { get; private set; } = Task.CompletedTask;
    internal event EventHandler? SetupComplete;

    internal void ShowStartupFailure(string diagnostic)
    {
        startupFailed = true;
        Refresh();
        Status.Text = "Could not read Playnite’s settings. Check the profile and start Playnite again.";
        Details.Text = diagnostic;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs args)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the mounted Playnite 10 folder",
            });
            if (folders.Count > 0)
            {
                using var folder = folders[0];
                SourceInput.Text = folder.TryGetLocalPath() ??
                    throw new IOException("The folder picker did not return a local mounted path.");
                SourceInput.Focus();
            }
        }
        catch (Exception exception) when (FirstTimeSetup.IsExpectedFailure(exception) || exception is NotSupportedException)
        {
            Status.Text = "Could not open the folder picker. Enter the mounted path instead.";
            Details.Text = exception.Message;
        }
    }

    private void OnImport(object? sender, RoutedEventArgs args)
    {
        Begin("Reading the Playnite 10 library…", async token =>
        {
            var progress = new ImportProgress(message => Dispatcher.UIThread.Post(() =>
            {
                if (operation?.Token == token && !token.IsCancellationRequested)
                {
                    Status.Text = message;
                }
            }));
            var result = await setup.ImportAsync(SourceInput.Text ?? "", OriginalPathInput.Text ?? "", progress, token);
            ready = true;
            applyImportedSettings = result.Settings is not null;
            FinishAction.Content = result.Settings is null && result.SettingsWarning is not null
                ? "Finish without legacy settings" : "Finish";
            Status.Text = $"Imported {result.Library.ImportedSteam} Steam games. The source was not changed.";
            var unmapped = result.Settings?.UnmappedPaths ?? [];
            var settings = unmapped.Count == 0 ? "" :
                $"\nUnmapped settings paths kept as legacy data: {string.Join(", ", unmapped.Select(item => item.Property).Distinct())}.";
            var preserved = result.Settings is not null && setup.HasNativeSettings
                ? "\nYour existing Linux preferences will be kept." : "";
            Details.Text = Artifacts() + settings + preserved +
                (result.SettingsWarning is null ? "" : "\n" + result.SettingsWarning);
        });
    }

    private void OnSteam(object? sender, RoutedEventArgs args) => Begin("Looking for local Steam games…", async token =>
    {
        var result = await setup.SyncSteamAsync(token);
        ready = true;
        applyImportedSettings = false;
        Status.Text = result.Issues.Any(issue => issue.Reason == SteamSyncReasons.SteamRootMissing)
            ? "No local Steam installation was found. You can finish with an empty library."
            : $"Steam setup found {result.Added + result.Updated + result.Unchanged} library entries to keep.";
        var issues = result.Issues.Where(issue => issue.Reason != SteamSyncReasons.SteamRootMissing)
            .Select(issue => issue.Reason).Distinct().ToArray();
        Details.Text = issues.Length + result.SkippedCount == 0 ? "" :
            $"Steam reported {result.SkippedCount} skipped app(s). {string.Join(", ", issues)}";
    });

    private void OnKeep(object? sender, RoutedEventArgs args)
    {
        if (operation is not null || !setup.CanKeepLibrary)
        {
            return;
        }
        ready = true;
        applyImportedSettings = setup.HasImportReport;
        Status.Text = "Your existing Linux library will be kept.";
        Details.Text = setup.HasImportReport ? Artifacts() : "";
        Refresh();
        FinishAction.Focus();
    }

    private void OnFinish(object? sender, RoutedEventArgs args)
    {
        if (ready)
        {
            Begin("Saving setup…", token => setup.FinishAsync(applyImportedSettings, token), finish: true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs args) => operation?.Cancel();

    private void Begin(string status, Func<CancellationToken, Task> action, bool finish = false)
    {
        if (operation is null)
        {
            Pending = RunAsync(status, action, finish);
        }
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> action, bool finish)
    {
        using var source = new CancellationTokenSource();
        operation = source;
        Status.Text = status;
        if (!finish)
        {
            Details.Text = "";
        }
        Refresh();
        var completed = false;
        try
        {
            await action(source.Token);
            completed = finish;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            Status.Text = "Setup was cancelled. You can try again.";
        }
        catch (FirstTimeSetup.LegacySettingsInvalidException exception)
        {
            applyImportedSettings = false;
            FinishAction.Content = "Finish without legacy settings";
            Status.Text = "The library was migrated, but its old settings cannot be used by this version of Playnite.";
            Details.Text = Artifacts() + "\n" + exception.Message;
        }
        catch (Exception exception)
        {
            Status.Text = finish ? "Could not save setup. Try Finish again." : "Setup failed. Check the details and try again.";
            Details.Text = exception.Message + (!finish && setup.HasLibrary
                ? "\nA Linux library is now present and will not be overwritten." : "");
            if (!FirstTimeSetup.IsExpectedFailure(exception))
            {
                Console.Error.WriteLine($"First-run setup failed ({exception.GetType().Name}).");
            }
        }
        finally
        {
            operation = null;
            Refresh();
        }

        if (closeRequested)
        {
            Close();
        }
        else if (completed)
        {
            SetupComplete?.Invoke(this, EventArgs.Empty);
        }
        else if (ready)
        {
            FinishAction.Focus();
        }
    }

    private string Artifacts() =>
        $"Library backup: {setup.LibraryBackup}\nLibrary report: {setup.LibraryReport}" +
        (File.Exists(setup.SettingsArchive) ? $"\nPreserved settings and path report: {setup.SettingsArchive}" : "");

    private void Refresh()
    {
        ChoicePanel.IsVisible = !startupFailed && !ready;
        ChoicePanel.IsEnabled = operation is null;
        SourceOptions.IsVisible = !setup.HasLibrary;
        KeepAction.IsVisible = setup.CanKeepLibrary;
        CancelAction.IsVisible = operation is not null;
        FinishAction.IsVisible = ready;
        FinishAction.IsEnabled = operation is null;
        OperationProgress.IsVisible = operation is not null;
    }

    private sealed class ImportProgress(Action<string> update) : IProgress<LibraryImportProgress>
    {
        private string? previous;

        public void Report(LibraryImportProgress value)
        {
            var text = value.Stage switch
            {
                LibraryImportStage.Reading => "Reading the Playnite 10 library…",
                LibraryImportStage.Read or LibraryImportStage.Transformed => "Checking source records…",
                LibraryImportStage.AssetPartialCopy or LibraryImportStage.AssetFlushed => "Copying library images…",
                LibraryImportStage.Copied or LibraryImportStage.PartialWrite or LibraryImportStage.Written => "Writing the new library…",
                LibraryImportStage.Verified or LibraryImportStage.BeforeSwap or LibraryImportStage.Publishing
                    or LibraryImportStage.Swapped or LibraryImportStage.ReceiptConfirmed => "Publishing the verified library…",
                LibraryImportStage.Committed or LibraryImportStage.BeforeReportMove => "Saving the migration report…",
                _ => previous,
            };
            if (text is not null && text != previous)
            {
                previous = text;
                update(text);
            }
        }
    }
}
