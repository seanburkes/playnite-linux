using Xunit;
using Playnite.Core.Serialization;

namespace Playnite.Core.Tests.Serialization;

public sealed class IniParserTests
{
    private const string SampleDocument =
        """
        ; Engine configuration, the shape v10 read from game settings
        [RegionDoesRequirePermission]
        GameDev=true
        Path=C:\Games

        [Second]
        GameDev=false

        [RegionDoesRequirePermission]
        GameDev=duplicate-section
        """;

    [Fact]
    public void Ini_parses_sections_and_verbatim_values()
    {
        var data = IniParser.Parse(SampleDocument);

        Assert.Equal(3, data.Sections.Count);
        Assert.Equal("RegionDoesRequirePermission", data.Sections[0].Name);
        Assert.Equal("true", data["RegionDoesRequirePermission"]!["GameDev"]);
        Assert.Equal("false", data["Second"]!["GameDev"]);
        Assert.Equal("duplicate-section", data.Sections[2]["GameDev"]);
        Assert.Equal("C:\\Games", data.Sections[0]["Path"]);
        Assert.Null(data["missing"]);
        Assert.Null(data["Second"]!["missing"]);
    }

    [Fact]
    public void Ini_returns_the_first_match_for_duplicate_names()
    {
        var data = IniParser.Parse("[s]\nkey=first\nkey=second\n");

        Assert.Equal(2, data["s"]!.Items.Count);
        Assert.Equal("first", data["s"]!["key"]);
    }

    [Fact]
    public void Ini_splits_an_entry_at_the_last_equals_sign_like_v10()
    {
        // v10 matched entries with the greedy pattern ^(.+)=(.*)$, so everything before the
        // last '=' became the name. Pinned as the reader's contract.
        var data = IniParser.Parse("[s]\nPath=a=b\n");

        Assert.Equal("b", data["s"]!["Path=a"]);
        Assert.Null(data["s"]!["Path"]);
    }

    [Fact]
    public void Ini_treats_only_semicolon_lines_as_comments()
    {
        var data = IniParser.Parse("[s]\n   ; indented comment\nkey=value ; trailing text\n");

        Assert.Single(data.Sections);
        Assert.Equal("value ; trailing text", data["s"]!["key"]);
    }

    [Fact]
    public void Ini_accepts_an_empty_or_comment_only_document()
    {
        Assert.Empty(IniParser.Parse(string.Empty).Sections);
        Assert.Empty(IniParser.Parse("; a\n; b\n\n").Sections);
    }

    [Fact]
    public void Ini_ignores_a_utf8_byte_order_mark()
    {
        var data = IniParser.Parse("\uFEFF[s]\nkey=value\n");
        Assert.Equal("value", data["s"]!["key"]);
    }

    [Fact]
    public void Ini_parses_the_line_overload_identically()
    {
        var fromText = IniParser.Parse(SampleDocument);
        var fromLines = IniParser.Parse(SampleDocument.Split('\n'));

        Assert.Equal(
            fromText.Sections.Select(section => section.Name),
            fromLines.Sections.Select(section => section.Name));
        Assert.Equal(
            fromText.Sections.SelectMany(section => section.Items.Select(item => (item.Name, item.Value))),
            fromLines.Sections.SelectMany(section => section.Items.Select(item => (item.Name, item.Value))));
    }

    [Fact]
    public void Ini_rejects_a_line_it_cannot_classify()
    {
        var exception = Assert.Throws<SerializationException>(
            () => IniParser.Parse("[s]\nthis is not an entry\n"));
        Assert.Equal("INI", exception.Format);
        Assert.Contains("line 2", exception.Message);
        Assert.Contains("neither a comment", exception.Message);
    }

    [Fact]
    public void Ini_rejects_an_entry_before_the_first_section()
    {
        var exception = Assert.Throws<SerializationException>(
            () => IniParser.Parse("key=value\n"));
        Assert.Contains("line 1", exception.Message);
        Assert.Contains("before the first section header", exception.Message);
    }

    [Fact]
    public void Ini_rejects_an_empty_or_unterminated_section_header()
    {
        Assert.Throws<SerializationException>(() => IniParser.Parse("[]\n"));
        Assert.Throws<SerializationException>(() => IniParser.Parse("[section\n"));
    }

    [Fact]
    public void Ini_rejects_an_entry_without_a_name()
    {
        var exception = Assert.Throws<SerializationException>(
            () => IniParser.Parse("[s]\n=value\n"));
        Assert.Contains("no entry name", exception.Message);
    }

    [Fact]
    public void Ini_preserves_a_trailing_carriage_return_only_line_ending()
    {
        var data = IniParser.Parse("[s]\rkey=value\r");
        Assert.Equal("value", data["s"]!["key"]);
    }
}
