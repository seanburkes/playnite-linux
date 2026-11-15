using Xunit;
using Newtonsoft.Json.Linq;
using Playnite.Core.Serialization;

namespace Playnite.Core.Tests.Serialization;

public sealed class JsonSerializationTests
{
    private const string ExpectedCompact =
        """{"Name":"héllo \"q\" \\ 日本語\nline","Count":7,"Ratio":0.5,"Enabled":true,"Tags":["a","ü"],"Children":{"c1":{"Label":"x","Values":[1,2]}},"First":{"Label":"y","Values":[]}}""";

    private const string ExpectedFormatted =
        """
        {
          "Name": "héllo \"q\" \\ 日本語\nline",
          "Count": 7,
          "Ratio": 0.5,
          "Enabled": true,
          "Tags": [
            "a",
            "ü"
          ],
          "Children": {
            "c1": {
              "Label": "x",
              "Values": [
                1,
                2
              ]
            }
          },
          "First": {
            "Label": "y",
            "Values": []
          }
        }
        """;

    [Fact]
    public void Json_round_trips_nested_objects_arrays_nulls_and_unicode()
    {
        var sample = SerializationSamples.Create();

        var compact = JsonSerialization.ToJson(sample);
        Assert.Equal(ExpectedCompact, compact);

        var formatted = JsonSerialization.ToJson(sample, formatted: true);
        SerializationText.Equal(ExpectedFormatted, formatted);

        var back = JsonSerialization.FromJson<SerializationSample>(formatted)!;
        Assert.Equal(sample.Name, back.Name);
        Assert.Equal(sample.Count, back.Count);
        Assert.Equal(sample.Ratio, back.Ratio);
        Assert.Equal(sample.Enabled, back.Enabled);
        Assert.Equal(sample.Tags, back.Tags);
        Assert.Equal([1, 2], back.Children["c1"].Values);
        Assert.Equal("y", back.First!.Label);
        Assert.Empty(back.First.Values);
        Assert.Null(back.Omitted);
        Assert.Equal(compact, JsonSerialization.ToJson(back));
    }

    [Fact]
    public void Json_honours_DontSerialize_SerializationPropertyName_and_JsonIgnore()
    {
        var json = JsonSerialization.ToJson(new AttributedSample());
        Assert.Equal("""{"renamed":2,"Plain":4,"Field":5}""", json);

        var back = JsonSerialization.FromJson<AttributedSample>("""{"Hidden":9,"renamed":20,"Plain":40,"Field":50}""")!;
        Assert.Equal(1, back.Hidden);
        Assert.Equal(20, back.Renamed);
        Assert.Equal(40, back.Plain);
        Assert.Equal(50, back.Field);
    }

    [Fact]
    public void Json_rejects_malformed_documents()
    {
        var exception = Assert.Throws<SerializationException>(
            () => JsonSerialization.FromJson<SerializationSample>("""{"Count":7,"""));
        Assert.Equal("JSON", exception.Format);
        Assert.Contains("Unexpected end of content", exception.Message);

        exception = Assert.Throws<SerializationException>(
            () => JsonSerialization.FromJson<SerializationSample>("{\"Count\":}"));
        Assert.StartsWith("JSON: ", exception.Message);
    }

    [Fact]
    public void Json_rejects_empty_input_but_accepts_the_null_literal()
    {
        Assert.Throws<SerializationException>(() => JsonSerialization.FromJson<SerializationSample>(string.Empty));
        Assert.Throws<SerializationException>(() => JsonSerialization.FromJson<SerializationSample>("   "));
        Assert.Null(JsonSerialization.FromJson<SerializationSample>("null"));
    }

    [Fact]
    public void Json_accepts_an_empty_object_as_all_defaults()
    {
        var sample = JsonSerialization.FromJson<SerializationSample>("{}")!;
        Assert.Null(sample.Name);
        Assert.Equal(0, sample.Count);
        Assert.Equal([], sample.Tags);
        Assert.Empty(sample.Children);
    }

