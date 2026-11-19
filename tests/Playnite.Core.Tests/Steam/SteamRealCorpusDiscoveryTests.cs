using System.Text.Json;
using Playnite.Core.Tests.Serialization;
using Playnite.Core.Tests.TestDoubles;
using Playnite.Platform.Linux;
using Playnite.Steam;
using Playnite.Steam.Artwork;
using Xunit;

namespace Playnite.Core.Tests.Steam;

public sealed class SteamRealCorpusDiscoveryTests
{
    // Recorded app keys in libraryfolders.vdf, in library index order.
    private static readonly uint[] RootApps =
    [
        11450, 21000, 32400, 39150, 40990, 105600, 110800, 112100, 200260, 208520,
        214340, 215530, 219150, 219740, 228980, 233290, 239160, 266510, 283880, 294100,
        304240, 307780, 333640, 359870, 377840, 396750, 404790, 413150, 415910, 427520,
        462770, 474750, 480640, 489830, 493900, 544750, 570680, 582010, 622440, 638070,
        646570, 659540, 770100, 811320, 870780, 956450, 960910, 960990, 975370, 976730,
        1070560, 1091010, 1091500, 1105510, 1118310, 1145360, 1173820, 1174180, 1235140, 1238040,
        1281930, 1308380, 1325860, 1388590, 1391110, 1486350, 1493710, 1628350, 1671210, 1826330,
        1905530, 1976950, 2344520, 2346660, 2373630, 2522260, 2805730, 3086180, 3658110, 4183110,
        4628710, 4930770, 4935160,
    ];

    private static readonly uint[] ExternalApps =
    [
        1510, 1520, 25620, 26500, 207170, 220200, 226840, 233450, 248860, 262060,
        289070, 322330, 326410, 365450, 397060, 398850, 400110, 404410, 416210, 454060,
        464350, 511820, 528180, 569480, 717640,
    ];

    // The corpus contains no artwork images. This test covers the recorded achievement
    // metadata identity, so artwork identification remains unproven by this test.
    [Fact]
    public void Redacted_real_corpus_yields_recorded_apps_library_paths_and_achievement_identity()
    {
        var corpus = RepositoryPaths.Combine("tests", "fixtures", "steam", "real");
        var files = VerifyCorpus(corpus);
        var fileSystem = new SteamFileSystem(new LinuxBoundedFileReader());
        var temporary = Directory.CreateTempSubdirectory("playnite-steam-real-");
        try
        {
            var home = fileSystem.GetRealPath(temporary.FullName);
            Assert.NotNull(home);
            var root = Path.Combine(home, ".local", "share", "Steam");
            foreach (var file in files)
            {
                var target = Path.Combine(root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(corpus, file), target);
            }

            // Remap both recorded paths into the temporary tree. Leave the external library absent.
            var external = Path.Combine(home, "mnt", "external", "SteamLibrary");
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            var libraries = File.ReadAllText(libraryFile)
                .Replace("/home/test/.local/share/Steam", root, StringComparison.Ordinal)
                .Replace("/mnt/external/SteamLibrary", external, StringComparison.Ordinal);
            File.WriteAllText(libraryFile, libraries);

            var environment = new MemoryEnvironment { Home = home };
            var discovery = SteamDiscovery.Discover(environment, new LinuxPaths(environment, new LinuxFileSystem()), fileSystem);

            Assert.Equal(root, discovery.Root);
            Assert.Equal(libraryFile, discovery.LibraryFilePath);
            Assert.Empty(discovery.Issues);
            Assert.Collection(discovery.Libraries,
                library => AssertLibrary(library, "0", root, SteamLibraryStatus.Available, RootApps),
                library => AssertLibrary(library, "1", external, SteamLibraryStatus.MissingPath, ExternalApps));

            var localLibrary = discovery.Libraries[0];
            var manifests = Directory.EnumerateFiles(Path.Combine(root, "steamapps"), "appmanifest_*.acf")
                .Select(file =>
                {
                    var parsed = SteamManifestParser.Parse(File.ReadAllBytes(file), file);
                    Assert.Empty(parsed.Issues);
                    Assert.NotNull(parsed.Value);
                    Assert.Contains(parsed.Value.AppId, localLibrary.Apps!.Keys);
                    return parsed.Value;
                })
                .OrderBy(manifest => manifest.AppId)
                .Select(manifest => manifest.ToApp(localLibrary.ResolvedPath, localLibrary.IsAvailable, false, false))
                .ToArray();
            var common = Path.Combine(root, "steamapps", "common");
            Assert.Equal(
                [(105600u, Path.Combine(common, "Terraria")), (427520u, Path.Combine(common, "Factorio")),
                 (1238040u, Path.Combine(common, "Dragon Age II")), (1493710u, Path.Combine(common, "Proton - Experimental"))],
                manifests.Select(app => (app.AppId, app.InstallPath)));

            var dragonAge = Assert.Single(manifests, app => app.AppId == 1238040);
            var identity = new ArtworkIdentity(dragonAge.AppId, ArtworkKind.Achievements);
            var metadataPath = Path.Combine("config", "librarycache", "1238040.json");
            Assert.Equal(metadataPath, identity.RelativePath);
            using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "userdata", "00000000", identity.RelativePath)));
            var section = Assert.Single(metadata.RootElement.EnumerateArray());
            Assert.Equal(2, section.GetArrayLength());
            Assert.Equal("achievements", section[0].GetString());
            Assert.Equal(2, section[1].GetProperty("version").GetInt32());
            var data = section[1].GetProperty("data");
            Assert.Equal(0, data.GetProperty("nTotal").GetInt32());
            Assert.Equal(0, data.GetProperty("nAchieved").GetInt32());
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static void AssertLibrary(SteamDiscoveredLibrary library, string index, string path,
        SteamLibraryStatus status, uint[] apps)
    {
        Assert.Equal(index, library.Index);
        Assert.Equal(path, library.DeclaredPath);
        Assert.Equal(path, library.ResolvedPath);
        Assert.Equal(status, library.Status);
        Assert.NotNull(library.Apps);
        Assert.Equal(apps, library.Apps.Keys.Order());
    }

    private static string[] VerifyCorpus(string corpus)
    {
        const string checksumFile = "SHA256SUMS";
        var checksums = File.ReadAllLines(Path.Combine(corpus, checksumFile))
            .Select(line => line.Split("  ", 2, StringSplitOptions.None)).ToArray();
        Assert.All(checksums, parts =>
        {
            Assert.Equal(2, parts.Length);
            Assert.Equal(64, parts[0].Length);
        });
        var files = checksums.Select(parts => parts[1]).ToArray();
        Assert.Equal(files.Append(checksumFile).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(corpus, file)).Order(StringComparer.Ordinal));
        Assert.All(checksums, parts =>
            Assert.Equal((parts[1], parts[0]), (parts[1], RepositoryPaths.Sha256(Path.Combine(corpus, parts[1])))));
        return files;
    }
}
