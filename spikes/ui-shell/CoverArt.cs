using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UiShellSpike;

// Invented geometric covers; brushes come from the same swappable theme as the controls.
public sealed class CoverArt : Control
{
    public static readonly StyledProperty<int> MotifProperty = AvaloniaProperty.Register<CoverArt, int>(nameof(Motif));
    public static readonly StyledProperty<IBrush> FillProperty = AvaloniaProperty.Register<CoverArt, IBrush>(nameof(Fill), Brushes.Transparent);
    public static readonly StyledProperty<IBrush> AccentProperty = AvaloniaProperty.Register<CoverArt, IBrush>(nameof(Accent), Brushes.Transparent);
    public int Motif { get => GetValue(MotifProperty); set => SetValue(MotifProperty, value); }
    public IBrush Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    static CoverArt() => AffectsRender<CoverArt>(MotifProperty, FillProperty, AccentProperty);
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Fill, new Rect(Bounds.Size));
        var radius = 16 + Motif * 3;
        context.DrawEllipse(Accent, null, new Point(Bounds.Width / 2, Bounds.Height / 2), radius, radius);
        var pen = new Pen(Accent, 2);
        for (var i = 0; i <= Motif; i++)
            context.DrawLine(pen, new Point(12, 16 + i * 9), new Point(Bounds.Width - 12, 16 + i * 9));
    }
}
