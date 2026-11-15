using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Playnite.Core.Serialization;

/// <summary>
/// v10-compatible JSON reading and writing.
/// </summary>
/// <remarks>
/// <para>
/// Writing keeps the v10 settings: public fields and properties, <c>DontSerialize</c> and
/// <c>JsonIgnore</c> exclusions, <c>SerializationPropertyName</c> names, nulls omitted, and a
/// maximum depth of 128. Member order follows declaration order, so the same object graph
/// always produces the same bytes.
/// </para>
/// <para>
/// Reading rejects malformed input, duplicate property names, trailing content after the root
/// value, and values that Newtonsoft would silently change (see <see cref="JsonValueGuard"/>).
/// Type names are never resolved: <see cref="TypeNameHandling.None"/> means a <c>$type</c>
/// property is ordinary data, not a request to instantiate a CLR type.
/// </para>
/// </remarks>
public static class JsonSerialization
{
    /// <summary>Maximum object and array nesting depth, matching v10.</summary>
    public const int MaxDepth = 128;

    /// <summary>Serializes an object graph to JSON.</summary>
    /// <param name="obj">Value to serialize; null produces <c>null</c>.</param>
    /// <param name="formatted">Indents the output when true.</param>
    /// <returns>The JSON document.</returns>
    /// <exception cref="SerializationException">The graph cannot be serialized.</exception>
    public static string ToJson(object? obj, bool formatted = false)
    {
        var settings = new JsonSerializerSettings
        {
            Formatting = formatted ? Formatting.Indented : Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = JsonResolver.Global,
            MaxDepth = MaxDepth,
            TypeNameHandling = TypeNameHandling.None,
        };

        try
        {
            return JsonConvert.SerializeObject(obj, settings);
        }
        catch (JsonException exception)
        {
            throw new SerializationException(
                FormatNames.Json,
                $"the value could not be serialized: {exception.Message}",
                exception);
        }
    }

    /// <summary>Deserializes a JSON document.</summary>
    /// <typeparam name="T">Root type to bind.</typeparam>
    /// <param name="json">The JSON document.</param>
    /// <param name="maxBytes">Maximum accepted UTF-8 document size.</param>
    /// <returns>The bound object, or null when the document is the JSON literal <c>null</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    /// <exception cref="SerializationException">
    /// The document is over the limit, malformed, ambiguous, has trailing content, or contains a
    /// value the binder would change.
    /// </exception>
    public static T? FromJson<T>(string json, int maxBytes = SerializationLimits.DefaultMaxDocumentBytes)
        where T : class
    {
        SerializationLimits.CheckSize(FormatNames.Json, json, maxBytes);
        json = SerializationLimits.WithoutByteOrderMark(json);

        try
        {
            using var text = new StringReader(json);
            using var reader = new JsonTextReader(text)
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double,
                MaxDepth = MaxDepth,
            };

            var loadSettings = new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore,
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                LineInfoHandling = LineInfoHandling.Load,
            };

            var token = JToken.ReadFrom(reader, loadSettings);
            if (reader.Read())
            {
                throw new SerializationException(
                    FormatNames.Json,
                    $"unexpected content after the document root at line {reader.LineNumber}, position {reader.LinePosition}.");
            }

            JsonValueGuard.Validate(token, typeof(T), "$");

            var serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                ContractResolver = JsonResolver.Global,
                DateParseHandling = DateParseHandling.None,
                MaxDepth = MaxDepth,
                TypeNameHandling = TypeNameHandling.None,
            });

            return token.ToObject<T>(serializer);
        }
        catch (SerializationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new SerializationException(FormatNames.Json, exception.Message, exception);
        }
    }
}
