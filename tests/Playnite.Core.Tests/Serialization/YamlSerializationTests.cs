using Xunit;
using Playnite.Core.Serialization;

namespace Playnite.Core.Tests.Serialization;

public sealed class YamlSerializationTests
{
    private static readonly string ExpectedYaml =
        """
        Name: >-
          héllo "q" \ 日本語

          line
        Count: 7
        Ratio: 0.5
        Enabled: true
        Tags:
        - a
        - ü
        Children:
          c1:
            Label: x
            Values:
            - 1
            - 2
        First:
          Label: y
          Values: []
        """ + "\nOmitted: \nExtra: \n";

    [Fact]
    public void Yaml_round_trips_nested_objects_arrays_nulls_and_unicode()
    {
        var sample = SerializationSamples.Create();

        var yaml = YamlSerialization.ToYaml(sample);
        SerializationText.Equal(ExpectedYaml, yaml);

        var back = YamlSerialization.FromYaml<SerializationSample>(yaml)!;
        Assert.Equal(sample.Name, back.Name);
        Assert.Equal(sample.Count, back.Count);
        Assert.Equal(sample.Ratio, back.Ratio);
        Assert.Equal(sample.Tags, back.Tags);
        Assert.Equal([1, 2], back.Children["c1"].Values);
        Assert.Equal("y", back.First!.Label);
        Assert.Null(back.Omitted);
        Assert.Equal(yaml, YamlSerialization.ToYaml(back));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(" lead")]
    [InlineData("trail ")]
    [InlineData("line\nbreak")]
    [InlineData("trail\n")]
    [InlineData("a\\b")]
    [InlineData("q\"q")]
    [InlineData("#hash")]
    [InlineData("a: b")]
    [InlineData("héllo ünïcode 日本語")]
    public void Yaml_round_trips_escaping_sensitive_strings(string value)
    {
        var yaml = YamlSerialization.ToYaml(new TextSample { Value = value });
        var back = YamlSerialization.FromYaml<TextSample>(yaml)!;
        Assert.Equal(value, back.Value);
    }

    [Fact]
    public void Yaml_quotes_strings_that_would_bind_as_another_type()
    {
        var yaml = YamlSerialization.ToYaml(new TextSample { Value = "123" });
        Assert.Contains("Value: \"123\"", yaml);

        yaml = YamlSerialization.ToYaml(new TextSample { Value = "true" });
        Assert.Contains("Value: \"true\"", yaml);
    }

    [Fact]
    public void Yaml_emits_no_anchors_or_aliases_for_a_shared_object()
    {
        var shared = new SerializationChild { Label = "shared", Values = [1] };
        var sample = new SerializationSample
        {
            Children = new Dictionary<string, SerializationChild> { ["a"] = shared },
            First = shared,
        };

        var yaml = YamlSerialization.ToYaml(sample);
        Assert.DoesNotContain("&", yaml);
        Assert.DoesNotContain("*", yaml);

        var back = YamlSerialization.FromYaml<SerializationSample>(yaml)!;
        Assert.Equal("shared", back.First!.Label);
        Assert.Equal("shared", back.Children["a"].Label);
    }

    [Fact]
    public void Yaml_rejects_malformed_documents()
    {
        var exception = Assert.Throws<SerializationException>(
            () => YamlSerialization.FromYaml<SerializationSample>("Name: [1, 2"));
        Assert.Equal("YAML", exception.Format);
        Assert.Contains("flow sequence", exception.Message);
    }

    [Fact]
    public void Yaml_rejects_duplicate_keys_at_any_depth()
    {
        var top = Assert.Throws<SerializationException>(
            () => YamlSerialization.FromYaml<SerializationSample>("Count: 1\nCount: 2"));
        Assert.Contains("Duplicate key Count", top.Message);

        var nested = Assert.Throws<SerializationException>(
            () => YamlSerialization.FromYaml<SerializationSample>(
                "First:\n  Label: a\n  Label: b\n"));
        Assert.Contains("Duplicate key Label", nested.Message);
    }

    [Fact]
    public void Yaml_treats_an_empty_or_comment_only_document_as_null()
    {
        Assert.Null(YamlSerialization.FromYaml<SerializationSample>(string.Empty));
        Assert.Null(YamlSerialization.FromYaml<SerializationSample>("# only a comment\n"));
    }

    [Fact]
    public void Yaml_rejects_multiple_documents()
    {
        var exception = Assert.Throws<SerializationException>(
            () => YamlSerialization.FromYaml<SerializationSample>("Count: 1\n---\nCount: 2"));
        Assert.Contains("StreamEnd", exception.Message);
    }

    [Fact]
    public void Yaml_rejects_an_unresolved_tag_instead_of_resolving_a_type()
    {
        var exception = Assert.Throws<SerializationException>(
            () => YamlSerialization.FromYaml<SerializationSample>("First: !System.IO.FileInfo {}"));
        Assert.Contains("unresolved tag", exception.Message);
    }

    [Fact]
    public void Yaml_rejects_null_for_a_value_type_member()
    {
        var exception = Assert.Throws<SerializationException>(
            () => YamlSerialization.FromYaml<SerializationSample>("Count: null"));
        Assert.Contains("$.Count", exception.Message);
        Assert.Contains("null cannot be stored", exception.Message);
    }

    [Fact]
    public void Yaml_rejects_null_inside_a_sequence_of_value_types()
    {
        var exception = Assert.Throws<SerializationException>(
            () => YamlSerialization.FromYaml<SerializationSample>("First:\n  Values:\n  - 1\n  - ~\n"));
        Assert.Contains("$.First.Values[1]", exception.Message);
        Assert.Contains("null cannot be stored", exception.Message);
    }

    [Fact]
    public void Yaml_keeps_the_plain_scalar_binding_for_text_members()
    {
        // An unquoted YAML scalar is text first; v10 manifests rely on numbers binding to
        // string members without a type name being invented.
        var sample = YamlSerialization.FromYaml<SerializationSample>("Name: 123")!;
        Assert.Equal("123", sample.Name);
    }

    [Fact]
    public void Yaml_ignores_unknown_properties()
    {
        var sample = YamlSerialization.FromYaml<SerializationSample>("Count: 4\nNope: true\n")!;
        Assert.Equal(4, sample.Count);
    }

    [Fact]
    public void Yaml_rejects_nesting_beyond_the_depth_limit()
    {
        var deepSequence = new string('[', 200) + "x" + new string(']', 200);
        var sequence = Assert.Throws<SerializationException>(
            () => YamlSerialization.FromYaml<List<object>>(deepSequence));
        Assert.Contains("nesting exceeds the maximum depth of 128", sequence.Message);

        var deepMapping = string.Concat(Enumerable.Range(0, 200).Select(level => new string(' ', level) + "a:\n"));
        var mapping = Assert.Throws<SerializationException>(
            () => YamlSerialization.FromYaml<Dictionary<string, object>>(deepMapping));
        Assert.Contains("nesting exceeds the maximum depth of 128", mapping.Message);
    }
}
