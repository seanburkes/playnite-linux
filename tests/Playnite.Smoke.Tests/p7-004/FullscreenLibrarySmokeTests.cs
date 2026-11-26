using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Desktop.Fullscreen;
using Playnite.Desktop.Integration;
using Playnite.Desktop.Shell;
using Playnite.Platform.Linux;
using Playnite.Platform.Sessions;
using Playnite.Steam;
using Playnite.Steam.Sync;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class FullscreenLibrarySmokeTests
{
    [Fact]
    public async Task Fullscreen_browses_and_plays_without_changing_the_desktop_selection()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("playnite-fullscreen-");
        try
        {
            var path = Path.Combine(directory.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(path, token);
            await database.WriteAsync(async session =>
            {
                for (var index = 0; index <= LibraryViewModel.PageSize; index++)
                {
                    var game = new Game(Guid.NewGuid(), (220 + index).ToString(), $"Game {index:D2}");
                    if (index == 4)
                    {
                        game.PluginId = new Guid(SteamLibrarySyncService.SteamPluginId);
                        game.IsInstalled = true;
                    }
                    if (index == 8)
                    {
                        game.IsInstalled = true;
                        game.GameActions =
                        [
                            new GameAction
                            {
                                Type = GameActionType.File,
                                IsPlayAction = true,
                                Path = "/usr/bin/sleep",
                                Arguments = "1",
                            },
                        ];
                    }
                    await session.Games.AddAsync(new StoredGame(game));
                }
                return true;
            }, token);

            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
            await session.Dispatch(async () =>
            {
                var opener = new Opener();
                var model = new LibraryViewModel(path);
                var desktop = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: true);
                var owner = new Window { Width = 1280, Height = 720, Content = desktop };
                try
                {
                    await model.LoadAsync(token);
                    owner.Show();
                    desktop.Apply(model);
                    var desktopGames = desktop.FindControl<ListBox>("GameList")!;
                    desktopGames.SelectedIndex = 2;
                    await desktop.SelectionTask;
                    var entry = desktop.FindControl<Button>("FullscreenButton")!;
                    Click(entry);

                    var fullscreen = Assert.IsType<FullscreenLibraryWindow>(Assert.Single(owner.OwnedWindows));
                    await fullscreen.OperationTask;
                    Assert.Equal(WindowState.FullScreen, fullscreen.WindowState);
                    var games = fullscreen.FindControl<ListBox>("GameList")!;
                    var play = fullscreen.FindControl<Button>("PlayButton")!;
                    Assert.Equal(0, games.SelectedIndex);
                    Assert.False(play.IsEnabled);

                    fullscreen.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, "");
                    Assert.Equal(4, games.SelectedIndex);
                    Assert.True(play.IsEnabled);
                    fullscreen.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
                    await fullscreen.OperationTask;
                    Assert.Equal(["steam://rungameid/224"], Assert.Single(opener.Requests).Arguments);

                    var next = fullscreen.FindControl<Button>("NextButton")!;
                    var previous = fullscreen.FindControl<Button>("PreviousButton")!;
                    Click(next);
                    await fullscreen.OperationTask;
                    Assert.Equal("Game 50", Assert.IsType<LibraryRow>(games.SelectedItem).Name);
                    Assert.False(next.IsEnabled);
                    Click(previous);
                    await fullscreen.OperationTask;
                    Assert.Equal("Game 00", Assert.IsType<LibraryRow>(games.SelectedItem).Name);

                    fullscreen.HandleController(GamepadButton.Down);
                    Assert.Equal("Game 04", Assert.IsType<LibraryRow>(games.SelectedItem).Name);
                    fullscreen.HandleController(GamepadButton.Accept);
                    await fullscreen.OperationTask;
                    Assert.Equal(2, opener.Requests.Count);
                    Assert.Equal("Game 04", Assert.IsType<LibraryRow>(games.SelectedItem).Name);

                    fullscreen.HandleController(GamepadButton.Down);
                    fullscreen.HandleController(GamepadButton.Accept);
                    await fullscreen.OperationTask;
                    var returned = Assert.IsType<LibraryRow>(games.SelectedItem);
                    Assert.Equal("Game 08", returned.Name);
                    Assert.Equal(1UL, returned.PlayCount);
                    Assert.Same(games, fullscreen.FocusManager!.GetFocusedElement());

                    fullscreen.FindControl<Button>("SearchButton")!.Focus();
                    fullscreen.HandleController(GamepadButton.Accept);
                    Assert.True(fullscreen.FindControl<StackPanel>("SearchPanel")!.IsVisible);
                    fullscreen.HandleController(GamepadButton.Right);
                    fullscreen.HandleController(GamepadButton.Accept);
                    Assert.Equal("b", fullscreen.FindControl<TextBox>("SearchInput")!.Text);
                    fullscreen.HandleController(GamepadButton.Back);
                    Assert.False(fullscreen.FindControl<StackPanel>("SearchPanel")!.IsVisible);

                    fullscreen.HandleController(GamepadButton.Right);
                    fullscreen.HandleController(GamepadButton.Accept);
                    await fullscreen.OperationTask;
                    Assert.Equal(["Game 04", "Game 08"], games.Items.Cast<LibraryRow>().Select(row => row.Name));
                    fullscreen.FindControl<Button>("DetailsButton")!.Focus();
                    fullscreen.HandleController(GamepadButton.Accept);
                    await fullscreen.OperationTask;
                    Assert.Contains("Game 04", fullscreen.FindControl<TextBlock>("DetailsText")!.Text);
                    fullscreen.HandleController(GamepadButton.Back);
                    Assert.False(fullscreen.FindControl<ScrollViewer>("DetailsPanel")!.IsVisible);

                    fullscreen.FindControl<Button>("SearchButton")!.Focus();
                    fullscreen.HandleController(GamepadButton.Accept);
                    for (var step = 0; step < 4; step++)
                    {
                        fullscreen.HandleController(GamepadButton.Down);
                    }
                    // The keyboard footer ends in Cancel. Move left to Apply after entering a query.
                    var search = fullscreen.FindControl<TextBox>("SearchInput")!;
                    search.Text = "08";
                    fullscreen.HandleController(GamepadButton.Left);
                    fullscreen.HandleController(GamepadButton.Accept);
                    await fullscreen.OperationTask;
                    Assert.Equal("Game 08", Assert.IsType<LibraryRow>(Assert.Single(games.Items)).Name);

                    fullscreen.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
                    Assert.False(fullscreen.IsVisible);
                    Assert.Equal(2, desktopGames.SelectedIndex);
                    Assert.Equal("Game 02", model.Selected!.Name);
                }
                finally
                {
                    foreach (var window in owner.OwnedWindows.ToArray())
                    {
                        window.Close();
                    }
                    owner.Close();
                    model.Dispose();
                }
            }, token);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Luna_link_saved_in_desktop_opens_in_Chrome_from_desktop_and_controller()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("playnite-luna-");
        try
        {
            var path = Path.Combine(directory.FullName, "library.sqlite");
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
            await session.Dispatch(async () =>
            {
                var opener = new Opener();
                var browserRequests = new List<LaunchRequest>();
                var luna = new LunaPlayRequest(name => name == "google-chrome" ? "/opt/Google Chrome/chrome" : null,
                    browserRequests.Add);
                var model = new LibraryViewModel(path);
                var view = new LibraryGridView(new SteamPlayRequest(opener), fullscreenPreview: true,
                    new LibrarySteamSyncRunner(), null, null, lunaPlay: luna);
                var owner = new Window { Width = 1250, Height = 720, Content = view };
                try
                {
                    await model.LoadAsync(token);
                    owner.Show();
                    view.Apply(model);
                    owner.UpdateLayout();
                    var listButton = view.FindControl<Button>("ListViewButton")!;
                    var addButton = view.FindControl<Button>("AddGameButton")!;
                    var listBounds = new Rect(listButton.TranslatePoint(default, view)!.Value, listButton.Bounds.Size);
                    var addBounds = new Rect(addButton.TranslatePoint(default, view)!.Value, addButton.Bounds.Size);
                    Assert.False(listBounds.Intersects(addBounds));
                    view.LunaEditorDialogRunner = (dialog, parent) =>
                    {
                        dialog.Opened += (_, _) =>
                        {
                            Assert.Equal("Fortnite", dialog.FindControl<TextBox>("GameNameBox")!.Text);
                            var url = dialog.FindControl<TextBox>("GameUrlBox")!;
                            Assert.Equal(LunaPlayRequest.FortniteUrl, url.Text);
                            url.Text = "https://luna.amazon.com.example.org/game/fortnite";
                            Click(dialog.FindControl<Button>("SaveButton")!);
                            Assert.True(dialog.FindControl<TextBlock>("ValidationErrorText")!.IsVisible);
                            url.Text = LunaPlayRequest.FortniteUrl;
                            Click(dialog.FindControl<Button>("SaveButton")!);
                        };
                        return dialog.ShowDialog<LunaGameInput?>(parent);
                    };
                    Click(view.FindControl<Button>("AddLunaGameButton")!);
                    await view.EditorTask;

                    var selected = Assert.IsType<LibraryRow>(model.Selected);
                    Assert.Equal("Amazon Luna", selected.SourceDisplay);
                    Assert.True(selected.CanPlay);
                    Assert.False(selected.IsInstalled);
                    Assert.False(view.FindControl<Button>("GameTitleButton")!.IsVisible);
                    Assert.Equal("Amazon Luna (Google Chrome)", selected.LaunchTargetDisplay);
                    Click(view.FindControl<Button>("PlayButton")!);
                    await view.PlayTask;
                    Assert.Equal([LunaPlayRequest.FortniteUrl], Assert.Single(browserRequests).Arguments);
                    Assert.Equal("/opt/Google Chrome/chrome", browserRequests[0].Executable);
                    Assert.Contains("Requested Chrome", view.FindControl<TextBlock>("PlayStatus")!.Text);

                    const string festivalUrl = "https://luna.amazon.com/game/fortnite-festival/B0CLKBMYPV";
                    view.LunaEditorDialogRunner = (dialog, parent) =>
                    {
                        dialog.Opened += (_, _) =>
                        {
                            dialog.FindControl<TextBox>("GameNameBox")!.Text = "Fortnite Festival";
                            dialog.FindControl<TextBox>("GameUrlBox")!.Text = festivalUrl;
                            Click(dialog.FindControl<Button>("SaveButton")!);
                        };
                        return dialog.ShowDialog<LunaGameInput?>(parent);
                    };
                    Click(view.FindControl<Button>("ConfigureLaunchButton")!);
                    await view.EditorTask;

                    Click(view.FindControl<Button>("FullscreenButton")!);
                    var fullscreen = Assert.IsType<FullscreenLibraryWindow>(Assert.Single(owner.OwnedWindows));
                    try
                    {
                        await fullscreen.OperationTask;
                        fullscreen.HandleController(GamepadButton.Accept);
                        await fullscreen.OperationTask;
                        var returned = Assert.IsType<LibraryRow>(fullscreen.FindControl<ListBox>("GameList")!.SelectedItem);
                        Assert.Equal(selected.Id, returned.Id);
                        Assert.Equal("Fortnite Festival", returned.Name);
                        Assert.Equal(2, browserRequests.Count);
                        Assert.Equal([festivalUrl], browserRequests[1].Arguments);
                        Assert.Same(fullscreen.FindControl<ListBox>("GameList"), fullscreen.FocusManager!.GetFocusedElement());
                    }
                    finally
                    {
                        fullscreen.Close();
                    }

                    var stored = (await model.GetStoredGameAsync(selected.Id, token))!.Value;
                    Assert.Equal("/game/fortnite-festival/B0CLKBMYPV", stored.GameId);
                    Assert.Equal(GameActionType.URL, Assert.Single(stored.GameActions!).Type);
                    Assert.Equal(0UL, stored.PlayCount);
                    Assert.Equal(0UL, stored.Playtime);
                    Assert.Empty(opener.Requests);
                }
                finally
                {
                    owner.Close();
                    model.Dispose();
                }
            }, token);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("http://luna.amazon.com/game/fortnite")]
    [InlineData("https://luna.amazon.com:444/game/fortnite")]
    [InlineData("https://user@luna.amazon.com/game/fortnite")]
    [InlineData("https://luna.amazon.com/account")]
    [InlineData("https://luna.amazon.com/game/../account")]
    [InlineData("https://luna.amazon.com/game/")]
    [InlineData("https://luna.amazon.com/game//")]
    [InlineData("https://luna.amazon.com/game/%2F")]
    [InlineData("https://luna.amazon.com/game/%00")]
    public async Task Invalid_Luna_link_is_rejected_before_browser_lookup(string url)
    {
        var luna = new LunaPlayRequest(_ => throw new InvalidOperationException("Chrome lookup for invalid link"));
        var status = await luna.SendAsync(url, "Fortnite", TestContext.Current.CancellationToken);
        Assert.False(status.Requested);
        Assert.Equal(LunaPlayRequest.InvalidUrlMessage, status.Status);
    }

    [Fact]
    public async Task Luna_reports_missing_Chrome_and_honors_cancellation()
    {
        var luna = new LunaPlayRequest(_ => null);
        var status = await luna.SendAsync(LunaPlayRequest.FortniteUrl, "Fortnite", TestContext.Current.CancellationToken);
        Assert.False(status.Requested);
        Assert.Contains("Chrome was not found", status.Status);
        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = luna.SendAsync(LunaPlayRequest.FortniteUrl, "Fortnite", new CancellationToken(canceled: true));
        });
    }

    [Fact]
    public async Task Luna_reports_when_Chrome_cannot_start()
    {
        var directory = Directory.CreateTempSubdirectory("playnite-luna-chrome-");
        try
        {
            var executable = Path.Combine(directory.FullName, "chrome");
            await File.WriteAllTextAsync(executable, string.Empty, TestContext.Current.CancellationToken);
            var luna = new LunaPlayRequest(configuredExecutable: executable);
            var result = await luna.SendAsync(LunaPlayRequest.FortniteUrl, "Fortnite", TestContext.Current.CancellationToken);
            Assert.False(result.Requested);
            Assert.Contains("Could not open Google Chrome", result.Status);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void Click(Button button)
    {
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private sealed class Opener : ISteamUriOpener
    {
        public List<LaunchRequest> Requests { get; } = [];

        public Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(0);
        }
    }
}
