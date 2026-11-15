using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Playnite.Core.Serialization;

/// <summary>
/// YAML reading and writing for v10 manifests and settings.
/// </summary>
/// <remarks>
/// <para>
/// Reading keeps the v10 deserializer behaviour (<c>IgnoreUnmatchedProperties</c>) and adds
/// duplicate-key detection, a recursion limit, and rejection of nulls that would become a
/// value type's default. Multiple documents in one stream are rejected by YamlDotNet, and a
/// tag such as <c>!System.IO.FileInfo</c> is rejected because no tag mapping or type resolver
/// is registered; a document cannot name a CLR type.
/// </para>
/// <para>
/// Writing quotes strings that would otherwise read back as another type, emits no anchors or
/// aliases, and keeps declaration order, so the same object graph always produces the same
/// bytes.
/// </para>
/// </remarks>
public static class YamlSerialization
{
    /// <summary>Maximum nesting depth, matching the JSON helper.</summary>
    public const int MaxDepth = 128;

    /// <summary>Serializes an object graph to YAML.</summary>
    /// <param name="obj">Value to serialize.</param>
    /// <returns>The YAML document, terminated by a newline.</returns>
    /// <exception cref="SerializationException">The graph cannot be serialized.</exception>
    public static string ToYaml(object? obj)
    {
        var serializer = new SerializerBuilder()
            .WithQuotingNecessaryStrings()
            .DisableAliases()
            .WithMaximumRecursion(MaxDepth)
            .Build();

        try
        {
            return serializer.Serialize(obj);
        }
        catch (Exception exception)
        {
            throw new SerializationException(
                FormatNames.Yaml,
                $"the document could not be read: {exception.Message}",
                exception);
        }
    }

    /// <summary>Deserializes a YAML document.</summary>
    /// <typeparam name="T">Root type to bind.</typeparam>
    /// <param name="yaml">The YAML document.</param>
    /// <param name="maxBytes">Maximum accepted UTF-8 document size.</param>
    /// <returns>The bound object, or null for an empty document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="yaml"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    /// <exception cref="SerializationException">
    /// The document is over the limit, malformed, has duplicate keys, has multiple documents,
    /// carries an unresolved tag, or contains a null for a value member.
    /// </exception>
    public static T? FromYaml<T>(string yaml, int maxBytes = SerializationLimits.DefaultMaxDocumentBytes)
        where T : class
    {
        SerializationLimits.CheckSize(FormatNames.Yaml, yaml, maxBytes);
        yaml = SerializationLimits.WithoutByteOrderMark(yaml);

        try
        {
            YamlDepthGuard.Validate(yaml);
            YamlNullGuard.Validate(yaml, typeof(T));

            var deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .WithDuplicateKeyChecking()
                .WithMaximumRecursion(MaxDepth)
                .Build();

            return deserializer.Deserialize<T>(yaml);
        }
        catch (SerializationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SerializationException(
                FormatNames.Yaml,
                $"the document could not be read: {exception.Message}",
                exception);
        }
    }
}
