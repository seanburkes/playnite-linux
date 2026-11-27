using System.Text;
using System.Text.Json;
using Playnite.Core.Addons;
using Playnite.Core.Manifests;
using Playnite.Core.Serialization;

namespace Playnite.Core.Plugins;

/// <summary>
/// Parses v2 <c>plugin.json</c> manifests and legacy v10 <c>extension.yaml</c> manifests into
/// <see cref="PluginDescriptor"/> instances without loading any plugin assemblies.
/// </summary>
public static class PluginManifestParser
{
    public const string PluginManifestFileName = "plugin.json";
    public const string LegacyExtensionManifestFileName = AddonInstallLayout.ExtensionManifestFileName;
    public const string InvalidManifestCode = "invalid-manifest";
    public const int MaxManifestBytes = 1024 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    /// <summary>Parses and validates a v2 <c>plugin.json</c> document.</summary>
    public static (PluginDescriptor? Descriptor, IReadOnlyList<PluginDiagnostic> Diagnostics) ParseV2Manifest(
        string json,
        string rootDirectory,
        string sourceName = PluginManifestFileName)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var normalized = SerializationLimits.WithoutByteOrderMark(json);
        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        if (byteCount > MaxManifestBytes)
        {
            return (null, [new PluginDiagnostic(string.Empty, InvalidManifestCode,
                $"{sourceName}: manifest is {byteCount} bytes, which exceeds the {MaxManifestBytes}-byte limit.")]);
        }

        var utf8Bytes = Encoding.UTF8.GetBytes(normalized);
        if (HasDuplicateObjectKeys(utf8Bytes, out var duplicatePath, out var readerError))
        {
            var reason = readerError ?? $"duplicate property '{duplicatePath}'";
            return (null, [new PluginDiagnostic(string.Empty, InvalidManifestCode, $"{sourceName}: invalid JSON ({reason}).")]);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            return (null, [new PluginDiagnostic(string.Empty, InvalidManifestCode, $"{sourceName}: invalid JSON ({exception.Message}).")]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, [new PluginDiagnostic(string.Empty, InvalidManifestCode, $"{sourceName}: root value must be a JSON object.")]);
            }

            var diagnostics = new List<PluginDiagnostic>();
            var root = document.RootElement;

            var id = ReadStrictOptionalString(root, "id", string.Empty, sourceName, diagnostics, out var idBadType);
            var diagnosticId = id ?? string.Empty;
            if (!idBadType && !AddonPathSafety.IsSafeSegment(id))
            {
                diagnostics.Add(new PluginDiagnostic(diagnosticId, AddonManifestValidator.UnsafeIdCode,
                    $"{sourceName}: $.id '{id}' must be one safe directory name."));
            }

            var name = ReadStrictOptionalString(root, "name", diagnosticId, sourceName, diagnostics, out var nameBadType);
            if (!nameBadType && string.IsNullOrWhiteSpace(name))
            {
                diagnostics.Add(new PluginDiagnostic(diagnosticId, InvalidManifestCode,
                    $"{sourceName}: $.name is required."));
            }

            var versionText = ReadStrictOptionalString(root, "version", diagnosticId, sourceName, diagnostics, out var versionBadType);
            Version? version = null;
            if (!versionBadType && (versionText is null
                    || !string.Equals(versionText, versionText.Trim(), StringComparison.Ordinal)
                    || !Version.TryParse(versionText, out version)))
            {
                diagnostics.Add(new PluginDiagnostic(diagnosticId, InvalidManifestCode,
                    $"{sourceName}: $.version '{versionText}' must be a valid version."));
            }

