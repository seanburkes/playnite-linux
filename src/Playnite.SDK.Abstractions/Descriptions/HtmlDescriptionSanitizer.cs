namespace Playnite.SDK.Descriptions;

/// <summary>One sanitised description and what was removed to make it safe.</summary>
public sealed record HtmlSanitizationResult(
    string Html,
    int RemovedElements = 0,
    int RemovedAttributes = 0,
    bool Truncated = false)
{
    /// <summary>True when nothing safe remains.</summary>
    public bool IsEmpty => Html.Length == 0;
}

/// <summary>The injectable sanitiser seam. A fake records calls and proves the service is isolated.</summary>
public interface IHtmlSanitizer
{
    /// <summary>Returns a safe subset of untrusted HTML. Never throws for malformed input.</summary>
    HtmlSanitizationResult Sanitize(string? html);
}
