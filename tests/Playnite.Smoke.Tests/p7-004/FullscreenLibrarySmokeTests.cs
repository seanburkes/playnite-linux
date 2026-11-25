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
