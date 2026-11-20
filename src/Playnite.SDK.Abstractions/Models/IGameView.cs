namespace Playnite.SDK.Models;

/// <summary>
/// The game fields an SDK-facing surface exposes without the Core database model. A host
/// projects a Core <c>Playnite.Core.Models.Game</c> onto this view through
/// <c>Playnite.Core.Hosting.GameViewAdapter</c>, and the host responses carry copies of their
/// game lists, taken with <c>ToArray()</c> when the response is built.
/// </summary>
public interface IGameView
{
    /// <summary>Stable database identity.</summary>
    Guid Id { get; }

    /// <summary>Display name.</summary>
    string Name { get; }
}
