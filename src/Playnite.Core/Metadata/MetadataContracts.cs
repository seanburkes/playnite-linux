using Playnite.Core.Models;

namespace Playnite.Core.Metadata;

/// <summary>Artwork reference kinds a metadata provider may return.</summary>
public enum MetadataArtworkKind
{
    /// <summary>Box art or an equivalent portrait cover.</summary>
    Cover,

    /// <summary>Wide background artwork.</summary>
    Background,

    /// <summary>Small application icon.</summary>
    Icon,

    /// <summary>Transparent title logo.</summary>
    Logo,

    /// <summary>Wide store header image.</summary>
    Header,

    /// <summary>Wide hero image.</summary>
    Hero,
}

/// <summary>One game identity to resolve through a metadata provider.</summary>
public sealed record MetadataQuery
{
    /// <summary>Creates a query for a provider game id.</summary>
    /// <param name="appId">Provider identity of the game, for example a Steam app id.</param>
    /// <param name="name">Optional display name used by name-based providers.</param>
    /// <param name="pluginId">Optional library plugin id that owns the game.</param>
    public MetadataQuery(string appId, string? name = null, Guid? pluginId = null)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("A provider app id is required.", nameof(appId));
        }

        AppId = appId;
        Name = name;
        PluginId = pluginId;
    }

    /// <summary>Gets the provider identity of the game.</summary>
    public string AppId { get; }

    /// <summary>Gets the optional display name used by name-based providers.</summary>
    public string? Name { get; }

    /// <summary>Gets the optional library plugin id that owns the game.</summary>
    public Guid? PluginId { get; }

    /// <summary>Creates a query from a stored game record.</summary>
    public static MetadataQuery FromGame(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(game.GameId))
        {
            throw new ArgumentException("The game has no provider app id.", nameof(game));
        }

        return new MetadataQuery(
            game.GameId,
            game.Name,
            game.PluginId == Guid.Empty ? null : game.PluginId);
    }
}

/// <summary>A provider-independent web link.</summary>
public sealed record MetadataLink
{
    /// <summary>Creates a named web link.</summary>
    public MetadataLink(string name, string location)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A link name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException("A link location is required.", nameof(location));
        }

        Name = name;
        Location = location;
    }

    /// <summary>Gets the link name.</summary>
    public string Name { get; }

    /// <summary>Gets the link location. It is a reference only and is never dereferenced here.</summary>
    public string Location { get; }
}

/// <summary>An artwork reference. The service never downloads or validates the referenced bytes.</summary>
public sealed record MetadataArtwork
{
    /// <summary>Creates an artwork reference.</summary>
    public MetadataArtwork(MetadataArtworkKind kind, string location)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown artwork kind.");
        }

        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException("An artwork location is required.", nameof(location));
        }

        Kind = kind;
        Location = location;
    }

    /// <summary>Gets the artwork kind.</summary>
    public MetadataArtworkKind Kind { get; }

    /// <summary>Gets the artwork location. It is a reference only and is never dereferenced here.</summary>
    public string Location { get; }
}

/// <summary>
/// The provider-independent metadata resolved for one game. Only values supplied by a provider or
/// read back from its validated cache entry are present; absent data stays absent.
/// </summary>
public sealed record MetadataDocument
{
    /// <summary>Creates a metadata document.</summary>
    /// <param name="description">HTML description, or null when the provider has none.</param>
    /// <param name="links">Web links, or null when the provider has none.</param>
    /// <param name="artwork">Artwork references, or null when the provider has none.</param>
    public MetadataDocument(
        string? description,
        IEnumerable<MetadataLink>? links = null,
        IEnumerable<MetadataArtwork>? artwork = null)
    {
        Description = description;
        Links = Copy(links, nameof(links));
        Artwork = Copy(artwork, nameof(artwork));
    }

    /// <summary>Gets the HTML description, or null when it is unknown.</summary>
    public string? Description { get; }

    /// <summary>Gets the web links. An empty list means the provider supplied none.</summary>
    public IReadOnlyList<MetadataLink> Links { get; }

    /// <summary>Gets the artwork references. An empty list means the provider supplied none.</summary>
    public IReadOnlyList<MetadataArtwork> Artwork { get; }

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T>? values, string parameterName)
        where T : class
    {
        if (values is null)
        {
            return Array.Empty<T>();
        }

        var copy = new List<T>();
        foreach (T value in values)
        {
            if (value is null)
            {
                throw new ArgumentException("A metadata collection cannot contain null.", parameterName);
            }

            copy.Add(value);
        }

        return copy.Count == 0 ? Array.Empty<T>() : copy.ToArray();
    }
}

/// <summary>The result of one provider request.</summary>
public sealed record MetadataProviderResult
{
    private MetadataProviderResult(MetadataDocument? document, string? failureReason)
    {
        Document = document;
        FailureReason = failureReason;
    }

    /// <summary>Gets the resolved document, or null when the provider failed.</summary>
    public MetadataDocument? Document { get; }

    /// <summary>Gets the stable provider failure reason, or null on success.</summary>
    public string? FailureReason { get; }

    /// <summary>Gets whether the provider returned a document.</summary>
    public bool Succeeded => Document is not null;

    /// <summary>Creates a successful provider result.</summary>
    public static MetadataProviderResult Success(MetadataDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new MetadataProviderResult(document, null);
    }

    /// <summary>Creates a failed provider result with a stable reason.</summary>
    public static MetadataProviderResult Failure(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A provider failure reason is required.", nameof(reason));
        }

        return new MetadataProviderResult(null, reason);
    }
}

/// <summary>
/// Injectable metadata source. Implementations own their transport and rate limiting; the service
/// only calls this interface and never opens a socket itself.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>Gets the stable provider identity. It is part of every cache key.</summary>
    string Identity { get; }

    /// <summary>Resolves metadata for one query.</summary>
    Task<MetadataProviderResult> FetchAsync(MetadataQuery query, CancellationToken cancellationToken);
}

/// <summary>How one metadata request was answered.</summary>
public enum MetadataResultStatus
{
    /// <summary>The provider returned fresh metadata, which was also offered to the cache.</summary>
    Resolved,

    /// <summary>A fresh cache entry answered the request without calling the provider.</summary>
    CacheHit,

    /// <summary>The provider failed and the previous cache entry is reported as stale.</summary>
    Stale,

    /// <summary>Offline mode answered the request from the cache, fresh or stale.</summary>
    OfflineCacheHit,

    /// <summary>No metadata is available. The result carries a stable reason.</summary>
    Failed,
}

/// <summary>One per-game metadata result. Failures carry a reason and never abort a batch.</summary>
public sealed record MetadataResult(
    MetadataQuery Query,
    MetadataDocument? Document,
    MetadataResultStatus Status,
    string? FailureReason = null,
    DateTimeOffset? FetchedAt = null,
    bool IsStale = false,
    string? CacheWarning = null)
{
    /// <summary>Gets whether metadata is available, including stale and offline cache results.</summary>
    public bool Succeeded => Document is not null;
}

/// <summary>Ordered per-game results from a batch request.</summary>
public sealed record MetadataBatchResult(IReadOnlyList<MetadataResult> Results)
{
    /// <summary>Gets the number of results that carry a document.</summary>
    public int SucceededCount => Results.Count(result => result.Succeeded);

    /// <summary>Gets the number of results that carry no document.</summary>
    public int FailedCount => Results.Count - SucceededCount;
}
