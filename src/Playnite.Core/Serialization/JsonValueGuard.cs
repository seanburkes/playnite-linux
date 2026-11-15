using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Playnite.Core.Serialization;

/// <summary>
/// Rejects JSON values that the binder would silently change instead of converting exactly.
/// </summary>
/// <remarks>
/// v10 used Newtonsoft's default binding, which rounds a fractional number into an integer
/// property and rounds integers above 2^53 into a <see cref="double"/>. Both change the stored
/// value, so the helpers refuse them. Type-preserving conversions that Newtonsoft performs
/// without losing the value (for example <c>"123"</c> into an <see cref="int"/>) stay available
/// because v10 settings documents rely on that binder; a value that cannot be represented
/// exactly throws from Newtonsoft and is reported unchanged.
/// </remarks>
internal static class JsonValueGuard
{
    private static readonly BigInteger MaxExactDoubleInteger = new(9007199254740992L);

    internal static void Validate(JToken token, Type declaredType, string path)
    {
        var underlying = Nullable.GetUnderlyingType(declaredType);
        var type = underlying ?? declaredType;
        var allowsNull = underlying is not null || !declaredType.IsValueType;

        switch (token.Type)
        {
            case JTokenType.Null:
            case JTokenType.Undefined:
                if (!allowsNull)
                {
                    Fail(path, $"null cannot be stored in {Describe(declaredType)}");
                }

                return;

            case JTokenType.Object:
                ValidateObject((JObject)token, type, path);
                return;

            case JTokenType.Array:
                ValidateArray((JArray)token, type, path);
                return;

            case JTokenType.Float:
                if (IsIntegral(type) || type.IsEnum)
                {
                    Fail(path, $"the fractional value {token.ToString(Formatting.None)} would be truncated for {Describe(type)}");
                }

                return;

            case JTokenType.Integer:
                if ((type == typeof(double) || type == typeof(float))
                    && Magnitude(((JValue)token).Value) > MaxExactDoubleInteger)
                {
                    Fail(path, $"the integer value {token.ToString(Formatting.None)} cannot be represented exactly as {Describe(type)}");
                }

                return;

            default:
                return;
        }
    }

    private static void ValidateObject(JObject value, Type type, string path)
    {
        if (type == typeof(object) || typeof(JToken).IsAssignableFrom(type))
        {
            return;
        }

        var contract = JsonResolver.Global.ResolveContract(type);
        if (contract.Converter is not null)
        {
            // A custom converter owns the representation; the guard cannot know its rules.
            return;
        }

        switch (contract)
        {
            case JsonDictionaryContract dictionary:
                var valueType = dictionary.DictionaryValueType ?? typeof(object);
                foreach (var property in value.Properties())
                {
                    Validate(property.Value, valueType, Child(path, property.Name));
                }

                return;

            case JsonObjectContract objectContract:
                foreach (var property in value.Properties())
                {
                    var member = objectContract.Properties.GetClosestMatchProperty(property.Name);
                    if (member is null || member.Ignored || member.Converter is not null
                        || member.PropertyName is null)
                    {
                        continue;
                    }

                    if (member.PropertyType is { } memberType)
                    {
                        Validate(property.Value, memberType, Child(path, property.Name));
                    }
                }

                return;

            default:
                return;
        }
    }

    private static void ValidateArray(JArray value, Type type, string path)
    {
        if (type == typeof(object) || typeof(JToken).IsAssignableFrom(type))
        {
            return;
        }

        var contract = JsonResolver.Global.ResolveContract(type);
        if (contract.Converter is not null || contract is not JsonArrayContract arrayContract
            || arrayContract.CollectionItemType is not { } itemType)
        {
            return;
        }

        for (var index = 0; index < value.Count; index++)
        {
            Validate(value[index], itemType, $"{path}[{index}]");
        }
    }

    private static BigInteger Magnitude(object? value)
    {
        return value switch
        {
            long number => BigInteger.Abs(number),
            ulong number => number,
            BigInteger number => BigInteger.Abs(number),
            int number => BigInteger.Abs(number),
            uint number => number,
            short number => BigInteger.Abs(number),
            ushort number => number,
            sbyte number => BigInteger.Abs(number),
            byte number => number,
            _ => BigInteger.Zero,
        };
    }

    private static string Child(string path, string name)
    {
        return $"{path}.{name}";
    }

    private static void Fail(string path, string message)
    {
        throw new SerializationException(FormatNames.Json, $"value at '{path}': {message}.");
    }

    private static bool IsIntegral(Type type)
    {
        return type == typeof(sbyte) || type == typeof(byte)
            || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint)
            || type == typeof(long) || type == typeof(ulong)
            || type == typeof(Int128) || type == typeof(UInt128);
    }

    private static string Describe(Type type)
    {
        return type.FullName ?? type.Name;
    }
}
