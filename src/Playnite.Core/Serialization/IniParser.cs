namespace Playnite.Core.Serialization;

/// <summary>A single <c>name=value</c> entry inside an INI section.</summary>
public sealed class IniItem
{
    /// <summary>Creates an entry.</summary>
    /// <param name="name">Entry name; must not be empty.</param>
    /// <param name="value">Entry text, taken verbatim.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public IniItem(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Value = value;
    }

    /// <summary>Gets the entry name.</summary>
    public string Name { get; }

    /// <summary>Gets the entry text exactly as it appeared after the last <c>=</c>.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>A named INI section with ordered entries.</summary>
public sealed class IniSection
{
    private readonly List<IniItem> items = [];

    /// <summary>Creates a section.</summary>
    /// <param name="name">Section name; must not be empty.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public IniSection(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>Gets the section name.</summary>
    public string Name { get; }

    /// <summary>Gets the entries in document order, including duplicate names.</summary>
    public IReadOnlyList<IniItem> Items => items;

    /// <summary>Gets the first value with the given name, or null when absent.</summary>
    /// <param name="itemName">Entry name to look up.</param>
    /// <returns>The first matching value, or null.</returns>
    public string? this[string itemName]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(itemName);
            return items.FirstOrDefault(item => item.Name == itemName)?.Value;
        }
    }

    internal void Add(IniItem item)
    {
        items.Add(item);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>A parsed INI document.</summary>
public sealed class IniData
{
    private readonly List<IniSection> sections = [];

    /// <summary>Gets the sections in document order, including duplicate names.</summary>
    public IReadOnlyList<IniSection> Sections => sections;

    /// <summary>Gets the first section with the given name, or null when absent.</summary>
    /// <param name="sectionName">Section name to look up.</param>
    /// <returns>The first matching section, or null.</returns>
    public IniSection? this[string sectionName]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(sectionName);
            return sections.FirstOrDefault(section => section.Name == sectionName);
        }
    }

    internal void Add(IniSection section)
    {
        sections.Add(section);
    }
}

/// <summary>
/// Reads the INI files v10 parsed, with hard failures instead of silent drops.
/// </summary>
/// <remarks>
/// <para>
/// The shape matches v10's reader: <c>;</c>-prefixed comments, <c>[section]</c> headers, and
/// <c>name=value</c> entries whose value is the text after the last <c>=</c>. Entry text is
/// kept verbatim, duplicate sections and names are preserved in order, and lookups return the
/// first match. There is no writer: INI in v10 is read-only input.
/// </para>
/// <para>
/// Unlike v10, a line the reader cannot classify, an entry before the first section, and an
/// empty section header fail with the line number instead of being dropped. A UTF-8 byte order
/// mark at the start of the document is ignored.
/// </para>
/// </remarks>
public static class IniParser
{
    /// <summary>Parses INI text.</summary>
    /// <param name="text">Complete INI document.</param>
    /// <param name="maxBytes">Maximum accepted UTF-8 document size.</param>
    /// <returns>The parsed document; an empty or comment-only document has no sections.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    /// <exception cref="SerializationException">A line cannot be classified or is out of place.</exception>
    public static IniData Parse(string text, int maxBytes = SerializationLimits.DefaultMaxDocumentBytes)
    {
        SerializationLimits.CheckSize(FormatNames.Ini, text, maxBytes);

        text = SerializationLimits.WithoutByteOrderMark(text);

        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return ParseLines(lines);
    }

    /// <summary>Parses INI lines, matching the v10 <c>File.ReadAllLines</c> input shape.</summary>
    /// <param name="lines">Document lines without terminators.</param>
    /// <param name="maxBytes">Maximum accepted UTF-8 document size.</param>
    /// <returns>The parsed document; an empty or comment-only document has no sections.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    /// <exception cref="SerializationException">A line cannot be classified or is out of place.</exception>
    public static IniData Parse(IReadOnlyList<string> lines, int maxBytes = SerializationLimits.DefaultMaxDocumentBytes)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var bytes = 0;
        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            bytes += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
        }

        if (bytes > maxBytes)
        {
            throw new SerializationException(
                FormatNames.Ini,
                $"The document is {bytes} bytes, which exceeds the {maxBytes}-byte limit.");
        }

        return ParseLines(lines);
    }

    private static IniData ParseLines(IReadOnlyList<string> lines)
    {
        var data = new IniData();
        IniSection? section = null;

        for (var index = 0; index < lines.Count; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith(';'))
            {
                continue;
            }

            if (trimmed.StartsWith('['))
            {
                if (trimmed.Length < 2 || !trimmed.EndsWith(']') || trimmed.Length == 2)
                {
                    throw Fail(lineNumber, $"'{trimmed}' is not a section header of the form [name].");
                }

                section = new IniSection(trimmed[1..^1]);
                data.Add(section);
                continue;
            }

            var separator = trimmed.LastIndexOf('=');
            if (separator < 0)
            {
                throw Fail(lineNumber, $"'{trimmed}' is neither a comment, a section header, nor a name=value entry.");
            }

            if (separator == 0)
            {
                throw Fail(lineNumber, $"'{trimmed}' has no entry name before '='.");
            }

            if (section is null)
            {
                throw Fail(lineNumber, $"'{trimmed}' appears before the first section header.");
            }

            section.Add(new IniItem(trimmed[..separator], trimmed[(separator + 1)..]));
        }

        return data;
    }

    private static SerializationException Fail(int lineNumber, string message)
    {
        return new SerializationException(FormatNames.Ini, $"line {lineNumber}: {message}");
    }
}
