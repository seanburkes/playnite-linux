using System.Text;
using Playnite.Core.Serialization;

namespace Playnite.Fuzz;

/// <summary>
/// The four serialisation parser entry points the fuzz target exercises.
/// </summary>
public static class FuzzParsers
{
    /// <summary>Formats in a stable order so the corpus and probe stream are reproducible.</summary>
    public static readonly string[] Formats = ["json", "yaml", "toml", "ini"];

    /// <summary>Parses a document with the entry point for <paramref name="format"/>.</summary>
    /// <param name="format">One of <see cref="Formats"/>.</param>
    /// <param name="document">Document text.</param>
    /// <param name="maxBytes">Maximum accepted UTF-8 size.</param>
    /// <returns>The bound value, or null when the format's reader returns null.</returns>
    public static object? Parse(string format, string document, int maxBytes)
    {
        return format switch
        {
            "json" => JsonSerialization.FromJson<FuzzSample>(document, maxBytes),
            "yaml" => YamlSerialization.FromYaml<FuzzSample>(document, maxBytes),
            "toml" => TomlSerialization.FromToml<FuzzSample>(document, maxBytes),
            "ini" => IniParser.Parse(document, maxBytes),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown fuzz format."),
        };
    }

    /// <summary>A document the parser must reject with a serialisation exception.</summary>
    /// <param name="format">One of <see cref="Formats"/>.</param>
    /// <returns>A deliberately malformed document.</returns>
    public static string MalformedProbe(string format)
    {
        return format switch
        {
            "json" => """{"Count":7,""",
            "yaml" => "Count: [unterminated",
            "toml" => "Count = \"unterminated",
            "ini" => "[section\nCount = 1",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown fuzz format."),
        };
    }

    /// <summary>
    /// A document that would overflow a naive recursive-descent parser but must be rejected
    /// by the bounded readers before any unbounded recursion runs.
    /// </summary>
    /// <param name="format">One of <see cref="Formats"/>.</param>
    /// <param name="depth">Nesting depth, which must exceed the documented limit.</param>
    /// <returns>A deeply nested document.</returns>
    public static string DeepNestingProbe(string format, int depth = 512)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);

        return format switch
        {
            "json" => new string('[', depth) + new string(']', depth),
            "yaml" => new string('[', depth) + "x" + new string(']', depth),
            "toml" => "[Next" + string.Concat(Enumerable.Repeat(".Next", depth)) + "]\nValue = 1\n",
            "ini" => "[" + new string('x', depth * 4),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown fuzz format."),
        };
    }

    /// <summary>A document one UTF-8 byte over the configured limit.</summary>
    /// <param name="maxBytes">Configured maximum size in bytes.</param>
    /// <returns>An oversized document, rejected before parsing.</returns>
    public static string OversizedProbe(int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return "[" + new string('x', maxBytes);
    }

    /// <summary>Gets the byte length of a document as the serialisation limit counts it.</summary>
    /// <param name="document">Document text.</param>
    /// <returns>UTF-8 byte count.</returns>
    public static int ByteCount(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Encoding.UTF8.GetByteCount(document);
    }
}

/// <summary>
/// Small object graph bound by the JSON, YAML and TOML readers.
/// </summary>
/// <remarks>
/// It deliberately mirrors the golden shape used by the serialisation unit tests: strings,
/// numbers, bools, arrays, a nested object and a dictionary. Unknown members are ignored by
/// all three readers, so one type can bind each format.
/// </remarks>
public sealed class FuzzSample
{
    /// <summary>Gets or sets a string member.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets an integer member.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets a floating-point member.</summary>
    public double? Ratio { get; set; }

    /// <summary>Gets or sets a boolean member.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a list member.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Gets or sets a nested dictionary.</summary>
    public Dictionary<string, FuzzChild> Children { get; set; } = [];

    /// <summary>Gets or sets a nested object.</summary>
    public FuzzChild? First { get; set; }
}

/// <summary>A nested object bound by the fuzz sample.</summary>
public sealed class FuzzChild
{
    /// <summary>Gets or sets a label.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets an integer array.</summary>
    public int[] Values { get; set; } = [];
}
