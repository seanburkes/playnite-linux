using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Playnite.Desktop.Shell;

/// <summary>
/// The library grid/list/details pilot. Text and semantic names are applied from the view-model by
/// <see cref="Apply"/>, matching the p7-002 pilot convention of reading real applied controls in
/// the headless host instead of unconditional markers.
/// </summary>
public sealed partial class LibraryGridView : UserControl
{
    public LibraryGridView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void Apply(LibraryViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var rows = this.FindControl<StackPanel>("Rows")!;
        rows.Children.Clear();
        for (var index = 0; index < model.Rows.Count; index++)
        {
            var row = model.Rows[index];
            var text = new TextBlock
            {
                Name = "Row" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Text = row.Name,
            };
            AutomationProperties.SetName(text, row.Name);
            rows.Children.Add(text);
        }

        this.FindControl<TextBlock>("DetailsName")!.Text = model.Selected?.Name ?? "(none)";
        this.FindControl<TextBlock>("DetailsId")!.Text = model.Selected?.GameId ?? "(none)";
    }
}
