using System.Diagnostics;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Playnite.Core.Database;
using Playnite.Core.Import;
using Xunit;

namespace Playnite.Core.Tests.Import;

public sealed class AssetCopyTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "playnite-assets-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(temporary, "source");
    private string Destination => Path.Combine(temporary, "library.sqlite");
    private string Store => Destination + ".assets";
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Playnite.slnx"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }
    private static string Bundle => Path.Combine(Root, "tests", "fixtures", "v10-assets");
    private static string Product(string folder, string name) => Path.Combine(Root, folder, name, "bin",
        AppContext.BaseDirectory.Contains("/Release/", StringComparison.Ordinal) ? "Release" : "Debug", "net11.0", name + ".dll");

    public AssetCopyTests()
    {
        Directory.CreateDirectory(Source);
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "tests", "fixtures", "v10", "library")))
            File.Copy(file, Path.Combine(Source, Path.GetFileName(file)));
        foreach (var file in Directory.GetFiles(Path.Combine(Bundle, "files"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Source, Path.GetRelativePath(Bundle, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Fact]
    public async Task V10_assets_are_copied_with_header_dimensions_and_missing_reference_is_counted_by_identity()
    {
        var before = Snapshot(Source);
        var report = await LibraryMigration.RunAsync(await Envelope(), Source, Destination);
        Assert.Equal(3, report.Assets.Count);
        Assert.Equal(1, report.MissingAssets); Assert.Equal(0, report.UnreadableAssets); Assert.Equal(0, report.EmptyAssets);
        var missing = Assert.Single(report.Assets.Where(a => a.Outcome == "missing"));
        Assert.Equal("10000000-0000-0000-0000-000000000001", missing.Reference.Identity);
        Assert.EndsWith("\\missing.png", missing.Reference.Path);
        var database = await LibraryDatabase.OpenAsync(Destination);
        var game = (await database.ReadAsync(s => s.Games.GetAsync(Guid.Parse(missing.Reference.Identity))))!;
        Assert.Equal("playnite-asset://missing/" + Uri.EscapeDataString(missing.Reference.Path), game.Value.BackgroundImage);
        foreach (var expected in JArray.Parse(File.ReadAllText(Path.Combine(Bundle, "assets.json"))))
        {
            var asset = Assert.Single(report.Assets.Where(a => a.Reference.Sha256 == (string?)expected["sha256"]));
            Assert.Equal("copied", asset.Outcome); Assert.Equal("decoded", asset.Dimensions);
            Assert.Equal((int)expected["width"]!, asset.Width); Assert.Equal((int)expected["height"]!, asset.Height);
            Assert.EndsWith("/" + asset.Reference.Sha256![..2] + "/" + asset.Reference.Sha256, asset.Destination);
            Assert.Equal(File.ReadAllBytes(Path.Combine(Bundle, (string)expected["path"]!)), File.ReadAllBytes(Path.Combine(temporary, asset.Destination!)));
            Assert.Contains(asset.Destination, new[] { game.Value.Icon, game.Value.CoverImage });
            Assert.Equal(Path.GetFileName((string)expected["path"]!), asset.Reference.Path.Split('\\')[^1]);
        }
        Assert.Equal(before, Snapshot(Source));
        Assert.Contains("MissingAssets", File.ReadAllText(Destination + ".import-report.json"));
    }

    [Fact]
    public async Task Identical_bytes_with_different_names_share_one_blob_and_second_copy_is_a_true_noop()
    {
        var bytes = File.ReadAllBytes(Directory.GetFiles(Path.Combine(Bundle, "files"), "discord.png", SearchOption.AllDirectories).Single());
        var first = Write("first.png", bytes); var second = Write("another-name.bin", bytes);
        var copied = await AssetCopy.RunAsync(Source, Store, [first, second]);
        Assert.Equal(["copied", "reused"], copied.Select(a => a.Outcome));
        Assert.Equal(copied[0].Destination, copied[1].Destination);
        var blob = Assert.Single(Directory.GetFiles(Store, "*", SearchOption.AllDirectories));
        var stamp = File.GetLastWriteTimeUtc(blob); var before = Snapshot(temporary);
        Assert.All(await AssetCopy.RunAsync(Source, Store, [first, second]), a => Assert.Equal("reused", a.Outcome));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(blob)); Assert.Equal(before, Snapshot(temporary));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_source_or_stored_blob_refuses_hash_mismatch_without_overwrite(bool changeSource)
    {
        var reference = Write("asset", "original"u8.ToArray());
        await AssetCopy.RunAsync(Source, Store, [reference]);
        var blob = Directory.GetFiles(Store, "*", SearchOption.AllDirectories).Single();
        File.WriteAllText(changeSource ? Path.Combine(Source, "files", "asset") : blob, "tampered");
        var before = Snapshot(temporary);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => AssetCopy.RunAsync(Source, Store, [reference]));
        Assert.Contains("hash mismatch", error.Message); Assert.Equal(before, Snapshot(temporary));
    }

    [Fact]
    public async Task Missing_empty_and_unreadable_are_distinct_and_reader_does_not_abort_on_locked_asset()
    {
        var empty = Write("empty", []);
        var locked = Write("locked", "not empty"u8.ToArray());
        using var lease = new FileStream(Path.Combine(Source, "files", "locked"), System.IO.FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var envelope = JObject.Parse(await Envelope());
        Assert.Equal("unreadable", (string?)((JArray)envelope["files"]!).Single(f => (string?)f["path"] == "files/locked")["status"]);
        var game = ((JArray)envelope["records"]!).First(r => (string?)r["collection"] == "Game");
        game["data"]!["Icon"] = "empty"; game["data"]!["CoverImage"] = "locked"; game["data"]!["BackgroundImage"] = "missing";
        var report = await LibraryMigration.RunAsync(envelope.ToString(), Source, Destination);
        Assert.Equal(1, report.MissingAssets); Assert.Equal(1, report.EmptyAssets); Assert.Equal(1, report.UnreadableAssets);
        Assert.Equal(["empty", "unreadable", "missing"], report.Assets.Select(a => a.Outcome));
        var database = await LibraryDatabase.OpenAsync(Destination);
        var saved = await database.ReadAsync(s => s.Games.GetAsync(Guid.Parse((string)game["data"]!["_id"]!)));
        Assert.Equal("playnite-asset://empty/empty", saved!.Value.Icon);
        Assert.Equal("playnite-asset://unreadable/locked", saved.Value.CoverImage);
        Assert.Equal("playnite-asset://missing/missing", saved.Value.BackgroundImage);
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task Dry_run_inspects_headers_but_writes_no_assets_or_database()
    {
        var before = Snapshot(temporary);
        var report = await LibraryMigration.RunAsync(await Envelope(), Source, Destination, dryRun: true);
        Assert.Equal(2, report.Assets.Count(a => a.Outcome == "planned")); Assert.Equal(1, report.MissingAssets);
        Assert.All(report.Assets.Where(a => a.Outcome == "planned"), a => Assert.Equal("decoded", a.Dimensions));
        Assert.Equal(before, Snapshot(temporary)); Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task Reader_unreadable_snapshot_stays_reported_after_source_lock_is_released()
    {
        Write("locked", "readable after release"u8.ToArray());
        JObject envelope;
        using (var lease = new FileStream(Path.Combine(Source, "files", "locked"), System.IO.FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            envelope = JObject.Parse(await Envelope());
        Assert.Equal("readable after release", File.ReadAllText(Path.Combine(Source, "files", "locked")));
        var captured = ((JArray)envelope["files"]!).Single(f => (string?)f["path"] == "files/locked");
        Assert.Equal("unreadable", (string?)captured["status"]);
        Assert.Equal(JTokenType.Null, captured["sha256"]!.Type);
        var game = ((JArray)envelope["records"]!).First(r => (string?)r["collection"] == "Game");
        game["data"]!["Icon"] = "locked";
        var report = await LibraryMigration.RunAsync(envelope.ToString(), Source, Destination);
        Assert.Equal(1, report.UnreadableAssets); Assert.Equal(1, report.MissingAssets); Assert.Equal(0, report.EmptyAssets);
        var unreadable = Assert.Single(report.Assets.Where(a => a.Outcome == "unreadable"));
        Assert.Equal("unreadable", unreadable.Reference.Status); Assert.Equal("locked", unreadable.Reference.Path);
        Assert.Equal("10000000-0000-0000-0000-000000000001", unreadable.Reference.Identity);
        Assert.Null(unreadable.Reference.Sha256); Assert.Null(unreadable.Destination);
        var database = await LibraryDatabase.OpenAsync(Destination);
        var saved = await database.ReadAsync(s => s.Games.GetAsync(Guid.Parse(unreadable.Reference.Identity)));
        Assert.Equal("playnite-asset://unreadable/locked", saved!.Value.Icon);
    }

    [Fact]
    public async Task SIGKILL_mid_copy_leaves_only_a_temporary_file_then_retry_publishes_verified_blob()
    {
        var reference = Write("large.png", new byte[1024 * 1024]);
        var envelope = JObject.Parse(await Envelope());
        var game = ((JArray)envelope["records"]!).First(r => (string?)r["collection"] == "Game");
        game["data"]!["Icon"] = "large.png";
        var plan = Path.Combine(temporary, "intermediate.json"); File.WriteAllText(plan, envelope.ToString());
        var start = Start(Path.Combine(AppContext.BaseDirectory, "AssetCopy.CrashHost.dll"));
        foreach (var arg in new[] { plan, Source, Destination }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            Assert.Equal("AssetPartialCopy", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            var partial = Assert.Single(Directory.GetFiles(Store, "*", SearchOption.AllDirectories));
            Assert.EndsWith(".tmp", partial);
            Assert.InRange(new FileInfo(partial).Length, 1, reference.Size!.Value - 1);
            Assert.False(File.Exists(Destination));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.NotEqual(0, process.ExitCode);
        var report = await LibraryMigration.RunAsync(envelope.ToString(), Source, Destination);
        Assert.Contains(report.Assets, a => a.Reference.Path == "large.png" && a.Outcome == "copied");
        var blob = Path.Combine(Store, "sha256", reference.Sha256![..2], reference.Sha256);
        Assert.Equal(reference.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(blob))));
    }

    [Theory]
    [InlineData("AssetPartialCopy")]
    [InlineData("before")]
    public async Task Cancellation_never_publishes_partial_blob_and_disposes_source(string state)
    {
        var reference = Write("large", new byte[256 * 1024]);
        using var cancel = new CancellationTokenSource();
        if (state == "before") cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AssetCopy.RunAsync(Source, Store, [reference],
            checkpoint: _ => cancel.Cancel(), cancellationToken: cancel.Token));
        if (Directory.Exists(Store)) Assert.Empty(Directory.GetFiles(Store, "*", SearchOption.AllDirectories));
        using var exclusive = new FileStream(Path.Combine(Source, "files", "large"), System.IO.FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("parent/../escape")]
    [InlineData("a//b")]
    public async Task Traversal_references_are_refused(string path)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => AssetCopy.RunAsync(Source, Store, [new("games.db", "Game", "identity", "Icon", path, null, null)]));
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task Source_and_destination_links_and_source_containment_are_refused()
    {
        var reference = Write("asset", [1, 2, 3]);
        File.CreateSymbolicLink(Path.Combine(Source, "files", "link"), Path.Combine(Source, "files", "asset"));
        await Assert.ThrowsAsync<IOException>(() => AssetCopy.RunAsync(Source, Store, [reference with { Path = "link" }]));
        await Assert.ThrowsAsync<ArgumentException>(() => AssetCopy.RunAsync(Source, Path.Combine(Source, "output"), [reference]));
        Directory.CreateSymbolicLink(Store, Source);
        await Assert.ThrowsAsync<IOException>(() => AssetCopy.RunAsync(Source, Store, [reference]));
    }

    [Theory]
    [InlineData("gif87", 3, 5, "decoded")]
    [InlineData("gif89", 3, 5, "decoded")]
    [InlineData("jpeg", 7, 9, "decoded")]
    [InlineData("jpeg-progressive", 7, 9, "decoded")]
    [InlineData("jpeg-app", 7, 9, "decoded")]
    [InlineData("jpeg-truncated", null, null, "invalid-header")]
    [InlineData("png-truncated", null, null, "invalid-header")]
    [InlineData("png-zero", null, null, "invalid-header")]
    [InlineData("png-overflow", null, null, "invalid-header")]
    [InlineData("unsupported", null, null, "unsupported")]
    public async Task Header_only_decoding_is_bounded_and_unsupported_or_invalid_data_is_still_copied(string format, int? width, int? height, string status)
    {
        byte[] bytes = format switch
        {
            "gif87" => [.. "GIF87a"u8, 3, 0, 5, 0], "gif89" => [.. "GIF89a"u8, 3, 0, 5, 0],
            "jpeg" => [255, 216, 255, 192, 0, 8, 8, 0, 9, 0, 7, 1],
            "jpeg-progressive" => [255, 216, 255, 194, 0, 8, 8, 0, 9, 0, 7, 1],
            "jpeg-app" => [255, 216, 255, 224, 0, 4, 0, 0, 255, 192, 0, 8, 8, 0, 9, 0, 7, 1],
            "jpeg-truncated" => [255, 216, 255, 192],
            "png-truncated" => [137, 80, 78, 71, 13, 10, 26, 10],
            "png-zero" => [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 0, 0, 0, 0, 1],
            "png-overflow" => [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 128, 0, 0, 0, 0, 0, 0, 1],
            _ => "not an image"u8.ToArray()
        };
        var result = Assert.Single(await AssetCopy.RunAsync(Source, Store, [Write("image", bytes)]));
        Assert.Equal("copied", result.Outcome); Assert.Equal(status, result.Dimensions);
        Assert.Equal(width, result.Width); Assert.Equal(height, result.Height);
    }

    private AssetReference Write(string path, byte[] bytes)
    {
        var full = Path.Combine(Source, "files", path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, bytes);
        return new("games.db", "Game", "10000000-0000-0000-0000-000000000001", "Icon", path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength);
    }

    [Fact]
    public async Task Platform_image_fields_are_remapped_and_original_documents_are_retained()
    {
        var envelope = JObject.Parse(await Envelope());
        var platform = ((JArray)envelope["records"]!).Single(r => (string?)r["collection"] == "Platform");
        var reference = JArray.Parse(File.ReadAllText(Path.Combine(Bundle, "references.json")))[0]!["path"]!;
        foreach (var field in new[] { "Icon", "Cover", "Background" }) platform["data"]![field] = reference.DeepClone();
        var report = await LibraryMigration.RunAsync(envelope.ToString(), Source, Destination);
        Assert.Equal(6, report.Assets.Count);
        Assert.Equal(3, report.Assets.Count(a => a.Reference.Collection == "Platform" && a.Outcome == "reused"));
        var database = await LibraryDatabase.OpenAsync(Destination);
        var saved = await database.ReadAsync(s => s.Metadata<Playnite.Core.Models.Platform>().GetAsync(Guid.Parse((string)platform["data"]!["_id"]!)));
        Assert.NotNull(saved); Assert.Equal(saved.Value.Icon, saved.Value.Cover); Assert.Equal(saved.Value.Icon, saved.Value.Background);
        Assert.True(JToken.DeepEquals(platform["data"], JObject.Parse(saved.LegacyJson)));
    }

    [Theory]
    [InlineData("https://example.invalid/cover.png")]
    [InlineData("C:\\old-library\\cover.png")]
    [InlineData("/external/cover.png")]
    public async Task External_references_are_explicitly_reported_without_reading_or_downloading(string path)
    {
        var result = Assert.Single(await AssetCopy.RunAsync(Source, Store, [new("games.db", "Game", "identity", "Icon", path, null, null)]));
        Assert.Equal("external", result.Outcome); Assert.Null(result.Destination); Assert.False(Directory.Exists(Store));
        var envelope = JObject.Parse(await Envelope());
        var game = ((JArray)envelope["records"]!).First(r => (string?)r["collection"] == "Game");
        game["data"]!["Icon"] = path;
        await LibraryMigration.RunAsync(envelope.ToString(), Source, Destination);
        var database = await LibraryDatabase.OpenAsync(Destination);
        var saved = await database.ReadAsync(s => s.Games.GetAsync(Guid.Parse((string)game["data"]!["_id"]!)));
        Assert.Equal("playnite-asset://external/" + Uri.EscapeDataString(path), saved!.Value.Icon);
        Assert.Equal(path, (string?)JObject.Parse(saved.LegacyJson)["Icon"]);
    }

    [Fact]
    public async Task Unmanifested_emptying_and_reader_then_deleted_assets_are_refused_explicitly()
    {
        var reference = Write("asset", [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => AssetCopy.RunAsync(Source, Store, [reference with { Sha256 = null, Size = null }]));
        File.WriteAllBytes(Path.Combine(Source, "files", "asset"), []);
        await Assert.ThrowsAsync<InvalidDataException>(() => AssetCopy.RunAsync(Source, Store, [reference]));
        Assert.False(Directory.Exists(Store));
        File.WriteAllBytes(Path.Combine(Source, "files", "asset"), [1, 2, 3]);
        var envelope = JObject.Parse(await Envelope());
        var game = ((JArray)envelope["records"]!).First(r => (string?)r["collection"] == "Game");
        game["data"]!["Icon"] = "asset";
        using (var lease = new FileStream(Path.Combine(Source, "files", "asset"), System.IO.FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = await Assert.ThrowsAsync<InvalidDataException>(() => LibraryMigration.RunAsync(envelope.ToString(), Source, Destination));
            Assert.Contains("cannot reproduce captured bytes", locked.Message);
            Assert.Contains("games.db/Game/10000000-0000-0000-0000-000000000001/asset", locked.Message);
            Assert.False(File.Exists(Destination));
        }
        File.Delete(Path.Combine(Source, "files", "asset"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LibraryMigration.RunAsync(envelope.ToString(), Source, Destination));
        Assert.Contains("disappeared after reader snapshot", error.Message);
        Assert.Contains("games.db/Game/10000000-0000-0000-0000-000000000001/asset", error.Message);
        Assert.False(File.Exists(Destination)); Assert.False(Directory.Exists(Store));
        Directory.CreateDirectory(Path.Combine(Source, "files", "asset"));
        var replaced = await Assert.ThrowsAsync<InvalidDataException>(() => LibraryMigration.RunAsync(envelope.ToString(), Source, Destination));
        Assert.Contains("cannot reproduce captured bytes", replaced.Message);
        Assert.Contains("games.db/Game/10000000-0000-0000-0000-000000000001/asset", replaced.Message);
        Assert.True(Directory.Exists(Path.Combine(Source, "files", "asset")));
        Assert.False(File.Exists(Destination)); Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task Seeded_malformed_header_fuzz_never_loads_pixels_or_changes_copied_bytes()
    {
        var random = new Random(5005);
        for (var index = 0; index < 256; index++)
        {
            var bytes = new byte[random.Next(1, 512)]; random.NextBytes(bytes);
            if (index % 2 == 0 && bytes.Length >= 2) { bytes[0] = 255; bytes[1] = 216; }
            var reference = Write("fuzz", bytes);
            var result = Assert.Single(await AssetCopy.RunAsync(Source, Store, [reference]));
            Assert.Contains(result.Dimensions, new[] { "unsupported", "invalid-header" });
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(temporary, result.Destination!)));
        }
    }

    [Fact]
    public void Fixture_bytes_dimensions_and_provenance_match_both_frozen_v10_originals()
    {
        var assets = JArray.Parse(File.ReadAllText(Path.Combine(Bundle, "assets.json")));
        Assert.Equal(2, assets.Count);
        foreach (var asset in assets)
        {
            var bytes = File.ReadAllBytes(Path.Combine(Bundle, (string)asset["path"]!));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Root, (string)asset["oracle"]!)));
            Assert.Equal((string?)asset["sha256"], Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.Equal((long?)asset["size"], bytes.LongLength);
            Assert.Equal((uint)asset["width"]!, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)));
            Assert.Equal((uint)asset["height"]!, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)));
        }
        var missing = JArray.Parse(File.ReadAllText(Path.Combine(Bundle, "references.json")))[2]!;
        Assert.False(File.Exists(Path.Combine(Bundle, "files", ((string)missing["path"]!).Replace('\\', '/'))));
    }

    private async Task<string> Envelope()
    {
        var start = Start(Product("src", "Playnite.LegacyImport")); start.ArgumentList.Add(Source);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Assert.True(process.ExitCode == 0, await error);
        var envelope = JObject.Parse(await output);
        foreach (var reference in JArray.Parse(File.ReadAllText(Path.Combine(Bundle, "references.json"))))
        {
            var row = ((JArray)envelope["records"]!).Single(r => (string?)r["file"] == (string?)reference["file"] &&
                (string?)r["collection"] == (string?)reference["collection"] && (string?)r["data"]?["_id"] == (string?)reference["identity"]);
            row["data"]![(string)reference["field"]!] = reference["path"]!.DeepClone();
        }
        return envelope.ToString();
    }

    private static ProcessStartInfo Start(string dll)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(dll); return start;
    }
    private static string[] Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(p => Path.GetRelativePath(directory, p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    public void Dispose() => Directory.Delete(temporary, recursive: true);
}
