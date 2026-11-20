namespace Playnite.SDK.Descriptions;

/// <summary>How a description request was answered.</summary>
public enum HtmlDescriptionStatus
{
    /// <summary>A non-empty safe document is available.</summary>
    Rendered = 0,

    /// <summary>The provider had no description, or nothing safe remained after sanitisation.</summary>
    Empty,

    /// <summary>The provider failed or returned an unusable response.</summary>
    Failed,
}

/// <summary>The provider's answer for one game description.</summary>
public sealed record HtmlDescriptionSourceResult(bool Success, string? Description, string? Diagnostic)
{
    /// <summary>A successful answer; a null or blank description means the provider had none.</summary>
    public static HtmlDescriptionSourceResult Found(string? description) => new(true, description, null);

    /// <summary>The provider is reachable but has no description for this game.</summary>
    public static HtmlDescriptionSourceResult Missing() => new(false, null, null);

    /// <summary>The provider failed with a stable diagnostic.</summary>
    public static HtmlDescriptionSourceResult Failure(string diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        return new HtmlDescriptionSourceResult(false, null, diagnostic);
    }
}

/// <summary>
/// The injectable description seam. The host adapts the p6-005 metadata document (its
/// <c>Description</c> field) or a plugin provider; the tests use a fake. Implementations do
/// their own I/O; this service opens no socket and no file.
/// </summary>
public interface IHtmlDescriptionProvider
{
    /// <summary>Returns the raw, untrusted description for one game id.</summary>
    ValueTask<HtmlDescriptionSourceResult> GetDescriptionAsync(
        string gameId,
        CancellationToken cancellationToken = default);
}

/// <summary>One sanitised description result.</summary>
public sealed record HtmlDescriptionResult(
    HtmlDescriptionStatus Status,
    string Html,
    string? Diagnostic = null,
    HtmlSanitizationResult? Sanitization = null)
{
    /// <summary>True when a safe document is available.</summary>
    public bool IsRendered => Status == HtmlDescriptionStatus.Rendered;
}
