using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Shell;
using Playnite.Platform.Sessions;
using Playnite.Steam;
using Playnite.Steam.Sync;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class SteamPlaySmokeTests
{
    [Fact]
    public async Task Play_dispatches_only_the_selected_installed_Steam_game_and_reports_the_opener_result()
    {
        await WithLibraryAsync(async (model, view, opener, _) =>
        {
            var list = view.FindControl<ListBox>("GameList")!;
            var play = view.FindControl<Button>("PlayButton")!;
            var status = view.FindControl<TextBlock>("PlayStatus")!;

            for (var row = 0; row < 3; row++)
            {
                list.SelectedIndex = row;
                await view.SelectionTask;
                Assert.False(play.IsEnabled);
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Assert.Empty(opener.Requests);

            list.SelectedIndex = 3;
            await view.SelectionTask;
            Assert.True(play.IsEnabled);
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(play.IsEnabled);
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var request = Assert.Single(opener.Requests);
            Assert.Equal("/usr/bin/xdg-open", request.Executable);
            Assert.Equal(["steam://rungameid/220"], request.Arguments);
            opener.Completion.SetResult(0);
            await view.PlayTask;
            Assert.Equal("Launch requested for D Ready. Check Steam to see if it started.", status.Text);
            Assert.True(play.IsEnabled);

            opener.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            opener.Completion.SetResult(1);
            await view.PlayTask;
            Assert.Equal("The desktop did not accept the Steam launch request for D Ready.", status.Text);
        });
    }

    [Fact]
    public async Task Ending_the_library_lifetime_cancels_the_wait_without_sending_another_request()
    {
        await WithLibraryAsync(async (_, view, opener, lifetime) =>
        {
            view.FindControl<ListBox>("GameList")!.SelectedIndex = 3;
            await view.SelectionTask;
            var play = view.FindControl<Button>("PlayButton")!;
            Assert.True(play.IsEnabled);
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            await lifetime.CancelAsync();
            await view.PlayTask;
            Assert.True(opener.RequestToken.IsCancellationRequested);
            Assert.Single(opener.Requests);
            Assert.False(play.IsEnabled);
        });
    }

    private static async Task WithLibraryAsync(
        Func<LibraryViewModel, LibraryGridView, Opener, CancellationTokenSource, Task> check)
    {
        var directory = Directory.CreateTempSubdirectory("playnite-steam-play-");
        try
        {
            var path = Path.Combine(directory.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(path);
            var steam = new Guid(SteamLibrarySyncService.SteamPluginId);
            var games = new Game[]
            {
                new(Guid.NewGuid(), "400", "A Non-Steam") { IsInstalled = true },
                new(Guid.NewGuid(), "0", "B Invalid") { PluginId = steam, IsInstalled = true },
                new(Guid.NewGuid(), "570", "C Uninstalled") { PluginId = steam },
                new(Guid.NewGuid(), "220", "D Ready") { PluginId = steam, IsInstalled = true },
            };
            await database.WriteAsync(async session =>
            {
                foreach (var game in games)
                {
                    await session.Games.AddAsync(new StoredGame(game));
                }
                return true;
            });

            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
            using var lifetime = new CancellationTokenSource();
            await session.Dispatch(async () =>
            {
                var opener = new Opener();
                var model = new LibraryViewModel(path);
                var view = new LibraryGridView(new SteamPlayRequest(opener));
                var window = new Window { Width = 1280, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(lifetime.Token);
                    window.Show();
                    view.Apply(model);
                    await view.SelectionTask;
                    await check(model, view, opener, lifetime);
                }
                finally
                {
                    await lifetime.CancelAsync();
                    await view.PlayTask;
                    model.Dispose();
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class Opener : ISteamUriOpener
    {
        public List<LaunchRequest> Requests { get; } = [];
        public CancellationToken RequestToken { get; private set; }
        public TaskCompletionSource<int> Completion { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestToken = cancellationToken;
            return Completion.Task.WaitAsync(cancellationToken);
        }
    }
}
