namespace Playnite.Core.Addons;

/// <summary>
/// Result of parsing and validating an add-on document. A valid result always carries the
/// parsed value; a failed result always carries at least one diagnostic.
/// </summary>
/// <typeparam name="T">Parsed manifest type.</typeparam>
public sealed class AddonValidationResult<T>
    where T : class
{
    private AddonValidationResult(T? value, IReadOnlyList<AddonDiagnostic> diagnostics)
    {
        Value = value;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the parsed manifest, or <see langword="null"/> when parsing failed.</summary>
    public T? Value { get; }

    /// <summary>Gets the failures in the order they were found.</summary>
    public IReadOnlyList<AddonDiagnostic> Diagnostics { get; }

    /// <summary>Gets whether the document parsed and passed every install-safety check.</summary>
    public bool IsValid => Value is not null && Diagnostics.Count == 0;

    /// <summary>Creates a valid result around a parsed manifest.</summary>
    public static AddonValidationResult<T> Valid(T value) => new(value, Array.Empty<AddonDiagnostic>());

    /// <summary>Creates a failed result with one or more diagnostics.</summary>
    public static AddonValidationResult<T> Invalid(params AddonDiagnostic[] diagnostics) =>
        new(null, diagnostics);

    /// <summary>Creates a failed result with one or more diagnostics.</summary>
    public static AddonValidationResult<T> Invalid(IReadOnlyList<AddonDiagnostic> diagnostics) =>
        new(null, diagnostics);
}
