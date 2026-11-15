using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Xunit;

namespace UiShellSpike;

// A semantic projection of rendered controls, not an AT-SPI/automation-peer audit.
internal sealed record SemanticNode(string Type, string Name, string AutomationName,
    double[] Bounds, bool Enabled, bool Visible, string? Text, string? Value, SemanticNode[] Children);

internal static class SemanticTree
{
    internal const double BoundsTolerance = 1; // logical pixels; scale is pinned to 1
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static SemanticNode Capture(Window window) => Project(window, window).Single();

    private static IEnumerable<SemanticNode> Project(Visual visual, Window window)
    {
        var children = visual.GetVisualChildren().SelectMany(child => Project(child, window)).ToArray();
        if (visual is Control control && (control is Window or TextBlock or TextBox or CheckBox
            or ListBox or ListBoxItem or CoverArt || control.Name == "Details"))
        {
            var origin = control.TranslatePoint(default, window);
            Assert.NotNull(origin);
            yield return new SemanticNode(control.GetType().Name, control.Name ?? "",
                AutomationProperties.GetName(control) ?? "",
                new[] { origin.Value.X, origin.Value.Y, control.Bounds.Width, control.Bounds.Height }
                    .Select(value => Math.Round(value, 3, MidpointRounding.AwayFromZero)).ToArray(),
                control.IsEffectivelyEnabled, control.IsEffectivelyVisible,
                control switch
                {
                    TextBlock text => text.Text,
                    CheckBox check => check.Content as string,
                    Window root => root.Title,
                    _ => null
                },
                control switch
                {
                    TextBox text => text.Text,
                    CheckBox check => check.IsChecked?.ToString(),
                    CoverArt cover => cover.Motif.ToString(),
                    _ => null
                }, children);
        }
        else
        {
            foreach (var child in children) yield return child;
        }
    }

    internal static IEnumerable<SemanticNode> Flatten(SemanticNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Flatten));

    internal static void AssertMatches(SemanticNode expected, SemanticNode actual)
    {
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.AutomationName, actual.AutomationName);
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.Visible, actual.Visible);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(4, expected.Bounds.Length);
        Assert.Equal(4, actual.Bounds.Length);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(double.IsFinite(expected.Bounds[i]) && double.IsFinite(actual.Bounds[i]));
            Assert.InRange(Math.Abs(expected.Bounds[i] - actual.Bounds[i]), 0, BoundsTolerance);
        }
        Assert.Equal(expected.Children.Length, actual.Children.Length);
        for (var i = 0; i < expected.Children.Length; i++)
            AssertMatches(expected.Children[i], actual.Children[i]);
    }
}
