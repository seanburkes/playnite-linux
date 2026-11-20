using Playnite.Core.Hosting;
using Playnite.Core.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using Xunit;

namespace Playnite.Core.Tests.SdkContracts;

/// <summary>
/// One fake-host contract test per interface moved into Playnite.SDK.Abstractions. Each fake
/// implements the abstraction at compile time, so a removed member or a changed signature fails
/// the build; each test then exercises every member through the real Core service.
/// </summary>
public sealed class SdkHostContractTests
{
    private static readonly Guid GameId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void IGameView_id_and_name_are_exercised()
    {
        IGameView view = new ContractGameView { Id = GameId, Name = "contract-game" };

        Assert.Equal(GameId, view.Id);
        Assert.Equal("contract-game", view.Name);
    }

    [Fact]
    public async Task INotificationHost_is_exercised_by_NotificationsApi()
    {
        var host = new ContractNotificationHost();
        var api = new NotificationsApi(host);

        var published = await api.PublishAsync("contract", "message", NotificationType.Info);
        var dismissed = await api.DismissAsync("contract");

        Assert.True(published.IsSuccess);
        Assert.True(dismissed.IsDismissed);
        Assert.Equal("contract", Assert.Single(host.Presented));
        Assert.Equal("contract", Assert.Single(host.Dismissed));
    }

    [Fact]
    public async Task INotificationsAPI_every_member_is_exercised()
    {
        INotificationsAPI api = new NotificationsApi(new ContractNotificationHost());
        var changed = 0;
        api.Changed += (_, _) => changed++;

        Assert.Empty(api.Messages);
        Assert.Equal(0, api.Count);

        var first = await api.PublishAsync("one", "first", NotificationType.Info);
        var second = await api.PublishAsync(
            new NotificationMessage("two", "second", NotificationType.Error));
        var dismissed = await api.DismissAsync("one");
        var all = await api.DismissAllAsync();

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.True(dismissed.IsDismissed);
        Assert.Equal(1, all.Dismissed);
        Assert.Equal(0, api.Count);
        Assert.True(changed >= 4);
    }

    [Fact]
    public async Task IDialogHost_is_exercised_by_DialogsFactory()
    {
        var host = new ContractDialogHost();
        var factory = new DialogsFactory(host);

        await factory.ShowMessageAsync(new MessageDialogRequest("hello"));
        await factory.ShowErrorAsync("error");
        await factory.SelectFileAsync(new FileDialogRequest(FileDialogKind.OpenFile));
        await factory.SelectFolderAsync("folder");
        await factory.SaveFileAsync("save");
        await factory.SelectStringAsync(new TextInputDialogRequest("name"));

        Assert.Equal(2, host.Messages.Count);
        Assert.Equal(3, host.Files.Count);
        Assert.Single(host.Strings);
    }

    [Fact]
    public async Task IDialogsFactory_every_member_is_exercised()
    {
        IDialogsFactory factory = new DialogsFactory(new ContractDialogHost());

        Assert.True((await factory.ShowMessageAsync(new MessageDialogRequest("hello"))).IsCompleted);
        Assert.True((await factory.ShowErrorAsync("error")).IsCompleted);
        Assert.True((await factory.SelectFileAsync(new FileDialogRequest(FileDialogKind.OpenFile))).IsCompleted);
        Assert.True((await factory.SelectFolderAsync()).IsCompleted);
        Assert.True((await factory.SaveFileAsync()).IsCompleted);
        Assert.True((await factory.SelectStringAsync(new TextInputDialogRequest("name"))).IsCompleted);
    }

    [Fact]
    public async Task IMainViewHost_is_exercised_by_MainViewApi()
    {
        var game = new Game(GameId, "contract-game", "Contract Game");
        var host = new ContractMainViewHost
        {
            State = new MainViewState(selectedGames: [game], filteredGames: [game]),
            Selected = [game],
            Filtered = [game],
        };
        var api = new MainViewApi(host);

        Assert.True((await api.GetStateAsync()).IsCompleted);
        Assert.True((await api.GetSelectedGamesAsync()).IsCompleted);
        Assert.True((await api.GetFilteredGamesAsync()).IsCompleted);
        Assert.Equal(1, host.StateCalls);
        Assert.Equal(1, host.SelectedCalls);
        Assert.Equal(1, host.FilteredCalls);
    }

    [Fact]
    public async Task IMainViewAPI_every_member_is_exercised()
    {
        var game = new Game(GameId, "contract-game", "Contract Game");
        IMainViewAPI api = new MainViewApi(new ContractMainViewHost
        {
            State = new MainViewState(selectedGames: [game], filteredGames: [game]),
            Selected = [game],
            Filtered = [game],
        });

        var state = await api.GetStateAsync();
        var selected = await api.GetSelectedGamesAsync();
        var filtered = await api.GetFilteredGamesAsync();

        Assert.Equal(DesktopView.Grid, state.State!.ActiveDesktopView);
        Assert.Equal(GameId, Assert.Single(selected.Games).Id);
        Assert.Equal(GameId, Assert.Single(filtered.Games).Id);
    }

    private sealed class ContractGameView : IGameView
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    private sealed class ContractNotificationHost : INotificationHost
    {
        public List<string> Presented { get; } = new();
        public List<string> Dismissed { get; } = new();

        public Task<NotificationHostResult> PresentAsync(
            NotificationMessage message,
            CancellationToken cancellationToken = default)
        {
            Presented.Add(message.Id);
            return Task.FromResult(new NotificationHostResult(NotificationHostStatus.Presented));
        }

        public Task<NotificationHostResult> DismissAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            Dismissed.Add(id);
            return Task.FromResult(new NotificationHostResult(NotificationHostStatus.Presented));
        }
    }

    private sealed class ContractDialogHost : IDialogHost
    {
        public List<MessageDialogRequest> Messages { get; } = new();
        public List<FileDialogRequest> Files { get; } = new();
        public List<TextInputDialogRequest> Strings { get; } = new();

        public Task<MessageDialogResponse> ShowMessageAsync(
            MessageDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            return Task.FromResult(new MessageDialogResponse(HostOperationStatus.Completed, MessageDialogResult.Ok));
        }

        public Task<FileDialogResponse> SelectFileAsync(
            FileDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            Files.Add(request);
            return Task.FromResult(new FileDialogResponse(HostOperationStatus.Completed, ["selected"]));
        }

        public Task<TextInputDialogResponse> SelectStringAsync(
            TextInputDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            Strings.Add(request);
            return Task.FromResult(new TextInputDialogResponse(HostOperationStatus.Completed, true, "value"));
        }
    }

    private sealed class ContractMainViewHost : IMainViewHost
    {
        public MainViewState State { get; init; } = new();
        public IReadOnlyList<IGameView> Selected { get; init; } = [];
        public IReadOnlyList<IGameView> Filtered { get; init; } = [];
        public int StateCalls { get; private set; }
        public int SelectedCalls { get; private set; }
        public int FilteredCalls { get; private set; }

        public Task<MainViewState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            StateCalls++;
            return Task.FromResult(State);
        }

        public Task<IReadOnlyList<IGameView>> GetSelectedGamesAsync(CancellationToken cancellationToken = default)
        {
            SelectedCalls++;
            return Task.FromResult(Selected);
        }

        public Task<IReadOnlyList<IGameView>> GetFilteredGamesAsync(CancellationToken cancellationToken = default)
        {
            FilteredCalls++;
            return Task.FromResult(Filtered);
        }
    }
}
