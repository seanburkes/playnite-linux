using Playnite.SDK.Descriptions;

namespace Playnite.Core.Descriptions;

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
