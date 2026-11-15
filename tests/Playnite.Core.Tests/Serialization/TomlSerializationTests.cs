using Xunit;
using Playnite.Core.Serialization;

namespace Playnite.Core.Tests.Serialization;

public sealed class TomlSerializationTests
{
    private static readonly string ExpectedToml =
        """
        Count = 7
        Enabled = true
        Name = "héllo \"q\" \\ 日本語\nline"
        Omitted = "present"
        Ratio = 0.5
        Tags = ["a", "ü"]
        [Children]
        [Children.c1]
        Label = "x"
        Values = [1, 2]
        [First]
        Label = "y"
        Values = []
        """ + "\n";

    [Fact]
    public void Toml_round_trips_tables_arrays_and_unicode_with_deterministic_member_order()
    {
        var sample = SerializationSamples.CreateToml();

        var toml = TomlSerialization.ToToml(sample);
        SerializationText.Equal(ExpectedToml, toml);
        Assert.Equal(toml, TomlSerialization.ToToml(sample));

        var back = TomlSerialization.FromToml<TomlSample>(toml)!;
        Assert.Equal(sample.Name, back.Name);
        Assert.Equal(sample.Count, back.Count);
        Assert.Equal(sample.Ratio, back.Ratio);
        Assert.Equal(sample.Enabled, back.Enabled);
        Assert.Equal(sample.Tags, back.Tags);
        Assert.Equal("present", back.Omitted);
        Assert.Equal([1, 2], back.Children["c1"].Values);
        Assert.Equal("y", back.First!.Label);
    }

    [Fact]
    public void Toml_rejects_a_null_member_instead_of_dropping_it()
    {
        var withNull = SerializationSamples.CreateToml();
        withNull.Name = null;
        var exception = Assert.Throws<SerializationException>(
            () => TomlSerialization.ToToml(withNull));
        Assert.Equal("TOML", exception.Format);
        Assert.Contains("null is not representable in TOML", exception.Message);

        var nested = Assert.Throws<SerializationException>(
            () => TomlSerialization.ToToml(new TomlNested
            {
                Name = "root",
                Count = 1,
                Child = new TomlNested { Name = null, Count = 2 },
            }));
        Assert.Contains("null is not representable in TOML", nested.Message);
    }

    [Fact]
    public void Toml_rejects_a_null_root()
    {
        var exception = Assert.Throws<SerializationException>(
            () => TomlSerialization.ToToml<SerializationSample>(null!));
        Assert.Contains("null is not representable in TOML", exception.Message);
    }

    [Fact]
    public void Toml_rejects_duplicate_keys()
    {
        var exception = Assert.Throws<SerializationException>(
            () => TomlSerialization.FromToml<SerializationSample>("Count = 1\nCount = 2"));
        Assert.Contains("Duplicate key 'Count'", exception.Message);
    }

    [Fact]
    public void Toml_rejects_malformed_documents()
    {
        var exception = Assert.Throws<SerializationException>(
            () => TomlSerialization.FromToml<SerializationSample>("Count = ["));
        Assert.Equal("TOML", exception.Format);
        Assert.Contains("Expected Integer token", exception.Message);
    }

    [Fact]
    public void Toml_treats_an_empty_document_as_a_default_instance()
    {
        // TOML has no null document; an empty table binds to default values. Pinned so a
        // later change cannot turn this into either an exception or a null quietly.
        var sample = TomlSerialization.FromToml<SerializationSample>(string.Empty);
        Assert.NotNull(sample);
        Assert.Null(sample.Name);
        Assert.Equal(0, sample.Count);
        Assert.Empty(sample.Tags);
    }

    [Fact]
    public void Toml_rejects_a_string_for_an_integer_member()
    {
        var exception = Assert.Throws<SerializationException>(
            () => TomlSerialization.FromToml<SerializationSample>("Count = \"1\""));
        Assert.Contains("Expected Integer token but was String", exception.Message);
    }

    [Fact]
    public void Toml_rejects_a_fractional_number_for_an_integer_member()
    {
        var exception = Assert.Throws<SerializationException>(
            () => TomlSerialization.FromToml<SerializationSample>("Count = 1.5"));
        Assert.Contains("Expected Integer token but was Float", exception.Message);
    }

    [Fact]
    public void Toml_rejects_a_non_table_document()
    {
        var exception = Assert.Throws<SerializationException>(
            () => TomlSerialization.FromToml<SerializationSample>("\"just text\""));
        Assert.StartsWith("TOML: ", exception.Message);
    }

    [Fact]
    public void Toml_rejects_nesting_beyond_the_depth_limit()
    {
        var header = "[Next" + string.Concat(Enumerable.Repeat(".Next", 200)) + "]\nValue = 1\n";
        var exception = Assert.Throws<SerializationException>(
            () => TomlSerialization.FromToml<TomlNode>(header));
        Assert.Contains("maximum depth of 128", exception.Message);
    }

    [Fact]
    public void Toml_ignores_unknown_keys()
    {
        var sample = TomlSerialization.FromToml<SerializationSample>("Count = 4\nNope = true\n")!;
        Assert.Equal(4, sample.Count);
    }

    [Fact]
    public void Toml_ignores_a_utf8_byte_order_mark()
    {
        var sample = TomlSerialization.FromToml<SerializationSample>("\uFEFFCount = 5")!;
        Assert.Equal(5, sample.Count);
    }

    public sealed class TomlNested
    {
        public string? Name { get; set; }

        public int Count { get; set; }

        public TomlNested? Child { get; set; }
    }

    public sealed class TomlNode
    {
        public TomlNode? Next { get; set; }

        public int Value { get; set; }
    }
}