    [Fact]
    public void Json_rejects_duplicate_property_names_at_any_depth()
    {
        var top = Assert.Throws<SerializationException>(
            () => JsonSerialization.FromJson<SerializationSample>("""{"Count":1,"Count":2}"""));
        Assert.Contains("already exists", top.Message);

        var nested = Assert.Throws<SerializationException>(
            () => JsonSerialization.FromJson<SerializationSample>(
                """{"Children":{"c1":{"Label":"x","Label":"z","Values":[]}}}"""));
        Assert.Contains("already exists", nested.Message);
        Assert.Contains("Children.c1.Label", nested.Message);
    }

    [Fact]
    public void Json_rejects_content_after_the_root_value()
    {
        var exception = Assert.Throws<SerializationException>(
            () => JsonSerialization.FromJson<SerializationSample>("""{"Count":1} {}"""));
        Assert.Contains("Additional text", exception.Message);
    }

    [Fact]
    public void Json_rejects_a_fractional_number_for_an_integer_member()
    {
        var exception = Assert.Throws<SerializationException>(
            () => JsonSerialization.FromJson<SerializationSample>("""{"Count":1.5}"""));
        Assert.Contains("$.Count", exception.Message);
        Assert.Contains("would be truncated", exception.Message);
    }

    [Fact]
    public void Json_rejects_an_integer_that_a_double_cannot_represent_exactly()
    {
        var exception = Assert.Throws<SerializationException>(
            () => JsonSerialization.FromJson<SerializationSample>("""{"Ratio":9007199254740993}"""));
        Assert.Contains("$.Ratio", exception.Message);
        Assert.Contains("cannot be represented exactly", exception.Message);

        var exact = JsonSerialization.FromJson<SerializationSample>("""{"Ratio":9007199254740992}""")!;
        Assert.Equal(9007199254740992d, exact.Ratio);
    }

    [Fact]
    public void Json_rejects_null_for_a_value_type_member()
    {
        var exception = Assert.Throws<SerializationException>(
            () => JsonSerialization.FromJson<SerializationSample>("""{"Count":null}"""));
        Assert.Contains("$.Count", exception.Message);
        Assert.Contains("null cannot be stored", exception.Message);
    }

    [Fact]
    public void Json_keeps_the_v10_textual_number_binding()
    {
        // v10 read settings through Newtonsoft's binder, which parses "123" into an int
        // without changing the value. The helper keeps that behaviour and pins it.
        var sample = JsonSerialization.FromJson<SerializationSample>("""{"Count":"123"}""")!;
        Assert.Equal(123, sample.Count);
    }

    [Fact]
    public void Json_preserves_an_iso_date_inside_a_string_member_verbatim()
    {
        const string timestamp = "2026-11-15T01:02:03+01:00";
        var sample = JsonSerialization.FromJson<SerializationSample>(
            $$"""{"Name":"{{timestamp}}"}""")!;
        Assert.Equal(timestamp, sample.Name);
    }

    [Fact]
    public void Json_does_not_resolve_a_type_name()
    {
        var back = JsonSerialization.FromJson<SerializationSample>(
            """{"Count":1,"Extra":{"$type":"System.IO.FileInfo","Length":5}}""")!;
        Assert.Equal(1, back.Count);
        var extra = Assert.IsType<JObject>(back.Extra);
        Assert.Equal(5, (int)extra["Length"]!);
    }

    [Fact]
    public void Json_ignores_unknown_properties()
    {
        var sample = JsonSerialization.FromJson<SerializationSample>("""{"Count":2,"Nope":true}""")!;
        Assert.Equal(2, sample.Count);
    }

    [Fact]
    public void Json_ignores_a_utf8_byte_order_mark()
    {
        var sample = JsonSerialization.FromJson<SerializationSample>("\uFEFF{\"Count\":3}")!;
        Assert.Equal(3, sample.Count);
    }
}
