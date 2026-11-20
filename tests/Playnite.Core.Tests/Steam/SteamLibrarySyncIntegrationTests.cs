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

    // A truncated library file loses the second library without saying so: the parser rejects the
    // whole file, p5-002 still reports LibraryFilePath, and the root library is the only entry
    // left. An app that lived in the lost library is now absent from the observation through no
    // fault of the user, so the sync must not treat it as uninstalled.
    [Fact]
    public async Task Truncated_library_file_suppresses_removal_and_reports_a_degraded_sync()
    {
        var home = Directory.CreateTempSubdirectory("playnite-steam-truncated-").FullName;
        try
        {
            var root = Path.Combine(home, ".local", "share", "Steam");
            var external = Path.Combine(home, "mnt", "external", "SteamLibrary");
            InstallManifest(root, 427520, "Factorio");
            InstallManifest(external, 105600, "Terraria");
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            var declaration = TwoLibraryDeclaration(root, external);
            File.WriteAllText(libraryFile, declaration);

            var host = ProductionHost(home);
            using var files = new DatabaseTestDirectory();
            var database = await LibraryDatabase.OpenAsync(files.PathFor());
            var service = new SteamLibrarySyncService(host, new FixedTimeProvider());

            var first = await service.SyncAsync(database, TestContext.Current.CancellationToken);
            Assert.Equal(2, first.Added);
            Assert.Equal(0, first.Removed);
            Assert.True(first.Reconciled);
            Assert.All(await ReadAll(database), game => Assert.True(game.Value.IsInstalled));

            // Truncation still leaves the root library enumerable, so the external app would be
            // the only removal a naive sync performs.
            File.WriteAllText(libraryFile, declaration[..(declaration.Length / 2)]);

            var snapshot = host.Discover(TestContext.Current.CancellationToken);
            Assert.False(snapshot.CanReconcile);
            Assert.Contains(snapshot.Issues, issue => issue.Reason == SteamSyncReasons.LibraryFileRejected);
            Assert.DoesNotContain(snapshot.Apps, app => app.AppId == 105600);

            var degraded = await service.SyncAsync(database, TestContext.Current.CancellationToken);

            Assert.Equal(1, degraded.Unchanged);
            Assert.Equal(0, degraded.Added);
            Assert.Equal(0, degraded.Updated);
            Assert.Equal(0, degraded.Removed);
            Assert.Equal(0, degraded.Changes);
            Assert.False(degraded.Reconciled);
            Assert.Contains(degraded.Issues, issue => issue.Reason == SteamSyncReasons.LibraryFileRejected);
            var stored = (await ReadAll(database)).OrderBy(game => uint.Parse(game.Value.GameId!)).ToArray();
            Assert.Equal(["105600", "427520"], stored.Select(game => game.Value.GameId));
            Assert.All(stored, game => Assert.True(game.Value.IsInstalled,
                "a degraded discovery must not uninstall a game"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // The parser drops a declared entry whose path is not absolute, so neither its path nor its
    // apps reach discovery. Its games were never observed and must not be removed.
    [Fact]
    public async Task Dropped_library_entry_suppresses_removal_and_reports_a_degraded_sync()
    {
        var home = Directory.CreateTempSubdirectory("playnite-steam-dropped-").FullName;
        try
        {
            var root = Path.Combine(home, ".local", "share", "Steam");
            var external = Path.Combine(home, "mnt", "external", "SteamLibrary");
            InstallManifest(root, 427520, "Factorio");
            InstallManifest(external, 105600, "Terraria");
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            File.WriteAllText(libraryFile, TwoLibraryDeclaration(root, external));

            var host = ProductionHost(home);
            using var files = new DatabaseTestDirectory();
            var database = await LibraryDatabase.OpenAsync(files.PathFor());
            var service = new SteamLibrarySyncService(host, new FixedTimeProvider());

            var first = await service.SyncAsync(database, TestContext.Current.CancellationToken);
            Assert.Equal(2, first.Added);
            Assert.All(await ReadAll(database), game => Assert.True(game.Value.IsInstalled));

            File.WriteAllText(libraryFile, TwoLibraryDeclaration(root, string.Empty));

            var snapshot = host.Discover(TestContext.Current.CancellationToken);
            Assert.False(snapshot.CanReconcile);
            Assert.Contains(snapshot.Issues, issue => issue.Reason == SteamSyncReasons.LibraryEntryDropped);
            Assert.DoesNotContain(snapshot.Apps, app => app.AppId == 105600);
            Assert.DoesNotContain(snapshot.Skipped, skip => skip.AppId == 105600);

            var degraded = await service.SyncAsync(database, TestContext.Current.CancellationToken);

            Assert.Equal(0, degraded.Removed);
            Assert.Equal(0, degraded.Changes);
            Assert.False(degraded.Reconciled);
            var stored = await ReadAll(database);
            Assert.Equal(2, stored.Count);
            Assert.All(stored, game => Assert.True(game.Value.IsInstalled));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // An unavailable library that declares no app set gives discovery nothing to report, so its
    // stored games are unknown rather than absent. A declared app set (the real corpus case) is
    // still safe, because every declared id is reported as skipped and stays observed.
    [Fact]
    public async Task Unavailable_library_without_a_declared_app_set_suppresses_removal()
    {
        var home = Directory.CreateTempSubdirectory("playnite-steam-no-apps-").FullName;
        try
        {
            var root = Path.Combine(home, ".local", "share", "Steam");
            var external = Path.Combine(home, "mnt", "external", "SteamLibrary");
            InstallManifest(root, 427520, "Factorio");
            InstallManifest(external, 105600, "Terraria");
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            File.WriteAllText(libraryFile, TwoLibraryDeclaration(root, external));

            var host = ProductionHost(home);
            using var files = new DatabaseTestDirectory();
            var database = await LibraryDatabase.OpenAsync(files.PathFor());
            var service = new SteamLibrarySyncService(host, new FixedTimeProvider());

            var first = await service.SyncAsync(database, TestContext.Current.CancellationToken);
            Assert.Equal(2, first.Added);
            Assert.All(await ReadAll(database), game => Assert.True(game.Value.IsInstalled));

            // The drive is gone and the entry no longer declares which apps it held.
            File.WriteAllText(libraryFile, TwoLibraryDeclaration(root,
                Path.Combine(home, "mnt", "gone", "SteamLibrary"), declareExternalApps: false));

            var snapshot = host.Discover(TestContext.Current.CancellationToken);
            Assert.False(snapshot.CanReconcile);
            Assert.Contains(snapshot.Issues, issue => issue.Reason == SteamSyncReasons.LibraryAppsUnknown);

            var degraded = await service.SyncAsync(database, TestContext.Current.CancellationToken);

            Assert.Equal(0, degraded.Removed);
            Assert.Equal(0, degraded.Changes);
            Assert.False(degraded.Reconciled);
            var stored = await ReadAll(database);
            Assert.Equal(2, stored.Count);
            Assert.All(stored, game => Assert.True(game.Value.IsInstalled));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static SteamDiscoverySource ProductionHost(string home)
    {
        var environment = new MemoryEnvironment { Home = home };
        return new SteamDiscoverySource(environment,
            new LinuxPaths(environment, new LinuxFileSystem()),
            new SteamFileSystem(new LinuxBoundedFileReader()));
    }

    private static void CopyManifest(string libraryRoot, uint appId)
    {
        var target = Path.Combine(libraryRoot, "steamapps", $"appmanifest_{appId}.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(Corpus, "steamapps", $"appmanifest_{appId}.acf"), target);
    }

    // A root library plus a second library. A non-absolute path is a declaration the parser
    // rejects, and an omitted apps object leaves the second library's app set unknown.
    private static string TwoLibraryDeclaration(string root, string externalPath, bool declareExternalApps = true)
    {
        var lines = new List<string>
        {
            "\"libraryfolders\"",
            "{",
            "\t\"0\"",
            "\t{",
            $"\t\t\"path\"\t\t\"{root}\"",
            "\t}",
            "\t\"1\"",
            "\t{",
            $"\t\t\"path\"\t\t\"{externalPath}\"",
        };
        if (declareExternalApps)
        {
            lines.Add("\t\t\"apps\"");
            lines.Add("\t\t{");
            lines.Add("\t\t\t\"105600\"\t\t\"831365954\"");
            lines.Add("\t\t}");
        }

        lines.Add("\t}");
        lines.Add("}");
        return string.Join("\n", lines) + "\n";
    }

    private static void InstallManifest(string libraryRoot, uint appId, string installDir)
    {
        CopyManifest(libraryRoot, appId);
        Directory.CreateDirectory(Path.Combine(libraryRoot, "steamapps", "common", installDir));
    }

    private static Task<IReadOnlyList<StoredGame>> ReadAll(LibraryDatabase database) =>
        database.ReadAsync(session => session.Games.GetAllAsync());

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
