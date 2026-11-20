using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace Playnite.Sample.LibraryProvider;

/// <summary>
/// Minimal library extension: returns two deterministic games without touching a store,
/// network or database.
/// </summary>
public sealed class LibraryProviderPlugin : LibraryPlugin
{
    /// <summary>Stable extension id for this sample.</summary>
    public static readonly Guid ExtensionId = Guid.Parse("9b2e4d6f-1a3c-4e5b-8c7d-0f1e2d3c4b5a");

    /// <inheritdoc/>
    public override Guid Id => ExtensionId;

    /// <inheritdoc/>
    public override string Name => "Sample Library";

    /// <inheritdoc/>
    public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        args.CancelToken.ThrowIfCancellationRequested();

        yield return new GameMetadata
        {
            Name = "Sample Quest",
            GameId = "sample-quest",
            Description = "A deterministic sample game.",
            ReleaseDate = new DateOnly(2024, 3, 14),
            Developers = ["Sample Studio"],
            Genres = ["Adventure"],
        };

        yield return new GameMetadata
        {
            Name = "Sample Rally",
            GameId = "sample-rally",
            Description = "A second deterministic sample game.",
            ReleaseDate = new DateOnly(2025, 7, 1),
            Developers = ["Sample Studio"],
            Genres = ["Racing"],
        };
    }
}
