using Xunit;
using System.Text;
using Playnite.Core.Serialization;

namespace Playnite.Core.Tests.Serialization;

public sealed class SerializationLimitTests
{
    public static TheoryData<string> Formats => new() { "json", "yaml", "toml", "ini" };

    [Fact]
    public void Default_limit_is_eight_mebibytes()
    {
        Assert.Equal(8 * 1024 * 1024, SerializationLimits.DefaultMaxDocumentBytes);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Each_format_accepts_a_document_exactly_at_the_limit(string format)
    {
        var document = MinimalDocument(format);
        var limit = Encoding.UTF8.GetByteCount(document);

        Assert.NotNull(Parse(format, document, limit));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Each_format_rejects_a_document_one_byte_over_the_limit(string format)
    {
        var document = MinimalDocument(format);
        var limit = Encoding.UTF8.GetByteCount(document) - 1;

        var exception = Assert.Throws<SerializationException>(() => Parse(format, document, limit));
        Assert.Contains($"exceeds the {limit}-byte limit", exception.Message);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Each_format_counts_utf8_bytes_not_characters(string format)
    {
        var document = format switch
        {
            "json" => """{"Name":"日本語"}""",
            "yaml" => "Name: 日本語",
            "toml" => "Name = \"日本語\"",
            "ini" => "[s]\nName=日本語",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        var characters = document.Length;
        var exception = Assert.Throws<SerializationException>(() => Parse(format, document, characters));
        Assert.Contains("exceeds", exception.Message);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Each_format_rejects_a_null_document(string format)
    {
        Assert.Throws<ArgumentNullException>(() => Parse(format, null!, SerializationLimits.DefaultMaxDocumentBytes));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Each_format_rejects_a_non_positive_limit(string format)
    {
        var document = MinimalDocument(format);
        Assert.Throws<ArgumentOutOfRangeException>(() => Parse(format, document, 0));
    }

    [Fact]
    public void Ini_line_input_counts_the_separators()
    {
        IReadOnlyList<string> lines = ["[s]", "Name=x"];
        var bytes = Encoding.UTF8.GetByteCount("[s]") + 1 + Encoding.UTF8.GetByteCount("Name=x") + 1;

        Assert.NotNull(IniParser.Parse(lines, bytes));
        Assert.Throws<SerializationException>(() => IniParser.Parse(lines, bytes - 1));
    }

    private static string MinimalDocument(string format)
    {
        return format switch
        {
            "json" => """{"Name":"value"}""",
            "yaml" => "Name: value",
            "toml" => "Name = \"value\"",
            "ini" => "[s]\nName=value",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    private static object? Parse(string format, string document, int maxBytes)
    {
        return format switch
        {
            "json" => JsonSerialization.FromJson<SerializationSample>(document, maxBytes),
            "yaml" => YamlSerialization.FromYaml<SerializationSample>(document, maxBytes),
            "toml" => TomlSerialization.FromToml<SerializationSample>(document, maxBytes),
            "ini" => IniParser.Parse(document, maxBytes),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }
}
