using System.Text;
using System.Text.Json;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class ArchiveTests
{
    [Fact]
    public void V10_fixture_listing_matches_the_committed_golden()
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(ArchiveTestData.Golden("v10-full.entries.json")));
        var expected = golden.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        Assert.Equal(expected, Archive.GetArchiveFiles(ArchiveTestData.Fixture("v10-full.zip")));
        Assert.Equal(expected, Archive.GetArchiveFiles(ArchiveTestData.Fixture("v10-full.zip")));
    }

    [Fact]
    public async Task Entry_stream_returns_exact_member_bytes_and_missing_members_return_null()
    {
        await using var stream = await Archive.GetEntryStreamAsync(
            ArchiveTestData.Fixture("v10-full.zip"), "library/games.db");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("synthetic-v10-games-db\n", await reader.ReadToEndAsync());
        Assert.Null(await Archive.GetEntryStreamAsync(
            ArchiveTestData.Fixture("v10-full.zip"), "library/missing.db"));
    }

    [Fact]
    public void Tar_archives_use_the_same_listing_contract()
    {
        Assert.Equal(["config.json", "library/games.db"],
            Archive.GetArchiveFiles(ArchiveTestData.Fixture("v10-minimal.tar")));
    }

    [Fact]
    public void Directory_members_are_validated_but_not_listed()
    {
        Assert.Equal(["config.json", "library/games.db"],
            Archive.GetArchiveFiles(ArchiveTestData.Fixture("directory.zip")));
    }

    [Fact]
    public void Windows_separators_are_normalized_to_portable_member_names()
    {
        Assert.Equal(["library/games.db"],
            Archive.GetArchiveFiles(ArchiveTestData.Fixture("backslash.zip")));
    }

    [Fact]
    public void Traversal_fixture_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            Archive.GetArchiveFiles(ArchiveTestData.Fixture("traversal.zip")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("library/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/windows.txt")]
    [InlineData("a/./dot.txt")]
    [InlineData("a//empty.txt")]
    [InlineData("a/\u0001control.txt")]
    public async Task Unsafe_member_names_are_rejected_by_listing_and_by_read(string name)
    {
        var directory = ArchiveTestData.NewDirectory();
        try
        {
            var archive = Path.Combine(directory, "unsafe.zip");
            ArchiveTestData.CreateZip(archive, (name, "escape"));
            Assert.Throws<InvalidDataException>(() => Archive.GetArchiveFiles(archive));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Archive.GetEntryStreamAsync(archive, "safe.txt"));
        }
        finally
        {
            ArchiveTestData.Delete(directory);
        }
    }

    [Fact]
    public void Duplicate_and_linked_members_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            Archive.GetArchiveFiles(ArchiveTestData.Fixture("duplicate.zip")));
        Assert.Throws<InvalidDataException>(() =>
            Archive.GetArchiveFiles(ArchiveTestData.Fixture("linked.tar")));
    }

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(100, 0, 100)]
    [InlineData(100, 100, 0)]
    public void Non_positive_limits_are_rejected(int entries, long entryBytes, long totalBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Archive.GetArchiveFiles(
            ArchiveTestData.Fixture("v10-full.zip"), new ArchiveLimits(entries, entryBytes, totalBytes)));
    }

    [Fact]
    public void Entry_count_limit_rejects_the_whole_archive()
    {
        Assert.Throws<InvalidDataException>(() => Archive.GetArchiveFiles(
            ArchiveTestData.Fixture("v10-full.zip"), new ArchiveLimits(EntryCount: 3)));
    }

    [Fact]
    public void Per_entry_and_total_size_limits_reject_the_whole_archive()
    {
        Assert.Throws<InvalidDataException>(() => Archive.GetArchiveFiles(
            ArchiveTestData.Fixture("v10-full.zip"), new ArchiveLimits(EntryBytes: 10)));
        Assert.Throws<InvalidDataException>(() => Archive.GetArchiveFiles(
            ArchiveTestData.Fixture("v10-full.zip"), new ArchiveLimits(TotalBytes: 30)));
    }

    [Fact]
    public async Task Entry_reads_honor_the_same_limits_and_cancellation()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Archive.GetEntryStreamAsync(
            ArchiveTestData.Fixture("v10-full.zip"), "config.json", new ArchiveLimits(EntryBytes: 10)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Archive.GetEntryStreamAsync(
            ArchiveTestData.Fixture("v10-full.zip"), "library/games.db", cancellationToken: cancelled.Token));
    }
}
