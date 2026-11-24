using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Playnite.Desktop.Shell;
using Playnite.Desktop.Theme;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class DialogChromeTests
{
    [Fact]
    public async Task Message_dialog_keeps_long_content_readable_in_the_dark_theme()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
        await session.Dispatch(() =>
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            Application.Current.Styles.Add(new PlayniteDarkTheme());

            var message = string.Join("\n", Enumerable.Repeat("This line explains how to recover.", 30));
            var view = new MessageBoxView("Game could not be launched", message);
            var window = new Window { Width = 480, Height = 240, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var scroll = Assert.IsType<ScrollViewer>(view.FindControl<ScrollViewer>("MessageScroll"));
                Assert.True(scroll.Viewport.Height > 0);
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                scroll.ScrollToEnd();
                Assert.True(scroll.Offset.Y > 0);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
