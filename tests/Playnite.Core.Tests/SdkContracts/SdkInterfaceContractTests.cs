using Playnite.Core.Descriptions;
using Playnite.Core.Hosting;
using Playnite.Core.Models;
using Playnite.Core.UriHandling;
using Playnite.SDK.Data;
using Xunit;
using SdkModels = Playnite.SDK.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Descriptions;

namespace Playnite.Core.Tests.SdkContracts;

/// <summary>
/// One fake-host contract test per SDK-facing interface. The fakes implement the interfaces at
/// compile time, so a removed member or a changed signature fails the build before the test runs;
/// each test then exercises every member through the real Core service.
/// </summary>
public sealed class SdkInterfaceContractTests
{
    private static readonly Guid GameId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // The merged p6-006 move projects a Core game through GameViewAdapter; the Core model does
    // not implement the abstraction type itself.
    private static IGameView View(Game game) => new GameViewAdapter(game);

    [Fact]
    public void IDataSerializer_every_member_is_exercised()
    {
        var serializer = new ContractDataSerializer();
        using var stream = new MemoryStream();

        Assert.Equal(string.Empty, serializer.ToYaml(null));
        Assert.Null(serializer.FromYaml<ContractPayload>("yaml"));
        Assert.True(serializer.TryFromYaml<ContractPayload>("yaml", out _));
        Assert.True(serializer.TryFromYaml<ContractPayload>("yaml", out _, out _));
        Assert.Null(serializer.FromYamlFile<ContractPayload>("file.yaml"));
        Assert.True(serializer.TryFromYamlFile<ContractPayload>("file.yaml", out _));
        Assert.True(serializer.TryFromYamlFile<ContractPayload>("file.yaml", out _, out _));
        Assert.Equal(string.Empty, serializer.ToJson(null));
        serializer.ToJsonStream(null, stream);
        Assert.Null(serializer.FromJson<ContractPayload>("json"));
        Assert.True(serializer.TryFromJson<ContractPayload>("json", out _));
        Assert.True(serializer.TryFromJson<ContractPayload>("json", out _, out _));
        Assert.Null(serializer.FromJsonStream<ContractPayload>(stream));
        Assert.True(serializer.TryFromJsonStream<ContractPayload>(stream, out _));
        Assert.True(serializer.TryFromJsonStream<ContractPayload>(stream, out _, out _));
        Assert.Null(serializer.FromJsonFile<ContractPayload>("file.json"));
        Assert.True(serializer.TryFromJsonFile<ContractPayload>("file.json", out _));
        Assert.True(serializer.TryFromJsonFile<ContractPayload>("file.json", out _, out _));
        Assert.Null(serializer.FromToml<ContractPayload>("toml"));
        Assert.True(serializer.TryFromToml<ContractPayload>("toml", out _));
        Assert.True(serializer.TryFromToml<ContractPayload>("toml", out _, out _));
        Assert.Null(serializer.FromTomlFile<ContractPayload>("file.toml"));
        Assert.True(serializer.TryFromTomlFile<ContractPayload>("file.toml", out _));
        Assert.True(serializer.TryFromTomlFile<ContractPayload>("file.toml", out _, out _));
        Assert.Null(serializer.GetClone<ContractPayload>(null));
        Assert.Null(serializer.GetClone<ContractPayload, ContractPayload>(null));
        Assert.True(serializer.AreObjectsEqual(null, null));

        Assert.Equal(27, serializer.Calls.Count);
        Assert.Equal(27, serializer.Calls.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void IIdentifiable_id_is_exercised()
    {
        SdkModels.IIdentifiable identifiable = new ContractIdentifiable { Id = GameId };
        Assert.Equal(GameId, identifiable.Id);
    }

    [Fact]
    public void IGameView_id_and_name_are_exercised()
    {
        SdkModels.IGameView view = new ContractGameView { Id = GameId, Name = "contract-game" };
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
        Assert.Single(host.Presented);
        Assert.Equal("contract", Assert.Single(host.Dismissed));
    }

    [Fact]
    public async Task INotificationsAPI_every_member_is_exercised()
    {
        var host = new ContractNotificationHost();
        INotificationsAPI api = new NotificationsApi(host);
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
        var view = View(game);
        var host = new ContractMainViewHost
        {
            State = new MainViewState(selectedGames: [view], filteredGames: [view]),
            Selected = [view],
            Filtered = [view],
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
        var view = View(game);
        IMainViewAPI api = new MainViewApi(new ContractMainViewHost
        {
            State = new MainViewState(selectedGames: [view], filteredGames: [view]),
            Selected = [view],
            Filtered = [view],
        });

        var state = await api.GetStateAsync();
        var selected = await api.GetSelectedGamesAsync();
        var filtered = await api.GetFilteredGamesAsync();

        Assert.Equal(DesktopView.Grid, state.State!.ActiveDesktopView);
        Assert.Equal(GameId, Assert.Single(selected.Games).Id);
        Assert.Equal(GameId, Assert.Single(filtered.Games).Id);
    }

    [Fact]
    public async Task IUriCommandHost_is_exercised_by_UriHandlerApi()
    {
        var host = new ContractUriCommandHost();
        var api = new UriHandlerApi(host);

        Assert.True((await api.DispatchAsync($"playnite://playnite/start/{GameId}")).Succeeded);
        Assert.True((await api.DispatchAsync($"playnite://playnite/showgame/{GameId}")).Succeeded);
        Assert.True((await api.DispatchAsync("playnite://playnite/search/term")).Succeeded);
        Assert.True((await api.DispatchAsync("playnite://playnite/view/desktop")).Succeeded);
        Assert.True((await api.DispatchAsync("playnite://playnite/filter/installed/true")).Succeeded);

        Assert.Equal(new[] { "start", "showgame", "search", "view", "filter" }, host.Calls);
    }

    [Fact]
    public async Task IUriHandlerAPI_every_member_is_exercised()
    {
        var concrete = new UriHandlerApi(new ContractUriCommandHost());
        IUriHandlerAPI api = concrete;
        var arguments = Array.Empty<string>();

        api.RegisterSource("contract", args => arguments = args.Arguments);
        Assert.True((await concrete.DispatchAsync("playnite://contract/one/two")).Succeeded);
        Assert.Equal(new[] { "one", "two" }, arguments);

        api.RemoveSource("contract");
        Assert.Equal(
            UriDispatchStatus.UnhandledSource,
            (await concrete.DispatchAsync("playnite://contract/one")).Status);
    }

    [Fact]
    public async Task IHtmlDescriptionProvider_is_exercised_by_HtmlDescriptionService()
    {
        var provider = new ContractHtmlDescriptionProvider();
        var service = new HtmlDescriptionService(provider, new ContractHtmlSanitizer());

        var result = await service.RenderAsync("game-1");

        Assert.True(result.IsRendered);
        Assert.Equal("game-1", Assert.Single(provider.Calls));
    }

    [Fact]
    public async Task IHtmlSanitizer_is_exercised_by_HtmlDescriptionService()
    {
        var sanitizer = new ContractHtmlSanitizer();
        var service = new HtmlDescriptionService(
            new ContractHtmlDescriptionProvider(),
            sanitizer);

        var result = await service.RenderAsync("game-1");

        Assert.True(result.IsRendered);
        Assert.Equal("<b>safe</b>", Assert.Single(sanitizer.Calls));
    }

    private sealed class ContractPayload
    {
        public string? Value { get; set; }
    }

    private sealed class ContractIdentifiable : SdkModels.IIdentifiable
    {
        public Guid Id { get; init; }
    }

    private sealed class ContractGameView : SdkModels.IGameView
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    private sealed class ContractDataSerializer : IDataSerializer
    {
        public List<string> Calls { get; } = new();

        public string ToYaml(object? obj)
        {
            Calls.Add(nameof(ToYaml));
            return string.Empty;
        }

        public T? FromYaml<T>(string yaml) where T : class
        {
            Calls.Add(nameof(FromYaml));
            return null;
        }

        public bool TryFromYaml<T>(string yaml, out T? content) where T : class
        {
            Calls.Add(nameof(TryFromYaml));
            content = null;
            return true;
        }

        public bool TryFromYaml<T>(string yaml, out T? content, out Exception? error) where T : class
        {
            Calls.Add(nameof(TryFromYaml) + "-error");
            content = null;
            error = null;
            return true;
        }

        public T? FromYamlFile<T>(string filePath) where T : class
        {
            Calls.Add(nameof(FromYamlFile));
            return null;
        }

        public bool TryFromYamlFile<T>(string filePath, out T? content) where T : class
        {
            Calls.Add(nameof(TryFromYamlFile));
            content = null;
            return true;
        }

        public bool TryFromYamlFile<T>(string filePath, out T? content, out Exception? error) where T : class
        {
            Calls.Add(nameof(TryFromYamlFile) + "-error");
            content = null;
            error = null;
            return true;
        }

        public string ToJson(object? obj, bool formatted = false)
        {
            Calls.Add(nameof(ToJson));
            return string.Empty;
        }

        public void ToJsonStream(object? obj, Stream stream, bool formatted = false)
        {
            Calls.Add(nameof(ToJsonStream));
        }

        public T? FromJson<T>(string json) where T : class
        {
            Calls.Add(nameof(FromJson));
            return null;
        }

        public bool TryFromJson<T>(string json, out T? content) where T : class
        {
            Calls.Add(nameof(TryFromJson));
            content = null;
            return true;
        }

        public bool TryFromJson<T>(string json, out T? content, out Exception? error) where T : class
        {
            Calls.Add(nameof(TryFromJson) + "-error");
            content = null;
            error = null;
            return true;
        }

        public T? FromJsonStream<T>(Stream stream) where T : class
        {
            Calls.Add(nameof(FromJsonStream));
            return null;
        }

        public bool TryFromJsonStream<T>(Stream stream, out T? content) where T : class
        {
            Calls.Add(nameof(TryFromJsonStream));
            content = null;
            return true;
        }

        public bool TryFromJsonStream<T>(Stream stream, out T? content, out Exception? error) where T : class
        {
            Calls.Add(nameof(TryFromJsonStream) + "-error");
            content = null;
            error = null;
            return true;
        }

        public T? FromJsonFile<T>(string filePath) where T : class
        {
            Calls.Add(nameof(FromJsonFile));
            return null;
        }

        public bool TryFromJsonFile<T>(string filePath, out T? content) where T : class
        {
            Calls.Add(nameof(TryFromJsonFile));
            content = null;
            return true;
        }

        public bool TryFromJsonFile<T>(string filePath, out T? content, out Exception? error) where T : class
        {
            Calls.Add(nameof(TryFromJsonFile) + "-error");
            content = null;
            error = null;
            return true;
        }

        public T? FromToml<T>(string toml) where T : class
        {
            Calls.Add(nameof(FromToml));
            return null;
        }

        public bool TryFromToml<T>(string toml, out T? content) where T : class
        {
            Calls.Add(nameof(TryFromToml));
            content = null;
            return true;
        }

        public bool TryFromToml<T>(string toml, out T? content, out Exception? error) where T : class
        {
            Calls.Add(nameof(TryFromToml) + "-error");
            content = null;
            error = null;
            return true;
        }

        public T? FromTomlFile<T>(string filePath) where T : class
        {
            Calls.Add(nameof(FromTomlFile));
            return null;
        }

        public bool TryFromTomlFile<T>(string filePath, out T? content) where T : class
        {
            Calls.Add(nameof(TryFromTomlFile));
            content = null;
            return true;
        }

        public bool TryFromTomlFile<T>(string filePath, out T? content, out Exception? error) where T : class
        {
            Calls.Add(nameof(TryFromTomlFile) + "-error");
            content = null;
            error = null;
            return true;
        }

        public T? GetClone<T>(T? source) where T : class
        {
            Calls.Add(nameof(GetClone));
            return null;
        }

        public U? GetClone<T, U>(T? source) where T : class where U : class
        {
            Calls.Add(nameof(GetClone) + "-two");
            return null;
        }

        public bool AreObjectsEqual(object? object1, object? object2)
        {
            Calls.Add(nameof(AreObjectsEqual));
            return true;
        }
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

    private sealed class ContractUriCommandHost : IUriCommandHost
    {
        public List<string> Calls { get; } = new();

        public ValueTask<UriCommandResult> StartGameAsync(
            Guid gameId,
            int? actionIndex,
            CancellationToken cancellationToken)
        {
            Calls.Add("start");
            return ValueTask.FromResult(UriCommandResult.Completed());
        }

        public ValueTask<UriCommandResult> ShowGameAsync(Guid gameId, CancellationToken cancellationToken)
        {
            Calls.Add("showgame");
            return ValueTask.FromResult(UriCommandResult.Completed());
        }

        public ValueTask<UriCommandResult> SearchAsync(string? term, CancellationToken cancellationToken)
        {
            Calls.Add("search");
            return ValueTask.FromResult(UriCommandResult.Completed());
        }

        public ValueTask<UriCommandResult> ShowViewAsync(
            PlayniteViewTarget view,
            CancellationToken cancellationToken)
        {
            Calls.Add("view");
            return ValueTask.FromResult(UriCommandResult.Completed());
        }

        public ValueTask<UriCommandResult> ApplyFilterAsync(
            PlayniteFilterTarget filter,
            CancellationToken cancellationToken)
        {
            Calls.Add("filter");
            return ValueTask.FromResult(UriCommandResult.Completed());
        }
    }

    private sealed class ContractHtmlDescriptionProvider : IHtmlDescriptionProvider
    {
        public List<string> Calls { get; } = new();

        public ValueTask<HtmlDescriptionSourceResult> GetDescriptionAsync(
            string gameId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(gameId);
            return ValueTask.FromResult(HtmlDescriptionSourceResult.Found("<b>safe</b>"));
        }
    }

    private sealed class ContractHtmlSanitizer : IHtmlSanitizer
    {
        public List<string?> Calls { get; } = new();

        public HtmlSanitizationResult Sanitize(string? html)
        {
            Calls.Add(html);
            return new HtmlSanitizationResult(html ?? string.Empty);
        }
    }
}
