using Xunit;

namespace Playnite.Core.Tests.Serialization;

public sealed class SerializationSample
{
    public string? Name { get; set; }

    public int Count { get; set; }

    public double? Ratio { get; set; }

    public bool Enabled { get; set; }

    public List<string> Tags { get; set; } = [];

    public Dictionary<string, SerializationChild> Children { get; set; } = [];

    public SerializationChild? First { get; set; }

    public string? Omitted { get; set; }

    public object? Extra { get; set; }
}

public sealed class TomlSample
{
    public string? Name { get; set; }

    public int Count { get; set; }

    public double? Ratio { get; set; }

    public bool Enabled { get; set; }

    public List<string> Tags { get; set; } = [];

    public Dictionary<string, SerializationChild> Children { get; set; } = [];

    public SerializationChild? First { get; set; }

    public string? Omitted { get; set; }
}

public sealed class SerializationChild
{
    public string? Label { get; set; }

    public int[] Values { get; set; } = [];
}

public sealed class AttributedSample
{
    [Playnite.SDK.Data.DontSerialize]
    public int Hidden { get; set; } = 1;

    [Playnite.SDK.Data.SerializationPropertyName("renamed")]
    public int Renamed { get; set; } = 2;

    [Newtonsoft.Json.JsonIgnore]
    public int Ignored { get; set; } = 3;

    public int Plain { get; set; } = 4;

    public int Field = 5;
}

public sealed class TextSample
{
    public string? Value { get; set; }
}

internal static class SerializationSamples
{
    internal static SerializationSample Create()
    {
        return new SerializationSample
        {
            Name = "héllo \"q\" \\ 日本語\nline",
            Count = 7,
            Ratio = 0.5,
            Enabled = true,
            Tags = ["a", "ü"],
            Children = new Dictionary<string, SerializationChild>
            {
                ["c1"] = new SerializationChild { Label = "x", Values = [1, 2] },
            },
            First = new SerializationChild { Label = "y", Values = [] },
        };
    }

    internal static TomlSample CreateToml()
    {
        var sample = Create();
        return new TomlSample
        {
            Name = sample.Name,
            Count = sample.Count,
            Ratio = sample.Ratio,
            Enabled = sample.Enabled,
            Tags = sample.Tags,
            Children = sample.Children,
            First = sample.First,
            Omitted = "present",
        };
    }
}

internal static class SerializationText
{
    internal static void Equal(string expected, string actual)
    {
        Assert.Equal(expected.ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
    }
}
