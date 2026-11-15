using System.Collections.Concurrent;
using System.Reflection;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Playnite.Core.Serialization;

/// <summary>
/// Rejects YAML nulls that would become a default value instead of a stored null.
/// </summary>
/// <remarks>
/// YamlDotNet binds an explicit YAML null into a non-nullable value member as that type's
/// default (0, false, ...), which silently changes the document. The guard walks the parsed
/// node tree using the same member names the deserializer binds and refuses those nodes.
/// Plain YAML scalar typing (an unquoted <c>123</c> that satisfies an <see cref="int"/>
/// member) is the format's own model and is left to the deserializer.
/// </remarks>
internal static class YamlNullGuard
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Type>> MemberCache = new();

    internal static void Validate(string yaml, Type declaredType)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0)
        {
            return;
        }

        ValidateNode(stream.Documents[0].RootNode, declaredType, "$", 0);
    }

    private static void ValidateNode(YamlNode node, Type declaredType, string path, int depth)
    {
        if (depth > YamlSerialization.MaxDepth)
        {
            throw new SerializationException(
                FormatNames.Yaml,
                $"value at '{path}': nesting exceeds the maximum depth of {YamlSerialization.MaxDepth}.");
        }

        var underlying = Nullable.GetUnderlyingType(declaredType);
        var type = underlying ?? declaredType;
        var allowsNull = underlying is not null || !declaredType.IsValueType;

        if (node is YamlScalarNode scalar)
        {
            if (!IsNull(scalar))
            {
                return;
            }

            if (!allowsNull)
            {
                throw new SerializationException(
                    FormatNames.Yaml,
                    $"value at '{path}': null cannot be stored in {Describe(type)}.");
            }

            return;
        }

        if (type == typeof(object) || type == typeof(string))
        {
            return;
        }

        if (node is YamlSequenceNode sequence)
        {
            if (TryGetElementType(type, out var elementType))
            {
                for (var index = 0; index < sequence.Children.Count; index++)
                {
                    ValidateNode(sequence.Children[index], elementType, $"{path}[{index}]", depth + 1);
                }
            }

            return;
        }

        if (node is not YamlMappingNode mapping)
        {
            return;
        }

        if (TryGetDictionaryValueType(type, out var valueType))
        {
            foreach (var pair in mapping.Children)
            {
                ValidateNode(pair.Value, valueType, Child(path, KeyText(pair.Key)), depth + 1);
            }

            return;
        }

        var members = Members(type);
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode key || key.Value is null
                || !members.TryGetValue(key.Value, out var memberType))
            {
                continue;
            }

            ValidateNode(pair.Value, memberType, Child(path, key.Value), depth + 1);
        }
    }

    private static bool IsNull(YamlScalarNode scalar)
    {
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted
            or ScalarStyle.Literal or ScalarStyle.Folded)
        {
            return false;
        }

        var value = scalar.Value;
        return string.IsNullOrEmpty(value)
            || value == "~"
            || value.Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, Type> Members(Type type)
    {
        return MemberCache.GetOrAdd(type, static target =>
        {
            var members = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var property in target.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod is null
                    || property.SetMethod is null
                    || property.IsDefined(typeof(YamlIgnoreAttribute), inherit: true))
                {
                    continue;
                }

                members.TryAdd(MemberName(property), property.PropertyType);
            }

            foreach (var field in target.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsDefined(typeof(YamlIgnoreAttribute), inherit: true) || field.IsInitOnly)
                {
                    continue;
                }

                members.TryAdd(MemberName(field), field.FieldType);
            }

            return members;
        });
    }

    private static string MemberName(MemberInfo member)
    {
        var attribute = member.GetCustomAttribute<YamlMemberAttribute>(inherit: true);
        return attribute?.Alias is { Length: > 0 } alias ? alias : member.Name;
    }

    private static bool TryGetElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null)
        {
            elementType = enumerable.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(object);
        return type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        var dictionary = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
            ? type
            : type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (dictionary is not null)
        {
            valueType = dictionary.GetGenericArguments()[1];
            return true;
        }

        valueType = typeof(object);
        return false;
    }

    private static string KeyText(YamlNode key)
    {
        return key is YamlScalarNode scalar && scalar.Value is { Length: > 0 } value ? value : "?";
    }

    private static string Child(string path, string name)
    {
        return string.IsNullOrEmpty(path) ? name : $"{path}.{name}";
    }

    private static string Describe(Type type)
    {
        return type.FullName ?? type.Name;
    }
}
