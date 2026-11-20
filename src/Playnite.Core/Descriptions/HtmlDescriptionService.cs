namespace Playnite.Core.Descriptions;

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

/// <summary>
/// Turns an untrusted game description from an injectable provider into the documented safe
/// HTML subset. The sanitiser is injectable for contract tests; the default is
/// <see cref="HtmlDescriptionSanitizer"/>. Provider failures are stable results, caller
/// cancellation propagates, and the service never fetches anything.
/// </summary>
public sealed class HtmlDescriptionService
{
    /// <summary>Diagnostic prefix for a provider exception.</summary>
    public const string ProviderFailureDiagnostic = "provider-exception";

    /// <summary>Diagnostic for a null provider response.</summary>
    public const string ProviderInvalidResponseDiagnostic = "provider-invalid-response";

    /// <summary>Diagnostic when sanitisation leaves no safe markup.</summary>
    public const string SanitizedEmptyDiagnostic = "sanitized-empty";

    /// <summary>Diagnostic when the source itself is blank.</summary>
    public const string EmptyDescriptionDiagnostic = "no-description";

    private readonly IHtmlDescriptionProvider provider;
    private readonly IHtmlSanitizer sanitizer;

    /// <summary>Creates the service over a provider and an optional sanitiser.</summary>
    public HtmlDescriptionService(IHtmlDescriptionProvider provider, IHtmlSanitizer? sanitizer = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
        this.sanitizer = sanitizer ?? new HtmlDescriptionSanitizer();
    }

    /// <summary>Resolves and sanitises one game description.</summary>
    public async Task<HtmlDescriptionResult> RenderAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        cancellationToken.ThrowIfCancellationRequested();

        HtmlDescriptionSourceResult? source;
        try
        {
            source = await provider.GetDescriptionAsync(gameId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new HtmlDescriptionResult(
                HtmlDescriptionStatus.Failed,
                string.Empty,
                $"{ProviderFailureDiagnostic}: {exception.GetType().Name}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (source is null)
        {
            return new HtmlDescriptionResult(
                HtmlDescriptionStatus.Failed,
                string.Empty,
                ProviderInvalidResponseDiagnostic);
        }

        if (!source.Success)
        {
            return source.Diagnostic is null
                ? new HtmlDescriptionResult(
                    HtmlDescriptionStatus.Empty,
                    string.Empty,
                    EmptyDescriptionDiagnostic)
                : new HtmlDescriptionResult(HtmlDescriptionStatus.Failed, string.Empty, source.Diagnostic);
        }

        if (string.IsNullOrWhiteSpace(source.Description))
        {
            return new HtmlDescriptionResult(
                HtmlDescriptionStatus.Empty,
                string.Empty,
                EmptyDescriptionDiagnostic);
        }

        var sanitization = sanitizer.Sanitize(source.Description);
        if (sanitization.IsEmpty)
        {
            return new HtmlDescriptionResult(
                HtmlDescriptionStatus.Empty,
                string.Empty,
                SanitizedEmptyDiagnostic,
                sanitization);
        }

        return new HtmlDescriptionResult(
            HtmlDescriptionStatus.Rendered,
            sanitization.Html,
            Sanitization: sanitization);
    }
}
