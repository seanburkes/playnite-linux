using LiteDB;
using Playnite.Common;
using Playnite.Database;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

internal static class Program
{
    private static readonly Guid Steam = new Guid("cb91dfc9-b977-43bf-8e70-55f46e410fab");
    private static readonly DateTime Stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Activity = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static readonly string[] Files = ("database.json platforms.db emulators.db genres.db companies.db " +
        "tags.db categories.db ageratings.db series.db regions.db sources.db features.db tools.db games.db " +
        "scanners.db filterpresets.db importexclusions.db completionstatuses.db").Split(' ');

    private static Guid GameId(int n) => new Guid("10000000-0000-0000-0000-" + n.ToString("D12"));
    private static Guid MetadataId(int n) => new Guid("20000000-0000-0000-0000-" + n.ToString("D12"));

    private static Game[] Games()
    {
        var games = new[]
        {
            new Game("Synthetic Steam Installed") { GameId = "220", IsInstalled = true,
                InstallDirectory = @"C:\Synthetic Steam\steamapps\common\Synthetic 220",
                Favorite = true, Playtime = 3600, PlayCount = 2, LastActivity = Activity },
            new Game("Synthetic Steam Uninstalled") { GameId = "570" },
            new Game("Synthetic Steam Unicode — 雪") { GameId = "400", IsInstalled = true,
                InstallDirectory = @"C:\Synthetic Steam\steamapps\common\Synthetic 雪",
                Hidden = true, ReleaseDate = new ReleaseDate(2019, 7) },
            new Game("Synthetic Custom Game") { GameId = "synthetic-custom" }
        };
        for (var i = 0; i < games.Length; i++)
        {
            games[i].Id = GameId(i + 1);
            games[i].PluginId = i < 3 ? Steam : Guid.Empty;
            games[i].Added = Stamp;
            games[i].Modified = Stamp;
        }
        games[0].PlatformIds = new List<Guid> { MetadataId(1) };
        games[0].SourceId = MetadataId(2);
        games[0].GenreIds = new List<Guid> { MetadataId(3) };
        games[0].TagIds = new List<Guid> { MetadataId(4) };
        games[0].CompletionStatusId = MetadataId(5);
        return games;
    }

