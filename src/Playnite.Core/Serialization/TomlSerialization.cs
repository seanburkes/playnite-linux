using Tomlyn;

namespace Playnite.Core.Serialization;

/// <summary>
/// TOML reading and writing.
/// </summary>
/// <remarks>
/// <para>
/// TOML carries tables and arrays; it cannot represent null. Writing therefore refuses a graph
/// containing null instead of dropping the member, and reading rejects the strings, mixed
/// types and duplicate keys that Tomlyn reports. Unknown keys are ignored, matching v10.
/// </para>
/// <para>
/// Output is deterministic: members and table keys are emitted in alphabetical order rather
/// than declaration order. Deserialization never resolves a CLR type named by a document;
/// Tomlyn has no document-level type discriminator and the helper registers no custom type
/// resolver or polymorphism options.
/// </para>
/// </remarks>
public static class TomlSerialization
{
    /// <summary>Maximum table and array nesting depth.</summary>
    public const int MaxDepth = 128;

    /// <summary>Serializes an object graph to TOML.</summary>
    /// <typeparam name="T">Root table type.</typeparam>
    /// <param name="value">Value to serialize; it must be a TOML table (an object or dictionary).</param>
    /// <returns>The TOML document.</returns>
    /// <exception cref="SerializationException">
    /// The graph contains null, a non-table root, or a shape TOML cannot represent.
    /// </exception>
    public static string ToToml<T>(T value)
        where T : class
    {
        if (value is null)
        {
            throw new SerializationException(
                FormatNames.Toml,
                "null is not representable in TOML; the document root must be a table.");
        }

        try
        {
            return TomlSerializer.Serialize(value, CreateOptions());
        }
        catch (TomlException exception)
        {
            throw new SerializationException(FormatNames.Toml, Describe(exception), exception);
        }
        catch (ArgumentNullException exception)
        {
            // Tomlyn's nested-table writer reports a null member as a bare argument failure.
            throw new SerializationException(
                FormatNames.Toml,
                "null is not representable in TOML; omit the member or use a sentinel value.",
                exception);
        }
    }

    /// <summary>Deserializes a TOML document.</summary>
    /// <typeparam name="T">Root table type to bind.</typeparam>
    /// <param name="toml">The TOML document.</param>
    /// <param name="maxBytes">Maximum accepted UTF-8 document size.</param>
    /// <returns>The bound object.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="toml"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    /// <exception cref="SerializationException">
    /// The document is over the limit, malformed, has duplicate keys, or changes a value's type.
    /// </exception>
    public static T? FromToml<T>(string toml, int maxBytes = SerializationLimits.DefaultMaxDocumentBytes)
        where T : class
    {
        SerializationLimits.CheckSize(FormatNames.Toml, toml, maxBytes);
        toml = SerializationLimits.WithoutByteOrderMark(toml);

        try
        {
            return TomlSerializer.Deserialize<T>(toml, CreateOptions());
        }
        catch (Exception exception)
        {
            throw new SerializationException(
                FormatNames.Toml,
                $"the document could not be read: {Describe(exception)}",
                exception);
        }
    }

    private static TomlSerializerOptions CreateOptions()
    {
        return new TomlSerializerOptions
        {
            DuplicateKeyHandling = TomlDuplicateKeyHandling.Error,
            DefaultIgnoreCondition = TomlIgnoreCondition.Never,
            MappingOrder = TomlMappingOrderPolicy.Alphabetical,
            MaxDepth = MaxDepth,
            NewLine = TomlNewLineKind.Lf,
            RootValueHandling = TomlRootValueHandling.Error,
        };
    }

    private static string Describe(Exception exception)
    {
        if (exception is TomlException
            && exception.Message.Contains("does not support null", StringComparison.OrdinalIgnoreCase))
        {
            return "null is not representable in TOML; omit the member or use a sentinel value.";
        }

        return exception.Message;
    }
}
