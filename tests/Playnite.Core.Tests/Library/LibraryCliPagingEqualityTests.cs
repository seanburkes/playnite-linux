using System.Text.Json;
using Playnite.Cli;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Tests.Database;
using Xunit;

namespace Playnite.Core.Tests.Library;

/// <summary>
/// Pins the CLI first page on a fixed dataset to the pre-wiring output. The names are distinct
/// ASCII titles, so the old current-culture order and the adopted NOCASE order agree and the
/// first page is an equality check rather than a re-baseline.
/// </summary>
public sealed class LibraryCliPagingEqualityTests
{
    [Fact]
    public async Task List_first_page_matches_the_pre_wiring_output()
    {
        using var directory = new DatabaseTestDirectory();
        var path = await CreateLibraryAsync(directory);
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await ListCommand.ExecuteAsync(
            ["--library", path, "--offset", "0", "--limit", "2"], output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var records = ReadRecords(output.ToString());
        Assert.Equal(3, records.Length);
        Assert.Equal(5, records[0].GetProperty("totalCount").GetInt32());
        Assert.Equal(2, records[0].GetProperty("returned").GetInt32());
        Assert.Equal(["Alpha", "Beta"], Names(records));
        Assert.Equal([Id(1), Id(2)], records[1..].Select(record => record.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Unpaged_list_matches_the_pre_wiring_output()
    {
        using var directory = new DatabaseTestDirectory();
        var path = await CreateLibraryAsync(directory);
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await ListCommand.ExecuteAsync(["--library", path], output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var records = ReadRecords(output.ToString());
        Assert.Equal(6, records.Length);
        Assert.Equal(5, records[0].GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, records[0].GetProperty("limit").ValueKind);
        Assert.Equal(["Alpha", "Beta", "Delta", "Epsilon", "Gamma"], Names(records));
    }

    [Fact]
    public async Task Search_first_page_matches_the_pre_wiring_output()
    {
        using var directory = new DatabaseTestDirectory();
        var path = await CreateLibraryAsync(directory);
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await SearchCommand.ExecuteAsync(
            ["ta", "--library", path, "--offset", "0", "--limit", "1"], output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var records = ReadRecords(output.ToString());
        Assert.Equal(2, records.Length);
        Assert.Equal(2, records[0].GetProperty("totalCount").GetInt32());
        Assert.Equal(["Beta"], Names(records));
        Assert.Equal(Id(2), records[1].GetProperty("id").GetGuid());
    }

    private static async Task<string> CreateLibraryAsync(DatabaseTestDirectory directory)
    {
        var path = directory.PathFor();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await LibraryDatabase.OpenAsync(path, cancellationToken);
        await database.WriteAsync(async session =>
        {
            var names = new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" };
            for (var index = 0; index < names.Length; index++)
            {
                var value = index + 1;
                await session.Games.AddAsync(new StoredGame(
                    new Game(Id(value), value.ToString(System.Globalization.CultureInfo.InvariantCulture), names[index])));
            }

            return 0;
        }, cancellationToken);
        return path;
    }

    private static Guid Id(int id) => Guid.Parse($"10000000-0000-0000-0000-{id:x12}");

    private static JsonElement[] ReadRecords(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static string?[] Names(JsonElement[] records)
        => records[1..].Select(record => record.GetProperty("name").GetString()).ToArray();
}
