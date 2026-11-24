using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PathShape = Avalonia.Controls.Shapes.Path;

namespace Playnite.Desktop.Theme;

internal static class PlayniteIcons
{
    private static LinearGradientBrush CreateBrandLogoBrush() => new()
    {
        StartPoint = new RelativePoint(0, 0.2, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.8, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#FFFF2460"), 0.0),
            new GradientStop(Color.Parse("#FFFF5B2E"), 0.52),
            new GradientStop(Color.Parse("#FFFFAE00"), 1.0),
        },
    };

    public static Control CreateBrandLogo(double size = 24)
    {
        var canvas = new Canvas
        {
            Width = 32,
            Height = 24,
        };

        var body = new PathShape
        {
            Data = Geometry.Parse("M 9,3 C 12.2,3 14.0,4.3 16,4.3 C 18.0,4.3 19.8,3 23,3 C 27.8,3 30.8,10.2 31.2,17.2 C 31.5,21.2 28.4,22.8 25.8,20.4 L 22.3,16.8 C 21.4,15.9 20.3,15.5 19.0,15.5 L 13.0,15.5 C 11.7,15.5 10.6,15.9 9.7,16.8 L 6.2,20.4 C 3.6,22.8 0.5,21.2 0.8,17.2 C 1.2,10.2 4.2,3 9,3 Z"),
            Fill = CreateBrandLogoBrush(),
        };

        var leftStick = new Ellipse
        {
            Width = 5.4,
            Height = 5.4,
            Fill = Brushes.White,
        };
        Canvas.SetLeft(leftStick, 6.2);
        Canvas.SetTop(leftStick, 7.6);

        var topBtn = new Ellipse { Width = 2.4, Height = 2.4, Fill = Brushes.White };
        Canvas.SetLeft(topBtn, 21.6);
        Canvas.SetTop(topBtn, 6.0);

        var bottomBtn = new Ellipse { Width = 2.4, Height = 2.4, Fill = Brushes.White };
        Canvas.SetLeft(bottomBtn, 21.6);
        Canvas.SetTop(bottomBtn, 11.4);

        var leftBtn = new Ellipse { Width = 2.4, Height = 2.4, Fill = Brushes.White };
        Canvas.SetLeft(leftBtn, 18.9);
        Canvas.SetTop(leftBtn, 8.7);

        var rightBtn = new Ellipse { Width = 2.4, Height = 2.4, Fill = Brushes.White };
        Canvas.SetLeft(rightBtn, 24.3);
        Canvas.SetTop(rightBtn, 8.7);

        canvas.Children.Add(body);
        canvas.Children.Add(leftStick);
        canvas.Children.Add(topBtn);
        canvas.Children.Add(bottomBtn);
        canvas.Children.Add(leftBtn);
        canvas.Children.Add(rightBtn);

        return new Viewbox
        {
            Width = size,
            Height = size * 0.75,
            Stretch = Stretch.Uniform,
            Child = canvas,
        };
    }
}
