using System.Globalization;

namespace Playnite.Steam.Artwork;

public enum ArtworkKind { Portrait, Header, Hero, Logo, HeroBlur, Achievements }

public sealed record ArtworkIdentity(uint AppId, ArtworkKind Kind)
{
    // Observed names only: Steam spec §8. No content-addressed Steam ids or CDN inference.
    public string FileName => Kind switch
    {
        ArtworkKind.Portrait => "library_600x900.jpg",
        ArtworkKind.Header => "header.jpg",
        ArtworkKind.Hero => "library_hero.jpg",
        ArtworkKind.Logo => "logo.png",
        ArtworkKind.HeroBlur => "library_hero_blur.jpg",
        ArtworkKind.Achievements => AppId.ToString(CultureInfo.InvariantCulture) + ".json",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };

    public string RelativePath => Kind == ArtworkKind.Achievements
        ? Path.Combine("config", "librarycache", FileName)
        : Path.Combine("appcache", "librarycache", AppId.ToString(CultureInfo.InvariantCulture), FileName);
}

// Resolve must be local/pure; offline calls may resolve identity but must not perform discovery HTTP.
// Null means this title/kind is not in the configured evidence, not permission to infer a URL.
public interface IArtworkSource
{
    ArtworkSource? Resolve(ArtworkIdentity identity);
}

public sealed record ArtworkSource(Uri Location, string? ExpectedSha256 = null);
public sealed record ArtworkIssue(uint AppId, ArtworkKind Kind, string Reason);
public sealed record ArtworkResult(string? Path, IReadOnlyList<ArtworkIssue> Issues);

public interface IArtworkClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class ArtworkClock : IArtworkClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
