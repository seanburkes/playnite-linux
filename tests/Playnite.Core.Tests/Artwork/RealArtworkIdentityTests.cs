using System.Globalization;
using Playnite.Core.Tests.Serialization;
using Playnite.Steam.Artwork;
using Xunit;

namespace Playnite.Core.Tests.Artwork;

// Identity-only proof against a real Steam cache manifest. Image bytes and Steam CDN/remote
// mapping are UNKNOWN and out of v1 scope; see tests/fixtures/steam/real-artwork/README.md.
public sealed class RealArtworkIdentityTests
{
    private static readonly uint[] CapturedApps = [219150, 770100, 975370, 1091500, 1145360];

    // The complete set of canonical identities recorded by the capture. 1091500 contributes
    // none: its cache has only content-addressed hashed-name entries.
    private static readonly ArtworkIdentity[] ExpectedIdentities =
    [
        new(219150, ArtworkKind.Header),
        new(219150, ArtworkKind.Portrait),
        new(219150, ArtworkKind.Hero),
        new(219150, ArtworkKind.HeroBlur),
        new(219150, ArtworkKind.Logo),
        new(975370, ArtworkKind.Logo),
        new(1145360, ArtworkKind.Header),
        new(1145360, ArtworkKind.Portrait),
        new(1145360, ArtworkKind.Hero),
        new(1145360, ArtworkKind.HeroBlur),
        new(1145360, ArtworkKind.Logo),
        new(770100, ArtworkKind.Portrait),
        new(770100, ArtworkKind.Hero),
        new(770100, ArtworkKind.HeroBlur),
        new(770100, ArtworkKind.Logo),
    ];

    private const int CapturedFileCount = 31;
    private const int HashedFileCount = 16;

