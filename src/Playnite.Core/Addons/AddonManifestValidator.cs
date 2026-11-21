using Playnite.Core.Manifests;
using Playnite.Core.Serialization;

namespace Playnite.Core.Addons;

/// <summary>
/// Validates add-on documents with the p3-005 parser and adds the install-directory safety
/// rules that a pure document boundary cannot know about. Every failure is a diagnostic with
/// a stable code and the offending manifest path; the parser's own failures keep their member
/// path so a malformed field is named instead of reported as a generic parse error.
/// </summary>
public static class AddonManifestValidator
{
    /// <summary>The document did not parse or failed a required-field rule.</summary>
    public const string InvalidManifestCode = "invalid-manifest";

    /// <summary>The add-on id is not one portable directory name.</summary>
    public const string UnsafeIdCode = "unsafe-id";

    /// <summary>The module path is absolute or escapes the install directory.</summary>
    public const string UnsafeModuleCode = "unsafe-module";

    /// <summary>The icon path is absolute or escapes the install directory.</summary>
    public const string UnsafeIconCode = "unsafe-icon";

    /// <summary>Validates an <c>extension.yaml</c> document.</summary>
    /// <param name="document">YAML text.</param>
    /// <param name="sourceName">File name used in diagnostics; defaults to <c>extension.yaml</c>.</param>
    public static AddonValidationResult<ExtensionManifest> ValidateExtension(
        string document,
        string sourceName = "extension.yaml")
    {
        ArgumentNullException.ThrowIfNull(document);
        var (manifest, diagnostics) = ParseExtension(document, sourceName);
        if (manifest is null)
        {
            return AddonValidationResult<ExtensionManifest>.Invalid(diagnostics);
        }

        CheckId(manifest.Id, sourceName, diagnostics);
        if (!AddonPathSafety.IsSafeRelativePath(manifest.Module))
        {
            diagnostics.Add(new AddonDiagnostic(
                UnsafeModuleCode,
                "$.Module",
                $"{sourceName}: module '{manifest.Module}' must be a relative path inside the add-on directory."));
        }

        CheckOptionalRelativePath(manifest.Icon, UnsafeIconCode, "$.Icon", "icon", sourceName, diagnostics);
        return diagnostics.Count == 0
            ? AddonValidationResult<ExtensionManifest>.Valid(manifest)
            : AddonValidationResult<ExtensionManifest>.Invalid(diagnostics);
    }

    /// <summary>Validates a <c>theme.yaml</c> document.</summary>
    /// <param name="document">YAML text.</param>
    /// <param name="sourceName">File name used in diagnostics; defaults to <c>theme.yaml</c>.</param>
    public static AddonValidationResult<ThemeManifest> ValidateTheme(
        string document,
        string sourceName = "theme.yaml")
    {
        ArgumentNullException.ThrowIfNull(document);
        var (manifest, diagnostics) = Parse<ThemeManifest>(document, sourceName);
        if (manifest is null)
        {
            return AddonValidationResult<ThemeManifest>.Invalid(diagnostics);
        }

        CheckId(manifest.Id, sourceName, diagnostics);
        return diagnostics.Count == 0
            ? AddonValidationResult<ThemeManifest>.Valid(manifest)
            : AddonValidationResult<ThemeManifest>.Invalid(diagnostics);
    }

    /// <summary>Validates catalogue <c>addon.json</c> metadata before its id is used as a directory.</summary>
    /// <param name="document">JSON text.</param>
    /// <param name="sourceName">File name used in diagnostics; defaults to <c>addon.json</c>.</param>
    public static AddonValidationResult<AddonManifest> ValidateAddon(
        string document,
        string sourceName = "addon.json")
    {
        ArgumentNullException.ThrowIfNull(document);
        var (manifest, diagnostics) = ParseJson<AddonManifest>(document, sourceName);
        if (manifest is null)
        {
            return AddonValidationResult<AddonManifest>.Invalid(diagnostics);
        }

        CheckId(manifest.AddonId, sourceName, diagnostics, "$.AddonId");
        return diagnostics.Count == 0
            ? AddonValidationResult<AddonManifest>.Valid(manifest)
            : AddonValidationResult<AddonManifest>.Invalid(diagnostics);
    }

