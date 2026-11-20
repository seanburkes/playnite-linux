using Playnite.SDK.Models;

namespace Playnite.SDK.Plugins;

/// <summary>
/// Base type for a library discovery extension.
/// </summary>
/// <remarks>
/// PREVIEW, NOT FROZEN. Part of the p6-009 headless preview surface; see
/// <see cref="Plugin"/>. Only the discovery path is present: import, play, install and UI hooks
/// remain deferred.
/// </remarks>
public abstract class LibraryPlugin : Plugin
{
    /// <summary>Gets the games the library currently exposes.</summary>
    public virtual IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
    {
        return [];
    }
}
