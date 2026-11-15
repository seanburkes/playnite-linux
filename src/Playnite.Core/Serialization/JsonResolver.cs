using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Playnite.SDK.Data;

namespace Playnite.Core.Serialization;

/// <summary>
/// v10-compatible Newtonsoft contract resolution: field serialization, custom member names and
/// the SDK <c>DontSerialize</c> exclusion.
/// </summary>
/// <remarks>
/// Ported from <c>legacy/source/Playnite/Common/Serialization.cs</c> so settings and manifest
/// bytes keep their v10 member set and names. The resolver does not enable type-name handling.
/// </remarks>
public class JsonResolver : DefaultContractResolver
{
    /// <summary>Gets the shared resolver instance.</summary>
    public static JsonResolver Global { get; } = new JsonResolver();

    /// <inheritdoc />
    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);
        if (Attribute.IsDefined(member, typeof(SerializationPropertyNameAttribute)))
        {
            var attribute = (SerializationPropertyNameAttribute)Attribute.GetCustomAttribute(
                member,
                typeof(SerializationPropertyNameAttribute))!;
            property.PropertyName = attribute.PropertyName;
        }

        return property;
    }

    /// <inheritdoc />
    protected override List<MemberInfo> GetSerializableMembers(Type objectType)
    {
        return objectType
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => member is PropertyInfo or FieldInfo)
            .Where(member => !Attribute.IsDefined(member, typeof(DontSerializeAttribute))
                && !Attribute.IsDefined(member, typeof(JsonIgnoreAttribute)))
            .ToList();
    }
}
