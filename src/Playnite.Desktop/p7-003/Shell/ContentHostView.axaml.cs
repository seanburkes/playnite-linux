using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;

namespace Playnite.Desktop.Shell;

/// <summary>
/// p7-003 content host. Moved from p7-002 by a one-to-one Cross-Unit-Move with the CLR namespace
/// and class preserved, so ShellView's existing <c>using:Playnite.Desktop.Shell</c> reference stays
/// byte-for-byte valid. The grid, its view-model and the database destination are instance-owned
/// here; no static locator reaches p7-001's private ServiceRegistry.
/// </summary>
public sealed partial class ContentHostView : UserControl
{
    private const string LibrarySmokeFlag = "--library-smoke";

    private readonly CancellationTokenSource lifetime = new();
    private readonly LibraryViewModel model;
    private readonly LibraryGridView grid;
    private readonly bool smoke;

    public ContentHostView()
    {
        AvaloniaXamlLoader.Load(this);
        smoke = Environment.GetCommandLineArgs().Contains(LibrarySmokeFlag, StringComparer.Ordinal);
        var directories = XdgDirectories.Resolve(new LinuxEnvironment());
        model = new LibraryViewModel(Path.Combine(directories.Data, "library.sqlite"));
        grid = this.FindControl<LibraryGridView>("LibraryGrid")!;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await model.LoadAsync(lifetime.Token);
            grid.Apply(model);
            if (smoke)
            {
                EmitLibrarySmoke();
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine("library: load-failed=" + exception.Message);
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs eventArgs)
    {
        lifetime.Cancel();
        model.Dispose();
    }

    /// <summary>Reads the real applied controls and their semantic names before emitting markers.</summary>
    private void EmitLibrarySmoke()
    {
        var rows = grid.FindControl<StackPanel>("Rows")!;
        var firstRow = rows.Children.OfType<TextBlock>().FirstOrDefault();
        var header = grid.FindControl<TextBlock>("HeaderText")!;
        var detailsName = grid.FindControl<TextBlock>("DetailsName")!;
        var detailsId = grid.FindControl<TextBlock>("DetailsId")!;

        var firstText = firstRow?.Text;
        var firstSemantic = firstRow is null ? null : AutomationProperties.GetName(firstRow);
        var expectedFirst = model.Rows.Count > 0 ? model.Rows[0] : null;
        var valid =
            string.Equals(header.Text, "Library", StringComparison.Ordinal)
            && (expectedFirst is null
                ? firstRow is null && string.Equals(detailsName.Text, "(none)", StringComparison.Ordinal)
                : firstRow is not null
                    && string.Equals(firstText, expectedFirst.Name, StringComparison.Ordinal)
                    && string.Equals(firstSemantic, expectedFirst.Name, StringComparison.Ordinal)
                    && string.Equals(detailsName.Text, model.Selected?.Name, StringComparison.Ordinal)
                    && string.Equals(detailsId.Text, model.Selected?.GameId, StringComparison.Ordinal))
            && rows.Children.Count == model.Rows.Count;

        Console.WriteLine(
            $"library: status={model.Status};count={rows.Children.Count};total={model.TotalCount};"
            + $"first={firstText ?? "<missing>"};details={detailsName.Text}|{detailsId.Text};"
            + $"semantic={AutomationProperties.GetName(header)}|{firstSemantic ?? "<missing>"}|"
            + $"{AutomationProperties.GetName(detailsName)}|{AutomationProperties.GetName(detailsId)};"
            + $"valid={valid.ToString().ToLowerInvariant()}");
        if (valid)
        {
            Console.WriteLine("library: grid-loaded");
        }
    }
}
