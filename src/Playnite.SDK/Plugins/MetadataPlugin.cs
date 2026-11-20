using Playnite.SDK.Models;

namespace Playnite.SDK.Plugins;

/// <summary>
/// Base type for a metadata extension.
/// </summary>
/// <remarks>
/// PREVIEW, NOT FROZEN. Part of the p6-009 headless preview surface; see
/// <see cref="Plugin"/>. Only the synchronous request/result path is present; on-demand
/// providers and background downloads remain deferred.
/// </remarks>
public abstract class MetadataPlugin : Plugin
{
    /// <summary>Gets the metadata fields this extension can supply.</summary>
    public abstract IReadOnlyList<MetadataField> SupportedFields { get; }

    /// <summary>Returns metadata for the requested game and fields.</summary>
    public abstract GameMetadata GetMetadata(GetMetadataArgs args);
}