            var sourceId = Guid.Empty;
            if (root.TryGetProperty("sourceId", out var sourceIdElement) && sourceIdElement.ValueKind != JsonValueKind.Null)
            {
                var rawSourceId = sourceIdElement.ValueKind == JsonValueKind.String ? sourceIdElement.GetString() : null;
                if (rawSourceId is null
                    || !string.Equals(rawSourceId, rawSourceId.Trim(), StringComparison.Ordinal)
                    || !Guid.TryParse(rawSourceId, out sourceId))
                {
                    diagnostics.Add(new PluginDiagnostic(diagnosticId, InvalidManifestCode,
                        $"{sourceName}: $.sourceId must be a valid GUID."));
                }
            }

            var surfaces = ParseSurfaces(root, diagnosticId, sourceName, diagnostics);
            var capabilities = ParseCapabilities(root, diagnosticId, sourceName, diagnostics);
            if ((capabilities.HasFlag(PluginCapabilityKind.LibrarySource) || capabilities.HasFlag(PluginCapabilityKind.Launch))
                && sourceId == Guid.Empty)
            {
                diagnostics.Add(new PluginDiagnostic(diagnosticId, InvalidManifestCode,
                    $"{sourceName}: $.sourceId is required when librarySource or launch capability is declared."));
            }

            var entryPoints = ParseEntryPoints(root, diagnosticId, sourceName, diagnostics);
            ValidateDeclaredSurfaceEntryPoints(surfaces, entryPoints, diagnosticId, sourceName, diagnostics);

