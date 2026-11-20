namespace Playnite.SDK.Models;

/// <summary>
/// Metadata a library or metadata extension supplies for one game.
/// </summary>
/// <remarks>
/// PREVIEW, NOT FROZEN. Part of the p6-009 headless preview surface. It carries only the fields
/// the samples use; the full v6 <c>Game</c>/<c>GameMetadata</c> model remains deferred.
/// </remarks>
public class GameMetadata
{
    /// <summary>Gets or sets the display name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the provider's stable game id.</summary>
    public string? GameId { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the release date.</summary>
    public DateOnly? ReleaseDate { get; set; }

    /// <summary>Gets or sets the developer names.</summary>
    public List<string>? Developers { get; set; }

    /// <summary>Gets or sets the genre names.</summary>
    public List<string>? Genres { get; set; }
}
