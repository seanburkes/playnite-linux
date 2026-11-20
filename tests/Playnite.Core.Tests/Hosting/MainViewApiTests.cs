using Playnite.Core.Hosting;
using Playnite.Core.Library;
using Playnite.Core.Models;
using Playnite.Core.Queries;
using Xunit;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace Playnite.Core.Tests.Hosting;

public sealed class MainViewApiTests
{
    private static Game Game(string name) =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "test", name);

    [Fact]
    public async Task GetStateReturnsTheHostSnapshot()
    {
        var game = Game("Portal 2");
        var state = new MainViewState(
            DesktopView.Details,
            FullscreenView.Details,
            SortOrder.LastActivity,
            sortDescending: true,
            LibraryGroupBy.Name,
            [game],
            [game]);
        var host = new FakeMainViewHost { StateHandler = _ => Task.FromResult(state) };
        var api = new MainViewApi(host);

        var response = await api.GetStateAsync();

        Assert.True(response.IsCompleted);
        Assert.NotNull(response.State);
        Assert.Equal(DesktopView.Details, response.State.ActiveDesktopView);
        Assert.Equal(FullscreenView.Details, response.State.ActiveFullscreenView);
        Assert.Equal(SortOrder.LastActivity, response.State.SortOrder);
        Assert.True(response.State.SortDescending);
        Assert.Equal(LibraryGroupBy.Name, response.State.Grouping);
        Assert.Single(response.State.SelectedGames);
        Assert.Single(response.State.FilteredGames);
    }

    [Fact]
    public async Task GetStateWithAMissingHostDegradesWithADiagnostic()
    {
        var api = new MainViewApi();

        var response = await api.GetStateAsync();

        Assert.Equal(HostOperationStatus.Unavailable, response.Status);
        Assert.Null(response.State);
        Assert.Equal(MainViewApi.MissingHostDiagnostic, response.Diagnostic);
    }

    [Fact]
    public async Task GetStateReportsAHostFailureRatherThanThrowing()
    {
        var host = new FakeMainViewHost
        {
            StateHandler = _ => throw new InvalidOperationException("shell gone"),
        };
        var api = new MainViewApi(host);

        var response = await api.GetStateAsync();

        Assert.Equal(HostOperationStatus.Failed, response.Status);
        Assert.Contains("shell gone", response.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStateReportsAHostThatReturnsNoState()
    {
        var host = new FakeMainViewHost { StateHandler = _ => Task.FromResult<MainViewState>(null!) };
        var api = new MainViewApi(host);

        var response = await api.GetStateAsync();

        Assert.Equal(HostOperationStatus.Failed, response.Status);
        Assert.Null(response.State);
        Assert.Contains("no state", response.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStateCancellationThrows()
    {
        using var source = new CancellationTokenSource();
        var host = new FakeMainViewHost
        {
            StateHandler = async token =>
            {
                source.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new MainViewState();
            },
        };
        var api = new MainViewApi(host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetStateAsync(source.Token));
    }

    [Fact]
    public async Task SelectedAndFilteredQueriesDelegateToTheHost()
    {
        var selected = Game("Selected");
        var filtered = Game("Filtered");
        var host = new FakeMainViewHost
        {
            SelectedHandler = _ => Task.FromResult<IReadOnlyList<IGameView>>([selected]),
            FilteredHandler = _ => Task.FromResult<IReadOnlyList<IGameView>>([filtered]),
        };
        var api = new MainViewApi(host);

        var selectedResponse = await api.GetSelectedGamesAsync();
        var filteredResponse = await api.GetFilteredGamesAsync();

        Assert.True(selectedResponse.IsCompleted);
        Assert.Equal("Selected", Assert.Single(selectedResponse.Games).Name);
        Assert.True(filteredResponse.IsCompleted);
        Assert.Equal("Filtered", Assert.Single(filteredResponse.Games).Name);
    }

    [Fact]
    public async Task MissingHostDegradesTheGameQueries()
    {
        var api = new MainViewApi();

        var selected = await api.GetSelectedGamesAsync();
        var filtered = await api.GetFilteredGamesAsync();

        Assert.Equal(HostOperationStatus.Unavailable, selected.Status);
        Assert.Empty(selected.Games);
        Assert.Equal(HostOperationStatus.Unavailable, filtered.Status);
        Assert.Empty(filtered.Games);
    }

    [Fact]
    public async Task AHostFailureInAGameQueryIsReported()
    {
        var host = new FakeMainViewHost
        {
            FilteredHandler = _ => throw new InvalidOperationException("collection gone"),
        };
        var api = new MainViewApi(host);

        var response = await api.GetFilteredGamesAsync();

        Assert.Equal(HostOperationStatus.Failed, response.Status);
        Assert.Empty(response.Games);
        Assert.Contains("collection gone", response.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANullHostGameListIsReportedAsFailed()
    {
        var host = new FakeMainViewHost
        {
            SelectedHandler = _ => Task.FromResult<IReadOnlyList<IGameView>>(null!),
            FilteredHandler = _ => Task.FromResult<IReadOnlyList<IGameView>>(null!),
        };
        var api = new MainViewApi(host);

        var selected = await api.GetSelectedGamesAsync();
        var filtered = await api.GetFilteredGamesAsync();

        Assert.Equal(HostOperationStatus.Failed, selected.Status);
        Assert.Equal(HostOperationStatus.Failed, filtered.Status);
        Assert.Contains("no game list", selected.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponsesCopyTheHostList()
    {
        var games = new List<Game> { Game("Portal 2") };
        var host = new FakeMainViewHost { FilteredHandler = _ => Task.FromResult<IReadOnlyList<IGameView>>(games) };
        var api = new MainViewApi(host);

        var response = await api.GetFilteredGamesAsync();
        games.Clear();

        Assert.Single(response.Games);
    }

    [Fact]
    public async Task CancellationBeforeTheHostRunsThrows()
    {
        var host = new FakeMainViewHost();
        var api = new MainViewApi(host);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetSelectedGamesAsync(source.Token));
    }

    [Fact]
    public void MainViewStateValidatesAndCopiesItsInputs()
    {
        var games = new List<Game> { Game("Portal 2") };

        var state = new MainViewState(filteredGames: games);
        games.Clear();

        Assert.Single(state.FilteredGames);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MainViewState(activeDesktopView: (DesktopView)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MainViewState(activeFullscreenView: (FullscreenView)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MainViewState(sortOrder: (SortOrder)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MainViewState(grouping: (LibraryGroupBy)99));
    }
}
