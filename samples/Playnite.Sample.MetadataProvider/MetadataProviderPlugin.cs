using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace Playnite.Sample.MetadataProvider;

/// <summary>
/// Minimal metadata extension: answers a synchronous request with deterministic values derived
/// from the requested game and fields, with no network access.
/// </summary>
public sealed class MetadataProviderPlugin : MetadataPlugin
{
    /// <summary>Stable extension id for this sample.</summary>
    public static readonly Guid ExtensionId = Guid.Parse("3c4d5e6f-7a8b-4c9d-8e0f-1a2b3c4d5e6f");

    /// <inheritdoc/>
    public override Guid Id => ExtensionId;

    /// <inheritdoc/>
    public override string Name => "Sample Metadata";

    /// <inheritdoc/>
    public override IReadOnlyList<MetadataField> SupportedFields { get; } =
    [
        MetadataField.Name,
        MetadataField.Description,
        MetadataField.Developers,
        MetadataField.Genres,
        MetadataField.ReleaseDate,
    ];

    /// <inheritdoc/>
    public override GameMetadata GetMetadata(GetMetadataArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var metadata = new GameMetadata();
        foreach (var field in args.Fields)
        {
            switch (field)
            {
                case MetadataField.Name:
                    metadata.Name = args.Game.Name;
                    break;
                case MetadataField.Description:
                    metadata.Description = $"Sample metadata for {args.Game.Name}.";
                    break;
                case MetadataField.Developers:
                    metadata.Developers = ["Sample Metadata Studio"];
                    break;
                case MetadataField.Genres:
                    metadata.Genres = ["Sample"];
                    break;
                case MetadataField.ReleaseDate:
                    metadata.ReleaseDate = new DateOnly(2026, 1, 2);
                    break;
            }
        }

        return metadata;
    }
}
