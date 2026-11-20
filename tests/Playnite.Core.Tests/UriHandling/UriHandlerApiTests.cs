using Playnite.Core.UriHandling;
using Playnite.SDK;
using Playnite.SDK.Events;
using Xunit;

namespace Playnite.Core.Tests.UriHandling;

public sealed class UriHandlerApiTests
{
    private const string GameId = "6a0d9f1e-4a2b-4c3d-8e9f-0123456789ab";
    private static readonly Guid Game = Guid.Parse(GameId);
    private const string EntityId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task Start_dispatches_the_game_and_action_to_the_host()
    {
        var host = new FakeUriCommandHost();
        var handler = new UriHandlerApi(host);

        var result = await handler.DispatchAsync(
            $"playnite://playnite/start/{GameId}/3",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(UriDispatchStatus.Completed, result.Status);
        var call = Assert.Single(host.Calls);
        Assert.Equal("start", call.Kind);
        Assert.Equal(Game, call.GameId);
        Assert.Equal(3, call.ActionIndex);
    }

    [Fact]
    public async Task Start_without_an_action_passes_null()
    {
        var host = new FakeUriCommandHost();
        var handler = new UriHandlerApi(host);

        var result = await handler.DispatchAsync(
            $"playnite://playnite/start/{GameId}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(Assert.Single(host.Calls).ActionIndex);
    }

    [Fact]
    public async Task Show_game_dispatches_the_id()
    {
        var host = new FakeUriCommandHost();
        var handler = new UriHandlerApi(host);

        var result = await handler.DispatchAsync(
            $"playnite://playnite/showgame/{GameId}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var call = Assert.Single(host.Calls);
        Assert.Equal("showgame", call.Kind);
        Assert.Equal(Game, call.GameId);
    }

    [Theory]
    [InlineData("playnite://playnite/search", null)]
    [InlineData("playnite://playnite/search/portal", "portal")]
    [InlineData("playnite://playnite/search/portal+2", "portal 2")]
    public async Task Search_dispatches_the_decoded_term(string uri, string? expected)
    {
        var host = new FakeUriCommandHost();
        var handler = new UriHandlerApi(host);

        var result = await handler.DispatchAsync(uri, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var call = Assert.Single(host.Calls);
        Assert.Equal("search", call.Kind);
        Assert.Equal(expected, call.Term);
    }

    [Theory]
    [InlineData("playnite://playnite/view/desktop", PlayniteViewTarget.Desktop)]
    [InlineData("playnite://playnite/view/fullscreen", PlayniteViewTarget.Fullscreen)]
    public async Task View_dispatches_the_target(string uri, PlayniteViewTarget expected)
    {
        var host = new FakeUriCommandHost();
        var handler = new UriHandlerApi(host);

        var result = await handler.DispatchAsync(uri, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var call = Assert.Single(host.Calls);
        Assert.Equal("view", call.Kind);
        Assert.Equal(expected, call.View);
    }

    [Fact]
    public async Task Filter_dispatches_the_normalized_target()
    {
        var host = new FakeUriCommandHost();
        var handler = new UriHandlerApi(host);

        var result = await handler.DispatchAsync(
            $"playnite://playnite/filter/genre/{EntityId}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var call = Assert.Single(host.Calls);
        Assert.Equal("filter", call.Kind);
        Assert.NotNull(call.Filter);
        Assert.Equal(PlayniteFilterField.Genre, call.Filter.Field);
        Assert.Equal(EntityId, call.Filter.Value);
    }

    [Theory]
    [InlineData("playnite://playnite/start/not-a-guid")]
    [InlineData("playnite://playnite/start/..%2F..%2Fbin%2Fsh")]
    [InlineData("playnite://playnite/start/" + GameId + "%3Brm+-rf+%2F")]
    [InlineData("playnite://playnite/showgame/not-a-guid")]
    [InlineData("playnite://playnite/view/mobile")]
    [InlineData("playnite://playnite/filter/favorite/maybe")]
    [InlineData("playnite://playnite/creatediag")]
    [InlineData("playnite://playnite/installaddon/https%3A%2F%2Fevil.example")]
    [InlineData("https://evil.example/playnite")]
    [InlineData("playnite://")]
    public async Task Hostile_or_malformed_input_is_rejected_before_the_host_runs(string uri)
    {
        var host = new FakeUriCommandHost();
        var handler = new UriHandlerApi(host);

        var result = await handler.DispatchAsync(uri, TestContext.Current.CancellationToken);

        Assert.Equal(UriDispatchStatus.Rejected, result.Status);
        Assert.Empty(host.Calls);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
        Assert.Null(result.Route);
    }

    [Fact]
    public async Task Plugin_source_receives_the_decoded_arguments()
    {
        var handler = new UriHandlerApi();
        PlayniteUriEventArgs? received = null;
        handler.RegisterSource("my-plugin", args => received = args);

        var result = await handler.DispatchAsync(
            "playnite://my-plugin/open/%E2%9C%93/a+b",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(received);
        Assert.Equal(new[] { "open", "\u2713", "a b" }, received.Arguments);
    }

    [Fact]
    public async Task Plugin_source_lookup_is_case_insensitive()
    {
        var handler = new UriHandlerApi();
        var calls = 0;
        handler.RegisterSource("my-plugin", _ => calls++);

        var result = await handler.DispatchAsync(
            "playnite://MY-PLUGIN",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Unknown_plugin_source_is_unhandled_and_keeps_the_route()
    {
        var handler = new UriHandlerApi();

        var result = await handler.DispatchAsync(
            "playnite://missing-plugin/x",
            TestContext.Current.CancellationToken);

        Assert.Equal(UriDispatchStatus.UnhandledSource, result.Status);
        Assert.NotNull(result.Route);
        Assert.Equal("missing-plugin", result.Route.Source);
        Assert.Contains("not registered", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throwing_plugin_handler_is_a_failure_not_an_escape()
    {
        var handler = new UriHandlerApi();
        handler.RegisterSource("my-plugin", _ => throw new InvalidOperationException("boom"));

        var result = await handler.DispatchAsync(
            "playnite://my-plugin/x",
            TestContext.Current.CancellationToken);

        Assert.Equal(UriDispatchStatus.Failed, result.Status);
        Assert.Contains("InvalidOperationException", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throwing_host_is_a_failure_not_an_escape()
    {
        var host = new FakeUriCommandHost
        {
            StartHandler = _ => throw new IOException("disk gone"),
        };
        var handler = new UriHandlerApi(host);

        var result = await handler.DispatchAsync(
            $"playnite://playnite/start/{GameId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(UriDispatchStatus.Failed, result.Status);
        Assert.Contains("IOException", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_unavailable_and_failed_results_are_mapped()
    {
        var host = new FakeUriCommandHost
        {
            Result = () => UriCommandResult.Unavailable("no shell"),
        };
        var handler = new UriHandlerApi(host);
        var result = await handler.DispatchAsync(
            "playnite://playnite/search/x",
            TestContext.Current.CancellationToken);
        Assert.Equal(UriDispatchStatus.Unavailable, result.Status);
        Assert.Equal("no shell", result.Diagnostic);

        host.Result = () => UriCommandResult.Failed("launch failed");
        result = await handler.DispatchAsync(
            "playnite://playnite/search/x",
            TestContext.Current.CancellationToken);
        Assert.Equal(UriDispatchStatus.Failed, result.Status);
        Assert.Equal("launch failed", result.Diagnostic);
    }

    [Fact]
    public async Task Missing_host_still_dispatches_plugin_sources()
    {
        var handler = new UriHandlerApi();
        var calls = 0;
        handler.RegisterSource("plugin", _ => calls++);

        var appResult = await handler.DispatchAsync(
            "playnite://playnite/search/x",
            TestContext.Current.CancellationToken);
        var pluginResult = await handler.DispatchAsync(
            "playnite://plugin/x",
            TestContext.Current.CancellationToken);

        Assert.Equal(UriDispatchStatus.Unavailable, appResult.Status);
        Assert.Equal(UriHandlerApi.MissingHostDiagnostic, appResult.Diagnostic);
        Assert.True(pluginResult.Succeeded);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_from_the_host()
    {
        using var source = new CancellationTokenSource();
        var host = new FakeUriCommandHost
        {
            StartHandler = token =>
            {
                source.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(UriCommandResult.Completed());
            },
        };
        var handler = new UriHandlerApi(host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.DispatchAsync($"playnite://playnite/start/{GameId}", source.Token));
    }

    [Fact]
    public async Task Caller_cancellation_before_dispatch_throws_and_runs_nothing()
    {
        var host = new FakeUriCommandHost();
        var handler = new UriHandlerApi(host);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.DispatchAsync($"playnite://playnite/start/{GameId}", source.Token));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Host_cancelling_its_own_token_is_a_failure()
    {
        var host = new FakeUriCommandHost
        {
            StartHandler = _ => throw new OperationCanceledException("host timeout"),
        };
        var handler = new UriHandlerApi(host);

        var result = await handler.DispatchAsync(
            $"playnite://playnite/start/{GameId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(UriDispatchStatus.Failed, result.Status);
    }

    [Fact]
    public void Register_and_remove_source_follow_the_v10_contract()
    {
        var handler = new UriHandlerApi();
        handler.RegisterSource("plugin", _ => { });
        handler.RegisterSource("other", _ => { });
        Assert.Equal(new[] { "other", "plugin" }, handler.RegisteredSources);

        handler.RemoveSource("PLUGIN");
        Assert.Equal(new[] { "other" }, handler.RegisteredSources);
        handler.RemoveSource("absent");
        Assert.Equal(new[] { "other" }, handler.RegisteredSources);
    }

    [Fact]
    public void Duplicate_and_reserved_sources_are_rejected()
    {
        var handler = new UriHandlerApi();
        handler.RegisterSource("plugin", _ => { });

        Assert.Throws<InvalidOperationException>(() => handler.RegisterSource("PLUGIN", _ => { }));
        Assert.Throws<ArgumentException>(() => handler.RegisterSource("playnite", _ => { }));
        Assert.Throws<ArgumentException>(() => handler.RegisterSource("PLAYNITE", _ => { }));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad source")]
    [InlineData("bad/source")]
    [InlineData("bad%20source")]
    [InlineData("..")]
    [InlineData(".")]
    public void Invalid_source_names_are_rejected(string source)
    {
        var handler = new UriHandlerApi();
        Assert.Throws<ArgumentException>(() => handler.RegisterSource(source, _ => { }));
    }

    [Fact]
    public void Null_handler_is_rejected()
    {
        var handler = new UriHandlerApi();
        Assert.Throws<ArgumentNullException>(() => handler.RegisterSource("plugin", null!));
    }
}
