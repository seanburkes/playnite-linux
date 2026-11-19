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

    private static readonly Dictionary<string, ArtworkKind> NamedCacheFiles = new(StringComparer.Ordinal)
    {
        ["library_600x900.jpg"] = ArtworkKind.Portrait,
        ["header.jpg"] = ArtworkKind.Header,
        ["library_hero.jpg"] = ArtworkKind.Hero,
        ["logo.png"] = ArtworkKind.Logo,
        ["library_hero_blur.jpg"] = ArtworkKind.HeroBlur,
    };

    // Reads one path relative to the Steam root. A documented canonical name yields an
    // identity. An observed content-addressed entry yields true with a null identity: its
    // kind and remote id are UNKNOWN and are never inferred. Unknown app ids, names and
    // malformed paths are rejected. Separators are '/' as in the recorded Linux layout.
    public static bool TryReadCachePath(string relativePath, IReadOnlySet<uint> knownAppIds,
        out ArtworkIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(knownAppIds);
        identity = null;
        if (string.IsNullOrEmpty(relativePath) || relativePath.Contains('\\')) return false;
        string[] parts = relativePath.Split('/');
        if (parts.Length < 4 || parts[0] != "appcache" || parts[1] != "librarycache") return false;

        string appText = parts[2];
        if (appText.Length == 0 || (appText.Length > 1 && appText[0] == '0')) return false;
        foreach (char c in appText) if (!char.IsAsciiDigit(c)) return false;
        if (!uint.TryParse(appText, NumberStyles.None, CultureInfo.InvariantCulture, out uint appId)) return false;
        if (appId == 0 || !knownAppIds.Contains(appId)) return false;

        string[] name = parts[3..];
        foreach (string segment in name)
            if (segment.Length == 0 || segment == "." || segment == "..") return false;

        if (name.Length == 1 && NamedCacheFiles.TryGetValue(name[0], out ArtworkKind kind))
        {
            identity = new ArtworkIdentity(appId, kind);
            return true;
        }
        // Observed hashed forms: <40 lowercase hex>.jpg, or <40 lowercase hex>/<child>.
        return name.Length == 1 ? IsContentAddressedFileName(name[0]) : IsContentAddressedDirectory(name[0]);
    }

    private static bool IsContentAddressedFileName(string name)
    {
        string stem = name.EndsWith(".jpg", StringComparison.Ordinal) ? name[..^".jpg".Length] : name;
        return IsContentAddressedDirectory(stem);
    }

    private static bool IsContentAddressedDirectory(string name)
    {
        if (name.Length != 40) return false;
        foreach (char c in name) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
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
