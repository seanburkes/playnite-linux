using Playnite.Core.Descriptions;
using Playnite.Core.UriHandling;
using Playnite.SDK;
using Playnite.SDK.Descriptions;
using Xunit;

namespace Playnite.Core.Tests.SdkContracts;

/// <summary>
/// Contract tests for the host-facing URI and HTML-description surfaces that live in
/// Playnite.SDK.Abstractions. Each interface is exercised through the real Core service and a
/// recording fake, so the moved contract cannot change without the implementation following.
/// </summary>
public sealed class UriHtmlContractTests
{
    private static readonly Guid GameId = Guid.Parse("6a0d9f1e-4a2b-4c3d-8e9f-0123456789ab");

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
