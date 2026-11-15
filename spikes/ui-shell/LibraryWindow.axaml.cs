using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UiShellSpike;

public sealed partial class LibraryWindow : Window
{
    public LibraryModel Model { get; } = new();
    public LibraryWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = Model;
        SizeChanged += (_, _) => ArrangeDetails();
    }
    private void ArrangeDetails()
    {
        var narrow = ClientSize.Width < (double)this.FindResource("NarrowBreakpoint")!;
        Classes.Set("narrow", narrow);
        var details = this.FindControl<Border>("Details")!;
        Grid.SetColumn(details, narrow ? 0 : 1);
        Grid.SetRow(details, narrow ? 1 : 0);
        details.Width = narrow ? double.NaN : (double)this.FindResource("DetailsWidth")!;
    }
}
