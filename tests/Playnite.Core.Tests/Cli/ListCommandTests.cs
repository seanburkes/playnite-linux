using Playnite.Cli;
using Playnite.Core.Models;
using Playnite.Core.Tests.Database;
using Xunit;

namespace Playnite.Core.Tests.Cli;

public sealed class ListCommandTests
{
    [Fact]
    public async Task List_writes_a_page_record_and_game_records_in_name_order()
    {
        using var directory = new DatabaseTestDirectory();
        var alpha = CliQueryFixture.NewGame("Alpha");
        alpha.IsInstalled = true;
        alpha.Hidden = true;
        alpha.Favorite = true;
        alpha.Playtime = 90;
        alpha.PlayCount = 2;
        alpha.ReleaseDate = new ReleaseDate(2019);
        var path = await CliQueryFixture.CreateLibraryAsync(directory,
            CliQueryFixture.NewGame("Gamma"), alpha, CliQueryFixture.NewGame("Beta"));

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await ListCommand.ExecuteAsync(["--library", path], output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var records = CliQueryFixture.ReadRecords(output.ToString());
        Assert.Equal(4, records.Length);
        Assert.Equal("page", records[0].GetProperty("kind").GetString());
        Assert.Equal(3, records[0].GetProperty("totalCount").GetInt32());
        Assert.Equal(0, records[0].GetProperty("offset").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, records[0].GetProperty("limit").ValueKind);
        Assert.Equal(3, records[0].GetProperty("returned").GetInt32());
        Assert.Equal(["Alpha", "Beta", "Gamma"],
            records[1..].Select(record => record.GetProperty("name").GetString()));

        var first = records[1];
        Assert.Equal("game", first.GetProperty("kind").GetString());
        Assert.Equal(alpha.Id, first.GetProperty("id").GetGuid());
        Assert.Equal("alpha", first.GetProperty("gameId").GetString());
        Assert.True(first.GetProperty("installed").GetBoolean());
        Assert.True(first.GetProperty("hidden").GetBoolean());
        Assert.True(first.GetProperty("favorite").GetBoolean());
        Assert.Equal(90UL, first.GetProperty("playtimeSeconds").GetUInt64());
        Assert.Equal(2UL, first.GetProperty("playCount").GetUInt64());
        Assert.Equal(2019, first.GetProperty("releaseYear").GetInt32());
    }

    [Fact]
    public async Task List_applies_the_bounded_sort_and_paging_arguments()
    {
        using var directory = new DatabaseTestDirectory();
        var alpha = CliQueryFixture.NewGame("Alpha");
        alpha.Playtime = 10;
        var beta = CliQueryFixture.NewGame("Beta");
        beta.Playtime = 20;
        var gamma = CliQueryFixture.NewGame("Gamma");
        gamma.Playtime = 30;
        var path = await CliQueryFixture.CreateLibraryAsync(directory, alpha, beta, gamma);

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await ListCommand.ExecuteAsync(
            ["--library", path, "--sort", "playtime", "--descending", "--offset", "1", "--limit", "1"],
            output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var records = CliQueryFixture.ReadRecords(output.ToString());
        Assert.Equal(2, records.Length);
        Assert.Equal(3, records[0].GetProperty("totalCount").GetInt32());
        Assert.Equal(1, records[0].GetProperty("offset").GetInt32());
        Assert.Equal(1, records[0].GetProperty("limit").GetInt32());
        Assert.Equal(1, records[0].GetProperty("returned").GetInt32());
        Assert.Equal("Beta", records[1].GetProperty("name").GetString());
    }

    [Theory]
    [MemberData(nameof(BadArguments))]
    public async Task List_rejects_invalid_arguments(string[] args, string expected)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await ListCommand.ExecuteAsync(args, output, error);

        Assert.Equal(2, exit);
        Assert.Equal("", output.ToString());
        Assert.Contains(expected, error.ToString());
    }

    public static TheoryData<string[], string> BadArguments => new()
    {
        { ["--library", "/tmp/library.sqlite", "--limit", "0"], "--limit must be an integer from 1 to 10000" },
        { ["--library", "/tmp/library.sqlite", "--limit", "10001"], "--limit must be an integer from 1 to 10000" },
        { ["--library", "/tmp/library.sqlite", "--limit", "many"], "--limit must be an integer from 1 to 10000" },
        { ["--library", "/tmp/library.sqlite", "--offset", "-1"], "--offset must be an integer from 0 to 1000000" },
        { ["--library", "/tmp/library.sqlite", "--offset", "1000001"], "--offset must be an integer from 0 to 1000000" },
        { ["--library", "/tmp/library.sqlite", "--sort", "bogus"], "Unknown sort key 'bogus'" },
        { ["--library", "/tmp/library.sqlite", "--limit", "1", "--limit", "2"], "--limit was specified more than once" },
        { ["--library", "/tmp/library.sqlite", "--offset", "1", "--offset", "2"], "--offset was specified more than once" },
        { ["--library", "/tmp/library.sqlite", "--sort", "name", "--sort", "playtime"], "--sort was specified more than once" },
        { ["--library", "/tmp/library.sqlite", "--descending", "--descending"], "--descending was specified more than once" },
        { ["--library", "/tmp/library.sqlite", "--library", "/tmp/other.sqlite"], "--library was specified more than once" },
        { ["--library", "/tmp/library.sqlite", "extra"], "Usage: Playnite.Cli list" },
        { [], "Usage: Playnite.Cli list" }
    };

    [Fact]
    public async Task List_reports_a_missing_library_as_a_service_error()
    {
        using var directory = new DatabaseTestDirectory();
        var path = directory.PathFor("absent.sqlite");

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await ListCommand.ExecuteAsync(["--library", path], output, error);

        Assert.Equal(4, exit);
        Assert.Equal("", output.ToString());
        Assert.Contains("was not found", error.ToString());
        Assert.False(File.Exists(path));
    }
}
