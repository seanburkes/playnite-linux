using System.Security.Cryptography;
using Playnite.Core.Database;
using Playnite.Core.Serialization;
using Playnite.Core.Tests.Database;
using Playnite.Core.Tests.Serialization;
using Playnite.Core.Tests.TestDoubles;
using Playnite.Platform.Linux;
using Playnite.Steam;
using Playnite.Steam.Sync;
using Xunit;

namespace Playnite.Core.Tests.Steam;

/// <summary>
/// Integration evidence for the sync service over the redacted real Steam corpus. The corpus is
/// copied into a temporary home and its recorded paths are remapped; no test reads the developer's
/// Steam installation, reaches the network, or loads an Avalonia assembly.
/// </summary>
public sealed class SteamLibrarySyncIntegrationTests
{
    private static readonly string Corpus = RepositoryPaths.Combine("tests", "fixtures", "steam", "real");

    [Fact]
    public async Task Real_corpus_syncs_four_manifests_reports_every_other_declared_app_and_is_idempotent()
    {
        var home = Directory.CreateTempSubdirectory("playnite-steam-sync-").FullName;
        try
        {
            var root = Path.Combine(home, ".local", "share", "Steam");
            CopyCorpus(root);
            var external = Path.Combine(home, "mnt", "external", "SteamLibrary");
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            var libraries = File.ReadAllText(libraryFile)
                .Replace("/home/test/.local/share/Steam", root, StringComparison.Ordinal)
                .Replace("/mnt/external/SteamLibrary", external, StringComparison.Ordinal);
            File.WriteAllText(libraryFile, libraries);

            var environment = new MemoryEnvironment { Home = home };
            var fileSystem = new SteamFileSystem(new LinuxBoundedFileReader());
            var paths = new LinuxPaths(environment, new LinuxFileSystem());
            var snapshot = new SteamDiscoverySource(environment, paths, fileSystem).Discover();
            var discovery = SteamDiscovery.Discover(environment, paths, fileSystem);

            Assert.True(snapshot.CanReconcile);
            Assert.Equal([105600u, 427520u, 1238040u, 1493710u], snapshot.Apps.Select(app => app.AppId));
            Assert.Equal(root, discovery.Root);

            var rootLibrary = discovery.Libraries.Single(library => library.Index == "0");
            var externalLibrary = discovery.Libraries.Single(library => library.Index == "1");
            var manifestIds = snapshot.Apps.Select(app => app.AppId).ToHashSet();
            Assert.Equal(
                rootLibrary.Apps!.Keys.Except(manifestIds).Order(),
                snapshot.Skipped.Where(skip => skip.Reason == SteamSyncReasons.ManifestMissing)
                    .Select(skip => skip.AppId).Order());
            Assert.Equal(
                externalLibrary.Apps!.Keys.Order(),
                snapshot.Skipped.Where(skip => skip.Reason == SteamSyncReasons.LibraryMissing)
                    .Select(skip => skip.AppId).Order());
            Assert.All(snapshot.Skipped, skip => Assert.False(string.IsNullOrWhiteSpace(skip.Reason)));
            Assert.All(snapshot.Skipped, skip => Assert.Contains(skip.AppId, rootLibrary.Apps!.Keys.Concat(externalLibrary.Apps!.Keys)));

            using var files = new DatabaseTestDirectory();
            var database = await LibraryDatabase.OpenAsync(files.PathFor());
            var service = new SteamLibrarySyncService(new SteamDiscoverySource(environment, paths, fileSystem),
                new FixedTimeProvider());

            var first = await service.SyncAsync(database, TestContext.Current.CancellationToken);

            Assert.Equal(4, first.Added);
            Assert.Equal(0, first.Updated);
            Assert.Equal(0, first.Removed);
            Assert.Equal(snapshot.Skipped.Count, first.SkippedCount);
            Assert.Equal(4, first.Changes);
            Assert.True(first.Reconciled);

            var stored = (await database.ReadAsync(session => session.Games.GetAllAsync()))
                .OrderBy(game => uint.Parse(game.Value.GameId!)).ToArray();
            Assert.Equal([105600, 427520, 1238040, 1493710], stored.Select(game => uint.Parse(game.Value.GameId!)));
            Assert.All(stored, game => Assert.Equal(new Guid(SteamLibrarySyncService.SteamPluginId), game.Value.PluginId));
            // The redacted corpus intentionally contains no game content, so every recorded
            // install directory is absent and the pure projection reports Broken.
            Assert.All(stored, game => Assert.False(game.Value.IsInstalled));
            Assert.Equal(
                [
                    (105600u, "Terraria", Path.Combine(root, "steamapps", "common", "Terraria"), 831365954UL),
                    (427520u, "Factorio", Path.Combine(root, "steamapps", "common", "Factorio"), 2387398965UL),
                    (1238040u, "Dragon Age II: Ultimate Edition", Path.Combine(root, "steamapps", "common", "Dragon Age II"), 10532431786UL),
                    (1493710u, "Proton Experimental", Path.Combine(root, "steamapps", "common", "Proton - Experimental"), 1539520708UL),
                ],
                stored.Select(game => (uint.Parse(game.Value.GameId!), game.Value.Name,
                    game.Value.InstallDirectory, game.Value.InstallSize!.Value)));

            // The unavailable external library's apps stay out of the library and are reported.
            Assert.DoesNotContain(stored, game => externalLibrary.Apps!.ContainsKey(uint.Parse(game.Value.GameId!)));
            var serialized = stored.Select(game => JsonSerialization.ToJson(game.Value)).ToArray();

            var second = await service.SyncAsync(database, TestContext.Current.CancellationToken);

            Assert.Equal(0, second.Added);
            Assert.Equal(0, second.Updated);
            Assert.Equal(0, second.Removed);
            Assert.Equal(4, second.Unchanged);
            Assert.Equal(0, second.Changes);
            Assert.Equal(snapshot.Skipped.Count, second.SkippedCount);
            Assert.Equal(serialized,
                (await database.ReadAsync(session => session.Games.GetAllAsync()))
                    .OrderBy(game => uint.Parse(game.Value.GameId!))
                    .Select(game => JsonSerialization.ToJson(game.Value)));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static void CopyCorpus(string root)
    {
        var checksums = File.ReadAllLines(Path.Combine(Corpus, "SHA256SUMS"));
        foreach (var line in checksums)
        {
            var parts = line.Split("  ", 2, StringSplitOptions.None);
            Assert.Equal(2, parts.Length);
            var source = Path.Combine(Corpus, parts[1]);
            var bytes = File.ReadAllBytes(source);
            Assert.Equal(parts[0], Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var target = Path.Combine(root, parts[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
    }
}