            var description = ReadStrictOptionalString(root, "description", diagnosticId, sourceName, diagnostics, out _);
            var enabledByDefault = true;
            if (root.TryGetProperty("enabledByDefault", out var enabledElement))
            {
                if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    diagnostics.Add(new PluginDiagnostic(diagnosticId, InvalidManifestCode,
                        $"{sourceName}: $.enabledByDefault must be a boolean."));
                }
                else
                {
                    enabledByDefault = enabledElement.GetBoolean();
                }
            }

            if (diagnostics.Count > 0 || id is null || name is null || version is null)
            {
                return (null, diagnostics);
            }

            return (
                new PluginDescriptor(
                    id,
                    sourceId,
                    name.Trim(),
                    version,
                    PluginLoadKind.ExternalAssembly,
                    surfaces,
                    capabilities,
                    entryPoints,
                    Path.GetFullPath(rootDirectory),
                    description,
                    enabledByDefault),
                []);
        }
    }

    /// <summary>Parses a legacy v10 <c>extension.yaml</c> document using <see cref="AddonManifestValidator"/>.</summary>
    public static (PluginDescriptor? Descriptor, IReadOnlyList<PluginDiagnostic> Diagnostics) ParseLegacyExtensionManifest(
        string yaml,
        string rootDirectory,
        string sourceName = LegacyExtensionManifestFileName)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var normalized = SerializationLimits.WithoutByteOrderMark(yaml);
        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        if (byteCount > MaxManifestBytes)
        {
            return (null, [new PluginDiagnostic(string.Empty, InvalidManifestCode,
                $"{sourceName}: manifest is {byteCount} bytes, which exceeds the {MaxManifestBytes}-byte limit.")]);
        }

        var validation = AddonManifestValidator.ValidateExtension(normalized, sourceName);
        if (validation.Value is not { } manifest || validation.Diagnostics.Count > 0)
        {
            var mapped = validation.Diagnostics
                .Select(d => new PluginDiagnostic(validation.Value?.Id ?? string.Empty, d.Code, d.Message))
                .ToArray();
            return (null, mapped);
        }

        if (manifest.Type == ExtensionType.Script)
        {
            return (null, [new PluginDiagnostic(manifest.Id ?? string.Empty, "unsupported-extension-type",
                $"{sourceName}: Script extensions cannot be loaded as .NET plugin assemblies.")]);
        }

        if (manifest.Version is null
            || !string.Equals(manifest.Version, manifest.Version.Trim(), StringComparison.Ordinal)
            || !Version.TryParse(manifest.Version, out var version))
        {
            return (null, [new PluginDiagnostic(manifest.Id ?? string.Empty, InvalidManifestCode,
                $"{sourceName}: $.Version '{manifest.Version}' must be a valid version.")]);
        }

        var capabilities = manifest.Type switch
        {
            ExtensionType.GameLibrary => PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch | PluginCapabilityKind.LibrarySync,
            ExtensionType.MetadataProvider => PluginCapabilityKind.Metadata,
            _ => PluginCapabilityKind.Commands | PluginCapabilityKind.EventHooks,
        };

        var sourceId = Guid.TryParse(manifest.Id, out var parsedSourceId) ? parsedSourceId : Guid.Empty;
        return (
            new PluginDescriptor(
                manifest.Id!,
                sourceId,
                manifest.Name ?? manifest.Id!,
                version,
                PluginLoadKind.ExternalAssembly,
                PluginSurface.Core,
                capabilities,
                new PluginEntryPoints(Core: manifest.Module),
                Path.GetFullPath(rootDirectory)),
            []);
    }

    private static bool HasDuplicateObjectKeys(
        ReadOnlySpan<byte> utf8Bytes,
        out string? duplicateKey,
        out string? readerError)
    {
        duplicateKey = null;
        readerError = null;
        var seenStack = new Stack<HashSet<string>>();
        var reader = new Utf8JsonReader(utf8Bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        seenStack.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        if (seenStack.Count > 0)
                        {
                            seenStack.Pop();
                        }
                        break;
                    case JsonTokenType.PropertyName:
                        var propertyName = reader.GetString() ?? string.Empty;
                        if (seenStack.Count > 0 && !seenStack.Peek().Add(propertyName))
                        {
                            duplicateKey = propertyName;
                            return true;
                        }
                        break;
                }
            }
        }
        catch (JsonException exception)
        {
            readerError = exception.Message;
            return true;
        }

        return false;
    }

    private static string? ReadStrictOptionalString(
        JsonElement root,
        string propertyName,
        string pluginId,
        string sourceName,
        List<PluginDiagnostic> diagnostics,
        out bool invalidType)
    {
        invalidType = false;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            invalidType = true;
            diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                $"{sourceName}: $.{propertyName} must be a string."));
            return null;
        }

        return element.GetString();
    }

    private static PluginSurface ParseSurfaces(
        JsonElement root,
        string pluginId,
        string sourceName,
        List<PluginDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("surfaces", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                $"{sourceName}: $.surfaces must be a non-empty string array."));
            return PluginSurface.None;
        }

        var result = PluginSurface.None;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                    $"{sourceName}: $.surfaces entries must be strings."));
                continue;
            }

            var value = item.GetString()?.Trim().ToLowerInvariant();
            switch (value)
            {
                case "core":
                    result |= PluginSurface.Core;
                    break;
                case "desktop":
                    result |= PluginSurface.Desktop;
                    break;
                case "fullscreen":
                    result |= PluginSurface.Fullscreen;
                    break;
                case "theme":
                    result |= PluginSurface.Theme;
                    break;
                default:
                    diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                        $"{sourceName}: $.surfaces contains unknown surface '{item.GetString()}'."));
                    break;
            }
        }

        if (result == PluginSurface.None)
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                $"{sourceName}: $.surfaces must declare at least one surface."));
        }

        return result;
    }

    private static PluginCapabilityKind ParseCapabilities(
        JsonElement root,
        string pluginId,
        string sourceName,
        List<PluginDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("capabilities", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return PluginCapabilityKind.None;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                $"{sourceName}: $.capabilities must be a string array."));
            return PluginCapabilityKind.None;
        }

        var result = PluginCapabilityKind.None;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                    $"{sourceName}: $.capabilities entries must be strings."));
                continue;
            }

            var value = item.GetString()?.Trim().ToLowerInvariant();
            switch (value)
            {
                case "library":
                case "librarysource":
                    result |= PluginCapabilityKind.LibrarySource;
                    break;
                case "launch":
                    result |= PluginCapabilityKind.Launch;
                    break;
                case "metadata":
                    result |= PluginCapabilityKind.Metadata;
                    break;
                case "commands":
                    result |= PluginCapabilityKind.Commands;
                    break;
                case "hooks":
                case "eventhooks":
                    result |= PluginCapabilityKind.EventHooks;
                    break;
                case "settings":
                    result |= PluginCapabilityKind.Settings;
                    break;
                case "uislots":
                case "ui-slots":
                    result |= PluginCapabilityKind.UiSlots;
                    break;
                case "theme":
                    result |= PluginCapabilityKind.Theme;
                    break;
                case "sync":
                case "librarysync":
                    result |= PluginCapabilityKind.LibrarySync;
                    break;
                default:
                    diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                        $"{sourceName}: $.capabilities contains unknown capability '{item.GetString()}'."));
                    break;
            }
        }

        return result;
    }

    private static PluginEntryPoints ParseEntryPoints(
        JsonElement root,
        string pluginId,
        string sourceName,
        List<PluginDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("entryPoints", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                $"{sourceName}: $.entryPoints must be a JSON object."));
            return new PluginEntryPoints();
        }

        var core = ReadSafeEntryPath(element, "core", pluginId, sourceName, diagnostics);
        var desktop = ReadSafeEntryPath(element, "desktop", pluginId, sourceName, diagnostics);
        var fullscreen = ReadSafeEntryPath(element, "fullscreen", pluginId, sourceName, diagnostics);
        var theme = ReadSafeEntryPath(element, "theme", pluginId, sourceName, diagnostics);
        var entryType = ReadStrictOptionalString(element, "entryType", pluginId, sourceName, diagnostics, out _);
        if (entryType is not null && string.IsNullOrWhiteSpace(entryType))
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                $"{sourceName}: $.entryPoints.entryType cannot be blank."));
        }

        return new PluginEntryPoints(core, desktop, fullscreen, theme, entryType?.Trim());
    }

    private static string? ReadSafeEntryPath(
        JsonElement entryPoints,
        string propertyName,
        string pluginId,
        string sourceName,
        List<PluginDiagnostic> diagnostics)
    {
        if (!entryPoints.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, AddonManifestValidator.UnsafeModuleCode,
                $"{sourceName}: $.entryPoints.{propertyName} must be a relative path string."));
            return null;
        }

        var path = element.GetString();
        if (!AddonPathSafety.IsSafeRelativePath(path))
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, AddonManifestValidator.UnsafeModuleCode,
                $"{sourceName}: $.entryPoints.{propertyName} '{path}' must be a relative path inside the plugin directory."));
            return null;
        }

        return path;
    }

    private static void ValidateDeclaredSurfaceEntryPoints(
        PluginSurface surfaces,
        PluginEntryPoints entryPoints,
        string pluginId,
        string sourceName,
        List<PluginDiagnostic> diagnostics)
    {
        if (surfaces.HasFlag(PluginSurface.Core) && entryPoints.Core is null)
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                $"{sourceName}: core surface requires $.entryPoints.core."));
        }

        if (surfaces.HasFlag(PluginSurface.Desktop) && entryPoints.Desktop is null && entryPoints.Core is null)
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                $"{sourceName}: desktop surface requires $.entryPoints.desktop or $.entryPoints.core."));
        }

        if (surfaces.HasFlag(PluginSurface.Fullscreen) && entryPoints.Fullscreen is null && entryPoints.Core is null)
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                $"{sourceName}: fullscreen surface requires $.entryPoints.fullscreen or $.entryPoints.core."));
        }

        if (surfaces.HasFlag(PluginSurface.Theme) && entryPoints.Theme is null)
        {
            diagnostics.Add(new PluginDiagnostic(pluginId, InvalidManifestCode,
                $"{sourceName}: theme surface requires $.entryPoints.theme."));
        }
    }
}
