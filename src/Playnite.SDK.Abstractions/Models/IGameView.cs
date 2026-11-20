namespace Playnite.SDK.Models;

/// <summary>
/// The game fields an SDK-facing surface exposes without the Core database model. The Core
/// <c>Playnite.Core.Models.Game</c> implements it, so a host can hand Core games to the
/// abstraction without copying or re-projecting them.
/// </summary>
public interface IGameView
{
    /// <summary>Stable database identity.</summary>
    Guid Id { get; }

    /// <summary>Display name.</summary>
    string Name { get; }
}
