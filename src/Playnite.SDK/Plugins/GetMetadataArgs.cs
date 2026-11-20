using Playnite.SDK.Models;

namespace Playnite.SDK.Plugins;

/// <summary>
/// A metadata request for one game.
/// </summary>
/// <remarks>
/// PREVIEW, NOT FROZEN. Part of the p6-009 headless preview surface. The game is the frozen
/// <see cref="IGameView"/> projection rather than the deferred v6 <c>Game</c> model.
/// </remarks>
public class GetMetadataArgs
{
    /// <summary>Creates a metadata request.</summary>
    public GetMetadataArgs(IGameView game, IReadOnlyList<MetadataField> fields)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(fields);
        Game = game;
        Fields = fields;
    }

    /// <summary>Gets the game being enriched.</summary>
    public IGameView Game { get; }

    /// <summary>Gets the requested fields.</summary>
    public IReadOnlyList<MetadataField> Fields { get; }
}
