using Playnite.Core.Tests.Serialization;
using Playnite.Core.Tests.TestDoubles;
using Playnite.Platform.Linux;
using Playnite.Steam;
using Xunit;

namespace Playnite.Core.Tests.Steam;

public sealed class SteamRealDiscoveryTests
{
    [Fact]
    public void Redacted_real_corpus_discovers_expected_apps_paths_and_platform_evidence()
    {
        var corpus = RepositoryPaths.Combine("tests", "fixtures", "steam", "real");
        var temporary = Directory.CreateTempSubdirectory("playnite-p5-008-real-").FullName;
        try
        {
            var root = Path.Combine(temporary, ".local", "share", "Steam");
            var steamapps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
            var libraries = File.ReadAllText(Path.Combine(corpus, "steamapps", "libraryfolders.vdf"))
                .Replace("/home/test", temporary, StringComparison.Ordinal)
                .Replace("/mnt/external", Path.Combine(temporary, "unmounted"), StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), libraries);
            var manifests = Directory.GetFiles(Path.Combine(corpus, "steamapps"), "appmanifest_*.acf")
                .Select(file => SteamManifestParser.Parse(File.ReadAllBytes(file), file)).ToArray();
            Assert.All(manifests, result => Assert.Empty(result.Issues));
            Assert.Equal([105600u, 427520u, 1238040u, 1493710u], manifests.Select(result => result.Value!.AppId).Order());
            foreach (var manifest in manifests.Select(result => result.Value!))
            {
                Directory.CreateDirectory(manifest.ToApp(root, true, true, false).InstallPath);
            }

            var environment = new MemoryEnvironment { Home = temporary };
            environment.Variables["STEAM_ROOT"] = root;
            var discovered = SteamDiscovery.Discover(environment, new LinuxPaths(environment, new LinuxFileSystem()),
                new SteamFileSystem(new LinuxBoundedFileReader()));
            Assert.Equal(root, discovered.Root);
            Assert.Empty(discovered.Issues);
            Assert.Equal(2, discovered.Libraries.Count);
            Assert.Equal(root, discovered.Libraries[0].ResolvedPath);
            Assert.True(discovered.Libraries[0].IsAvailable);
            Assert.False(discovered.Libraries[1].IsAvailable);
            Assert.Equal(Path.Combine(temporary, "unmounted", "SteamLibrary"), discovered.Libraries[1].DeclaredPath);
            Assert.Equal([1510u, 1520u, 25620u, 26500u, 207170u, 220200u, 226840u, 233450u, 248860u,
                262060u, 289070u, 322330u, 326410u, 365450u, 397060u, 398850u, 400110u, 404410u,
                416210u, 454060u, 464350u, 511820u, 528180u, 569480u, 717640u], discovered.Libraries[1].Apps!.Keys.Order());
            Assert.Equal(83, discovered.Libraries[0].Apps!.Count);
            Assert.All(manifests, result => Assert.Contains(result.Value!.AppId, discovered.Libraries[0].Apps!.Keys));

            var expected = new Dictionary<uint, string>
            {
                [105600] = "Terraria", [427520] = "Factorio", [1238040] = "Dragon Age II", [1493710] = "Proton - Experimental",
            };
            foreach (var manifest in manifests.Select(result => result.Value!))
            {
                Assert.Equal(Path.Combine(root, "steamapps", "common", expected[manifest.AppId]),
                    manifest.ToApp(root, true, true, false).InstallPath);
            }

            var overrides = KeyValues.Parse(File.ReadAllBytes(Path.Combine(corpus, "userdata", "00000000", "config", "compat.vdf")), "compat.vdf");
            Assert.Equal("platform_overrides", overrides.Key);
            var dragonAge = manifests.Single(result => result.Value!.AppId == 1238040).Value!;
            Assert.Equal(SteamExecutionKind.Proton, SteamExecutionSelection.Evaluate(dragonAge, overrides.Object, false,
                File.Exists(Path.Combine(corpus, "steamapps", "compatdata", "1238040", "config_info"))).Kind);
            foreach (var id in new[] { 105600u, 427520u })
            {
                var native = manifests.Single(result => result.Value!.AppId == id).Value!;
                Assert.Equal(SteamExecutionKind.Unknown, SteamExecutionSelection.Evaluate(native, overrides.Object,
                    Directory.Exists(Path.Combine(corpus, "steamapps", "compatdata", id.ToString())), false).Kind);
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
