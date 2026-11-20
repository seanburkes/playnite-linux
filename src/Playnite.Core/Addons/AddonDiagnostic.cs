namespace Playnite.Core.Addons;

/// <summary>
/// One explainable add-on validation or install failure. <see cref="Code"/> is stable for
/// tests and callers; <see cref="Path"/> names the document member or layout member.
/// </summary>
/// <param name="Code">Machine-readable failure code, for example <c>unsafe-id</c>.</param>
/// <param name="Path">Manifest path such as <c>$.Module</c>, or <c>$</c> for the document.</param>
/// <param name="Message">Human-readable explanation that includes the source name.</param>
public sealed record AddonDiagnostic(string Code, string Path, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Code} at {Path}: {Message}";
}