    private static int Main(string[] args)
    {
        try
        {
            Require(Environment.OSVersion.Platform == PlatformID.Win32NT, "Requires Windows .NET Framework, not Wine/Linux evidence.");
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            Require((args.Length == 2 && (args[0] == "generate" || args[0] == "verify")) ||
                (args.Length == 3 && args[0] == "compare"),
                "Usage: V10LibraryFixture generate <new-bundle-dir> | verify <bundle-dir> | compare <bundle-dir> <bundle-dir>");
            if (args[0] == "generate") Generate(Path.GetFullPath(args[1]));
            else if (args[0] == "compare") CompareLogical(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            else Verify(Path.GetFullPath(args[1]));
            Console.WriteLine("PASS " + args[0]);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Generate(string bundle)
    {
        Require(!Directory.Exists(bundle) && !File.Exists(bundle), "Output must not exist.");
        var library = Path.Combine(bundle, "library");
        Directory.CreateDirectory(Path.Combine(library, "files"));
        File.WriteAllText(Path.Combine(library, "database.json"), Serialization.ToJson(
            new DatabaseSettings { Version = GameDatabase.NewFormatVersion }), new UTF8Encoding(false));
        // Existing version marker prevents v10 from inserting random default metadata.
        using (var database = new GameDatabase(library)) database.OpenDatabase();
        Write(library, "platforms", new Platform { Id = MetadataId(1), Name = "Synthetic Windows" });
        Write(library, "sources", new GameSource { Id = MetadataId(2), Name = "Steam" });
        Write(library, "genres", new Genre { Id = MetadataId(3), Name = "Synthetic Genre" });
        Write(library, "tags", new Tag { Id = MetadataId(4), Name = "Synthetic Tag" });
        Write(library, "completionstatuses", new CompletionStatus { Id = MetadataId(5), Name = "Synthetic Not Played" });
        // Base ItemCollection.Add preserves our dates; GamesCollection.Add overwrites them.
        using (var games = new ItemCollection<Game>(Path.Combine(library, "games"), GameDatabase.GetCollectionMapper()))
            games.Add(Games());
        File.WriteAllText(Path.Combine(bundle, "SHA256SUMS"), Hashes(library), new UTF8Encoding(false));
        Verify(bundle);
    }

    private static void Write<T>(string library, string file, T item) where T : DatabaseObject
    {
        using (var collection = new ItemCollection<T>(Path.Combine(library, file), GameDatabase.GetCollectionMapper()))
            collection.Add(item);
    }

    private static string Hashes(string library)
    {
        var actual = Directory.GetFiles(library, "*", SearchOption.AllDirectories)
            .Select(p => p.Substring(library.Length + 1).Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(Files.OrderBy(p => p, StringComparer.Ordinal)), "Unexpected library file set.");
        var result = new StringBuilder();
        using (var sha = SHA256.Create())
            foreach (var name in actual)
            {
                using (var stream = File.OpenRead(Path.Combine(library, name)))
                    result.Append(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant());
                result.Append("  library/").Append(name).Append('\n');
            }
        return result.ToString();
    }

    private static void Verify(string bundle)
    {
        var library = Path.Combine(bundle, "library");
        var before = Hashes(library);
        Require(before == File.ReadAllText(Path.Combine(bundle, "SHA256SUMS")), "Checksum mismatch.");
        Require(Serialization.FromJson<DatabaseSettings>(File.ReadAllText(Path.Combine(library, "database.json"))).Version ==
            GameDatabase.NewFormatVersion, "Database version mismatch.");
        // Verify the serialized layout before v10 gets any opportunity to repair a copy.
        using (var raw = new LiteDatabase(new ConnectionString
        {
            Filename = Path.Combine(library, "games.db"), Mode = LiteDB.FileMode.ReadOnly
        }, GameDatabase.GetCollectionMapper()))
        {
            var typed = raw.GetCollection<Game>();
            Require(raw.GetCollectionNames().SequenceEqual(new[] { typed.Name }), "Unexpected games collection names.");
            CheckGames(typed.FindAll().ToArray());
            var doc = raw.GetCollection(typed.Name).FindById(GameId(3));
            Require(doc["ReleaseDate"].AsString == new ReleaseDate(2019, 7).Serialize(), "ReleaseDate mapper mismatch.");
            Require(!doc.ContainsKey("Genres") && !doc.ContainsKey("InstallationStatus"), "Computed properties persisted.");
        }
        var scratch = Path.Combine(Path.GetTempPath(), "playnite-v10-readback-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(scratch, "files"));
            foreach (var file in Files) File.Copy(Path.Combine(library, file), Path.Combine(scratch, file));
            using (var database = new GameDatabase(scratch))
            {
                database.OpenDatabase();
                CheckGames(database.Games.ToArray());
                CheckMetadata(database.Platforms, 1, "Synthetic Windows");
                CheckMetadata(database.Sources, 2, "Steam");
                CheckMetadata(database.Genres, 3, "Synthetic Genre");
                CheckMetadata(database.Tags, 4, "Synthetic Tag");
                CheckMetadata(database.CompletionStatuses, 5, "Synthetic Not Played");
                Require(database.Emulators.Count == 0 && database.Companies.Count == 0 && database.Categories.Count == 0 &&
                    database.AgeRatings.Count == 0 && database.Series.Count == 0 && database.Regions.Count == 0 &&
                    database.Features.Count == 0 && database.SoftwareApps.Count == 0 && database.GameScanners.Count == 0 &&
                    database.FilterPresets.Count == 0 && database.ImportExclusions.Count == 0, "Unexpected metadata rows.");
            }
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            Require(before == Hashes(library), "Source bytes changed during read-back.");
        }
    }

    private static void CheckMetadata<T>(IEnumerable<T> items, int id, string name) where T : DatabaseObject
    {
        var rows = items.ToArray();
        Require(rows.Length == 1 && rows[0].Id == MetadataId(id) && rows[0].Name == name, "Metadata mismatch: " + name);
    }

    private static void CompareLogical(string first, string second)
    {
        Verify(first);
        Verify(second);
        foreach (var file in Files.Where(f => f.EndsWith(".db", StringComparison.Ordinal)))
        {
            using (var left = OpenReadOnly(Path.Combine(first, "library", file)))
            using (var right = OpenReadOnly(Path.Combine(second, "library", file)))
            {
                var names = left.GetCollectionNames().OrderBy(n => n, StringComparer.Ordinal).ToArray();
                Require(names.SequenceEqual(right.GetCollectionNames().OrderBy(n => n, StringComparer.Ordinal)),
                    "Logical collection mismatch: " + file);
                foreach (var name in names)
                {
                    // Compare complete persisted documents, not randomized index pages.
                    // These strings remain in memory; this is not a library export format.
                    var a = left.GetCollection(name).FindAll().Select(d => LiteDB.JsonSerializer.Serialize(d))
                        .OrderBy(d => d, StringComparer.Ordinal);
                    var b = right.GetCollection(name).FindAll().Select(d => LiteDB.JsonSerializer.Serialize(d))
                        .OrderBy(d => d, StringComparer.Ordinal);
                    Require(a.SequenceEqual(b), "Logical document mismatch: " + file + "/" + name);
                }
            }
        }
    }

    private static LiteDatabase OpenReadOnly(string path) => new LiteDatabase(new ConnectionString
    {
        Filename = path, Mode = LiteDB.FileMode.ReadOnly, UtcDate = true
    }, GameDatabase.GetCollectionMapper());

    private static void CheckGames(Game[] actual)
    {
        Require(actual.Length == 4, "Expected exactly four games.");
        foreach (var expected in Games())
        {
            var row = actual.Single(g => g.Id == expected.Id);
            Require(row.Name == expected.Name && row.GameId == expected.GameId && row.PluginId == expected.PluginId &&
                row.IsInstalled == expected.IsInstalled && row.InstallDirectory == expected.InstallDirectory &&
                row.Favorite == expected.Favorite && row.Hidden == expected.Hidden && row.Playtime == expected.Playtime &&
                row.PlayCount == expected.PlayCount && row.Added?.ToUniversalTime() == Stamp && row.Modified?.ToUniversalTime() == Stamp &&
                row.LastActivity?.ToUniversalTime() == expected.LastActivity && row.ReleaseDate == expected.ReleaseDate &&
                row.SourceId == expected.SourceId && row.CompletionStatusId == expected.CompletionStatusId &&
                Same(row.PlatformIds, expected.PlatformIds) && Same(row.GenreIds, expected.GenreIds) && Same(row.TagIds, expected.TagIds) &&
                row.Icon == null && row.CoverImage == null && row.BackgroundImage == null &&
                !row.IsRunning && !row.IsLaunching && !row.IsInstalling && !row.IsUninstalling,
                "Game mismatch: " + expected.Id);
        }
    }

    private static bool Same(List<Guid> actual, List<Guid> expected) =>
        actual == null ? expected == null : expected != null && actual.SequenceEqual(expected);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
