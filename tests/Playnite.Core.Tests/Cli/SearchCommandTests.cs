using Playnite.Cli;
using Playnite.Core.Tests.Database;
using Xunit;

namespace Playnite.Core.Tests.Cli;

public sealed class SearchCommandTests
{
    [Fact]
    public async Task Search_matches_display_names_and_writes_the_page()
    {
        using var directory = new DatabaseTestDirectory();
        var path = await CliQueryFixture.CreateLibraryAsync(directory,
            CliQueryFixture.NewGame("Synthetic Steam Installed"),
            CliQueryFixture.NewGame("Synthetic Steam Unicode"),
            CliQueryFixture.NewGame("Unrelated"));

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SearchCommand.ExecuteAsync(["synthetic steam", "--library", path], output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var records = CliQueryFixture.ReadRecords(output.ToString());
        Assert.Equal(3, records.Length);
        Assert.Equal(2, records[0].GetProperty("totalCount").GetInt32());
        Assert.Equal(2, records[0].GetProperty("returned").GetInt32());
        Assert.Equal(["Synthetic Steam Installed", "Synthetic Steam Unicode"],
            records[1..].Select(record => record.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Search_with_a_blank_term_lists_the_library()
    {
        using var directory = new DatabaseTestDirectory();
        var path = await CliQueryFixture.CreateLibraryAsync(directory,
            CliQueryFixture.NewGame("Alpha"), CliQueryFixture.NewGame("Beta"));

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SearchCommand.ExecuteAsync(["", "--library", path], output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var records = CliQueryFixture.ReadRecords(output.ToString());
        Assert.Equal(3, records.Length);
        Assert.Equal(2, records[0].GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Search_applies_paging_and_reports_the_unpaged_total()
    {
        using var directory = new DatabaseTestDirectory();
        var path = await CliQueryFixture.CreateLibraryAsync(directory,
            CliQueryFixture.NewGame("Match One"), CliQueryFixture.NewGame("Match Two"), CliQueryFixture.NewGame("Match Three"));

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SearchCommand.ExecuteAsync(
            ["Match", "--library", path, "--offset", "1", "--limit", "1"], output, error);

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        var records = CliQueryFixture.ReadRecords(output.ToString());
        Assert.Equal(2, records.Length);
        Assert.Equal(3, records[0].GetProperty("totalCount").GetInt32());
        Assert.Equal(1, records[0].GetProperty("offset").GetInt32());
        Assert.Equal(1, records[0].GetProperty("limit").GetInt32());
        Assert.Equal("Match Three", records[1].GetProperty("name").GetString());
    }

    [Theory]
    [MemberData(nameof(BadArguments))]
    public async Task Search_rejects_invalid_arguments(string[] args, string expected)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SearchCommand.ExecuteAsync(args, output, error);

        Assert.Equal(2, exit);
        Assert.Equal("", output.ToString());
        Assert.Contains(expected, error.ToString());
    }

    public static TheoryData<string[], string> BadArguments => new()
    {
        { [], "Usage: Playnite.Cli search" },
        { ["--library", "/tmp/library.sqlite"], "Usage: Playnite.Cli search" },
        { ["term", "extra", "--library", "/tmp/library.sqlite"], "Usage: Playnite.Cli search" },
        { ["term", "--library", "/tmp/library.sqlite", "--limit", "0"], "--limit must be an integer from 1 to 10000" },
        { ["term", "--library", "/tmp/library.sqlite", "--offset", "1000001"], "--offset must be an integer from 0 to 1000000" },
        { ["term", "--library", "/tmp/library.sqlite", "--limit", "1", "--limit", "2"], "--limit was specified more than once" },
        { ["term", "--library", "/tmp/library.sqlite", "--offset", "1", "--offset", "2"], "--offset was specified more than once" },
        { ["term", "--library", "/tmp/library.sqlite", "--sort", "name", "--sort", "playtime"], "--sort was specified more than once" },
        { ["term", "--library", "/tmp/library.sqlite", "--descending", "--descending"], "--descending was specified more than once" },
        { ["term", "--library", "/tmp/library.sqlite", "--library", "/tmp/other.sqlite"], "--library was specified more than once" }
    };

    [Fact]
    public async Task Search_reports_a_missing_library_as_a_service_error()
    {
        using var directory = new DatabaseTestDirectory();
        var path = directory.PathFor("absent.sqlite");

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SearchCommand.ExecuteAsync(["term", "--library", path], output, error);

        Assert.Equal(4, exit);
        Assert.Equal("", output.ToString());
        Assert.Contains("was not found", error.ToString());
    }
}
