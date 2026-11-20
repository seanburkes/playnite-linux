using Playnite.Core.Descriptions;
using Xunit;
using Playnite.SDK.Descriptions;

namespace Playnite.Core.Tests.Descriptions;

public sealed class HtmlDescriptionServiceTests
{
    [Fact]
    public async Task Provider_contract_sanitises_the_untrusted_description()
    {
        var provider = new FakeHtmlDescriptionProvider
        {
            Handler = (_, _) => ValueTask.FromResult(
                HtmlDescriptionSourceResult.Found("<p>Hello <script>alert(1)</script></p>")),
        };
        var service = new HtmlDescriptionService(provider);

        var result = await service.RenderAsync("game-1", TestContext.Current.CancellationToken);

        Assert.True(result.IsRendered);
        Assert.Equal(HtmlDescriptionStatus.Rendered, result.Status);
        Assert.Equal("<p>Hello </p>", result.Html);
        Assert.Null(result.Diagnostic);
        Assert.Equal(new[] { "game-1" }, provider.Calls);
    }

    [Fact]
    public async Task Injected_sanitiser_receives_the_raw_description_and_its_output_is_returned()
    {
        var sanitizer = new FakeHtmlSanitizer
        {
            Handler = html => new HtmlSanitizationResult($"safe:{html}", RemovedElements: 2),
        };
        var provider = new FakeHtmlDescriptionProvider
        {
            Handler = (id, _) => ValueTask.FromResult(HtmlDescriptionSourceResult.Found($"<b>{id}</b>")),
        };
        var service = new HtmlDescriptionService(provider, sanitizer);

        var result = await service.RenderAsync("game-2", TestContext.Current.CancellationToken);

        Assert.Equal("safe:<b>game-2</b>", result.Html);
        Assert.Equal(new string?[] { "<b>game-2</b>" }, sanitizer.Calls);
        Assert.NotNull(result.Sanitization);
        Assert.Equal(2, result.Sanitization.RemovedElements);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_description_is_empty_and_never_sanitised(string? description)
    {
        var provider = new FakeHtmlDescriptionProvider
        {
            Handler = (_, _) => ValueTask.FromResult(HtmlDescriptionSourceResult.Found(description)),
        };
        var sanitizer = new FakeHtmlSanitizer();
        var service = new HtmlDescriptionService(provider, sanitizer);

        var result = await service.RenderAsync("game-3", TestContext.Current.CancellationToken);

        Assert.Equal(HtmlDescriptionStatus.Empty, result.Status);
        Assert.False(result.IsRendered);
        Assert.Equal(string.Empty, result.Html);
        Assert.Equal(HtmlDescriptionService.EmptyDescriptionDiagnostic, result.Diagnostic);
        Assert.Empty(sanitizer.Calls);
    }

    [Fact]
    public async Task A_missing_description_is_empty_not_a_failure()
    {
        var provider = new FakeHtmlDescriptionProvider
        {
            Handler = (_, _) => ValueTask.FromResult(HtmlDescriptionSourceResult.Missing()),
        };
        var service = new HtmlDescriptionService(provider);

        var result = await service.RenderAsync("game-4", TestContext.Current.CancellationToken);

        Assert.Equal(HtmlDescriptionStatus.Empty, result.Status);
        Assert.Equal(HtmlDescriptionService.EmptyDescriptionDiagnostic, result.Diagnostic);
    }

    [Fact]
    public async Task A_provider_failure_keeps_its_stable_reason()
    {
        var provider = new FakeHtmlDescriptionProvider
        {
            Handler = (_, _) => ValueTask.FromResult(
                HtmlDescriptionSourceResult.Failure("steam-unavailable")),
        };
        var service = new HtmlDescriptionService(provider);

        var result = await service.RenderAsync("game-5", TestContext.Current.CancellationToken);

        Assert.Equal(HtmlDescriptionStatus.Failed, result.Status);
        Assert.Equal("steam-unavailable", result.Diagnostic);
    }

    [Fact]
    public async Task A_provider_exception_becomes_a_failure_not_an_escape()
    {
        var provider = new FakeHtmlDescriptionProvider
        {
            Handler = (_, _) => throw new TimeoutException("slow"),
        };
        var service = new HtmlDescriptionService(provider);

        var result = await service.RenderAsync("game-6", TestContext.Current.CancellationToken);

        Assert.Equal(HtmlDescriptionStatus.Failed, result.Status);
        Assert.Equal("provider-exception: TimeoutException", result.Diagnostic);
    }

    [Fact]
    public async Task A_null_provider_result_is_an_invalid_response()
    {
        var provider = new FakeHtmlDescriptionProvider
        {
            Handler = (_, _) => ValueTask.FromResult<HtmlDescriptionSourceResult>(null!),
        };
        var service = new HtmlDescriptionService(provider);

        var result = await service.RenderAsync("game-7", TestContext.Current.CancellationToken);

        Assert.Equal(HtmlDescriptionStatus.Failed, result.Status);
        Assert.Equal(HtmlDescriptionService.ProviderInvalidResponseDiagnostic, result.Diagnostic);
    }

    [Fact]
    public async Task Sanitiser_output_with_nothing_safe_is_empty()
    {
        var provider = new FakeHtmlDescriptionProvider
        {
            Handler = (_, _) => ValueTask.FromResult(
                HtmlDescriptionSourceResult.Found("<script>alert(1)</script>")),
        };
        var service = new HtmlDescriptionService(provider);

        var result = await service.RenderAsync("game-8", TestContext.Current.CancellationToken);

        Assert.Equal(HtmlDescriptionStatus.Empty, result.Status);
        Assert.Equal(HtmlDescriptionService.SanitizedEmptyDiagnostic, result.Diagnostic);
        Assert.NotNull(result.Sanitization);
        Assert.True(result.Sanitization.IsEmpty);
        Assert.Equal(1, result.Sanitization.RemovedElements);
    }

    [Fact]
    public async Task Caller_cancellation_before_the_provider_throws_and_calls_nothing()
    {
        var provider = new FakeHtmlDescriptionProvider();
        var service = new HtmlDescriptionService(provider);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RenderAsync("game-9", source.Token));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_provider_propagates()
    {
        using var source = new CancellationTokenSource();
        var provider = new FakeHtmlDescriptionProvider
        {
            Handler = (_, token) =>
            {
                source.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(HtmlDescriptionSourceResult.Missing());
            },
        };
        var service = new HtmlDescriptionService(provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RenderAsync("game-10", source.Token));
    }

    [Fact]
    public async Task Provider_cancelling_its_own_token_is_a_failure()
    {
        var provider = new FakeHtmlDescriptionProvider
        {
            Handler = (_, _) => throw new OperationCanceledException("provider timeout"),
        };
        var service = new HtmlDescriptionService(provider);

        var result = await service.RenderAsync("game-11", TestContext.Current.CancellationToken);

        Assert.Equal(HtmlDescriptionStatus.Failed, result.Status);
        Assert.StartsWith("provider-exception", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blank_game_id_is_rejected()
    {
        var service = new HtmlDescriptionService(new FakeHtmlDescriptionProvider());

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.RenderAsync(" ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_null_provider_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() => new HtmlDescriptionService(null!));
    }
}
