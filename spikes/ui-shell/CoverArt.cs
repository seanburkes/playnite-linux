using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UiShellSpike;

// Invented geometric covers; brushes come from the same swappable theme as the controls.
public sealed class CoverArt : Control
{
    public static readonly StyledProperty<int> MotifProperty = AvaloniaProperty.Register<CoverArt, int>(nameof(Motif));
    public int Motif { get => GetValue(MotifProperty); set => SetValue(MotifProperty, value); }
    static CoverArt() => AffectsRender<CoverArt>(MotifProperty);
    public override void Render(DrawingContext context)
    {
        var background = (IBrush)this.FindResource("CoverBrush")!;
        var accent = (IBrush)this.FindResource("AccentBrush")!;
        context.FillRectangle(background, new Rect(Bounds.Size));
        var radius = 16 + Motif * 3;
        context.DrawEllipse(accent, null, new Point(Bounds.Width / 2, Bounds.Height / 2), radius, radius);
        var pen = new Pen(accent, 2);
        for (var i = 0; i <= Motif; i++)
            context.DrawLine(pen, new Point(12, 16 + i * 9), new Point(Bounds.Width - 12, 16 + i * 9));
    }
}