    private static void CheckId(
        string? id,
        string sourceName,
        List<AddonDiagnostic> diagnostics,
        string path = "$.Id")
    {
        if (!AddonPathSafety.IsSafeSegment(id))
        {
            diagnostics.Add(new AddonDiagnostic(
                UnsafeIdCode,
                path,
                $"{sourceName}: id '{id}' must be one safe directory name."));
        }
    }

    private static void CheckOptionalRelativePath(
        string? value,
        string code,
        string path,
        string label,
        string sourceName,
        List<AddonDiagnostic> diagnostics)
    {
        if (value is not null && !AddonPathSafety.IsSafeRelativePath(value))
        {
            diagnostics.Add(new AddonDiagnostic(
                code,
                path,
                $"{sourceName}: {label} '{value}' must be a relative path inside the add-on directory."));
        }
    }

    private static (ExtensionManifest? Value, List<AddonDiagnostic> Diagnostics) ParseExtension(
        string document,
        string sourceName)
    {
        try
        {
            return (ManifestSerialization.FromYaml<ExtensionManifest>(document), new List<AddonDiagnostic>());
        }
        catch (SerializationException exception) when (IsMissingDefaultType(exception))
        {
            // v10's serializer omits default-valued members, so the oracle-captured
            // TestPlugin.extension.yaml has no Type even though v10 binds GenericPlugin.
            // The strict p3-005 boundary still rejects that shape; the install boundary
            // restores the documented v10 default explicitly through the derived type.
            try
            {
                return (ManifestSerialization.FromYaml<V10ExtensionManifest>(document), new List<AddonDiagnostic>());
            }
            catch (SerializationException fallback)
            {
                return (null, Fail(sourceName, fallback));
            }
        }
        catch (SerializationException exception)
        {
            return (null, Fail(sourceName, exception));
        }
    }

    private static bool IsMissingDefaultType(SerializationException exception) =>
        exception.Message.Contains("missing Type", StringComparison.Ordinal);

    private static (T? Value, List<AddonDiagnostic> Diagnostics) Parse<T>(string document, string sourceName)
        where T : class
    {
        try
        {
            return (ManifestSerialization.FromYaml<T>(document), new List<AddonDiagnostic>());
        }
        catch (SerializationException exception)
        {
            return (null, Fail(sourceName, exception));
        }
    }

    private static (T? Value, List<AddonDiagnostic> Diagnostics) ParseJson<T>(string document, string sourceName)
        where T : class
    {
        try
        {
            return (ManifestSerialization.FromJson<T>(document), new List<AddonDiagnostic>());
        }
        catch (SerializationException exception)
        {
            return (null, Fail(sourceName, exception));
        }
    }

    private static List<AddonDiagnostic> Fail(string sourceName, SerializationException exception) =>
    [
        new AddonDiagnostic(
            InvalidManifestCode,
            MemberPath(exception.Message),
            $"{sourceName}: {exception.Message}")
    ];

    // p3-005 failures start with the format and usually name a member path, for example
    // "YAML: $.Module: required non-empty string." Keep that path instead of flattening it.
    // The shared guard sometimes writes it in quotes: "value at '$.Type': ...".
    private static string MemberPath(string message)
    {
        var quoted = message.IndexOf("'$", StringComparison.Ordinal);
        if (quoted >= 0)
        {
            var quotedEnd = message.IndexOf('\'', quoted + 1);
            if (quotedEnd > quoted)
            {
                return message[(quoted + 1)..quotedEnd];
            }
        }

        var start = message.IndexOf('$', StringComparison.Ordinal);
        if (start < 0)
        {
            return "$";
        }

        var end = message.IndexOf(':', start);
        return end < 0 ? "$" : message[start..end];
    }

    // Same wire shape as ExtensionManifest; the derived type only tells the p3-005 parser
    // that the absent discriminator should use the v10 default (GenericPlugin, enum zero).
    private sealed class V10ExtensionManifest : ExtensionManifest;
}
