using Playnite.Core.Descriptions;

namespace Playnite.Core.Tests.Descriptions;

internal sealed class FakeHtmlDescriptionProvider : IHtmlDescriptionProvider
{
    public List<string> Calls { get; } = new();

    public Func<string, CancellationToken, ValueTask<HtmlDescriptionSourceResult>> Handler { get; set; } =
        (_, _) => ValueTask.FromResult(HtmlDescriptionSourceResult.Missing());

    public ValueTask<HtmlDescriptionSourceResult> GetDescriptionAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(gameId);
        return Handler(gameId, cancellationToken);
    }
}

internal sealed class FakeHtmlSanitizer : IHtmlSanitizer
{
    public List<string?> Calls { get; } = new();

    public Func<string?, HtmlSanitizationResult> Handler { get; set; } =
        html => new HtmlSanitizationResult(html ?? string.Empty);

    public HtmlSanitizationResult Sanitize(string? html)
    {
        Calls.Add(html);
        return Handler(html);
    }
}