    [Fact]
    public void Manifest_is_deterministic_sorted_and_carries_no_image_bytes()
    {
        var entries = ReadManifest();
        Assert.Equal(CapturedFileCount, entries.Length);
        Assert.Equal(CapturedApps, entries.Select(entry => entry.AppId).Distinct());
        Assert.Equal(entries.OrderBy(entry => entry.AppId).ThenBy(entry => entry.RelativePath, StringComparer.Ordinal), entries);
        Assert.Equal(entries.Length, entries.Select(entry => entry.RelativePath).Distinct(StringComparer.Ordinal).Count());

        foreach (var entry in entries)
        {
            Assert.True(entry.Bytes > 0);
            Assert.Matches("^[0-9a-f]{64}$", entry.Sha256);
            Assert.StartsWith($"appcache/librarycache/{entry.AppId}/", entry.RelativePath);
            Assert.DoesNotContain('\\', entry.RelativePath);
            Assert.DoesNotContain("//", entry.RelativePath);
            Assert.All(entry.RelativePath.Split('/'), part => Assert.NotEqual("..", part));
        }

        string fixture = FixtureRoot();
        Assert.Equal(
            ["README.md", "manifest.sha256", "manifest.tsv"],
            Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(fixture, file))
                .Order(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(fixture, "*.jpg", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(fixture, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public void Self_check_hash_matches_the_manifest_bytes()
    {
        string fixture = FixtureRoot();
        string line = Assert.Single(File.ReadAllLines(Path.Combine(fixture, "manifest.sha256")));
        string[] parts = line.Split("  ", 2, StringSplitOptions.None);
        Assert.Equal(2, parts.Length);
        Assert.Equal("manifest.tsv", parts[1]);
        Assert.Matches("^[0-9a-f]{64}$", parts[0]);
        Assert.Equal(RepositoryPaths.Sha256(Path.Combine(fixture, "manifest.tsv")), parts[0]);
    }

    [Fact]
    public void Empty_tree_is_classified_to_exactly_the_recorded_identities()
    {
        var entries = ReadManifest();
        var known = entries.Select(entry => entry.AppId).ToHashSet();
        var root = Directory.CreateTempSubdirectory("playnite-real-artwork-");
        try
        {
            foreach (var entry in entries)
            {
                string target = Path.Combine([root.FullName, .. entry.RelativePath.Split('/')]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, []);
            }

            string[] files = Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(entries.Length, files.Length);

            var identities = new List<ArtworkIdentity>();
            int hashed = 0;
            foreach (string file in files)
            {
                Assert.Equal(0, new FileInfo(file).Length);
                string relative = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');
                Assert.True(ArtworkIdentity.TryReadCachePath(relative, known, out ArtworkIdentity? identity), relative);
                if (identity is null)
                {
                    hashed++;
                    continue;
                }

                identities.Add(identity);
                Assert.Equal(relative, identity.RelativePath.Replace('\\', '/'));
            }

            Assert.Equal(HashedFileCount, hashed);
            Assert.Equal(entries.Length, identities.Count + hashed);
            Assert.Equal(Ordered(ExpectedIdentities), Ordered(identities));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("appcache/librarycache/219150/library_600x900.jpg", ArtworkKind.Portrait)]
    [InlineData("appcache/librarycache/219150/header.jpg", ArtworkKind.Header)]
    [InlineData("appcache/librarycache/219150/library_hero.jpg", ArtworkKind.Hero)]
    [InlineData("appcache/librarycache/219150/logo.png", ArtworkKind.Logo)]
    [InlineData("appcache/librarycache/219150/library_hero_blur.jpg", ArtworkKind.HeroBlur)]
    public void Documented_names_resolve_to_their_identity(string path, ArtworkKind kind)
    {
        Assert.True(ArtworkIdentity.TryReadCachePath(path, CapturedApps.ToHashSet(), out ArtworkIdentity? identity));
        Assert.Equal(new ArtworkIdentity(219150, kind), identity);
    }

    [Theory]
    [InlineData("appcache/librarycache/219150/493c0a3e823c60c14f91204526f26cc064ed7c84.jpg")]
    [InlineData("appcache/librarycache/975370/34e18f5695fffd64458b78110930abc5258ccd14/library_header.jpg")]
    [InlineData("appcache/librarycache/1091500/cf8cec802dc47d0f24b75f9eee135e96812e2652/library_hero.jpg")]
    [InlineData("appcache/librarycache/770100/04a3a43772e764c473902ea13b4a3385f28b4174/markers.svg")]
    public void Observed_hashed_entries_are_accepted_without_an_inferred_identity(string path)
    {
        Assert.True(ArtworkIdentity.TryReadCachePath(path, CapturedApps.ToHashSet(), out ArtworkIdentity? identity));
        Assert.Null(identity);
    }

    [Theory]
    [InlineData("appcache/librarycache/999999/header.jpg")]
    [InlineData("appcache/librarycache/0/header.jpg")]
    [InlineData("appcache/librarycache/0219150/header.jpg")]
    [InlineData("appcache/librarycache/219150/header.png")]
    [InlineData("appcache/librarycache/219150/HEADER.JPG")]
    [InlineData("appcache/librarycache/219150/library_header.jpg")]
    [InlineData("appcache/librarycache/219150/library_hero_blur.png")]
    [InlineData("appcache/librarycache/219150/f2590e71d4b5c48c200ecc4d8b6e0eddb60eff20.PNG")]
    [InlineData("appcache/librarycache/219150/F2590E71D4B5C48C200ECC4D8B6E0EDDB60EFF20.jpg")]
    [InlineData("appcache/librarycache/219150/f2590e71d4b5c48c200ecc4d8b6e0eddb60eff2.jpg")]
    [InlineData("appcache/librarycache/219150/f2590e71d4b5c48c200ecc4d8b6e0eddb60eff200.jpg")]
    [InlineData("appcache/librarycache/219150/header.jpg/extra")]
    [InlineData("appcache/librarycache/219150/../header.jpg")]
    [InlineData("appcache/librarycache/219150/")]
    [InlineData("appcache/librarycache/219150")]
    [InlineData("appcache\\librarycache\\219150\\header.jpg")]
    [InlineData("steamapps/librarycache/219150/header.jpg")]
    [InlineData("config/librarycache/219150.json")]
    [InlineData("")]
    public void Unknown_names_unknown_appids_and_malformed_paths_are_rejected(string path)
    {
        Assert.False(ArtworkIdentity.TryReadCachePath(path, CapturedApps.ToHashSet(), out ArtworkIdentity? identity));
        Assert.Null(identity);
    }

    private static ArtworkIdentity[] Ordered(IEnumerable<ArtworkIdentity> identities) =>
        identities.OrderBy(identity => identity.AppId).ThenBy(identity => identity.Kind).ToArray();

    private static string FixtureRoot() => RepositoryPaths.Combine("tests", "fixtures", "steam", "real-artwork");

    private static ManifestEntry[] ReadManifest()
    {
        string[] lines = File.ReadAllLines(Path.Combine(FixtureRoot(), "manifest.tsv"));
        Assert.NotEmpty(lines);
        Assert.Equal("appid\trelative_path\tbytes\tsha256", lines[0]);
        return lines.Skip(1).Select(line =>
        {
            string[] parts = line.Split('\t');
            Assert.Equal(4, parts.Length);
            return new ManifestEntry(
                uint.Parse(parts[0], CultureInfo.InvariantCulture),
                parts[1],
                long.Parse(parts[2], CultureInfo.InvariantCulture),
                parts[3]);
        }).ToArray();
    }

    private sealed record ManifestEntry(uint AppId, string RelativePath, long Bytes, string Sha256);
}
