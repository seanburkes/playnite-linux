namespace Playnite.SDK.Data;

/// <summary>
/// Indicates that an object member is ignored by v10-compatible serialization.
/// </summary>
/// <remarks>Port of the v10 <c>Playnite.SDK.Data.DontSerializeAttribute</c> contract.</remarks>
public class DontSerializeAttribute : Attribute
{
}

/// <summary>
/// Sets the serialized name of an object member for v10-compatible serialization.
/// </summary>
/// <remarks>Port of the v10 <c>Playnite.SDK.Data.SerializationPropertyNameAttribute</c> contract.</remarks>
public class SerializationPropertyNameAttribute : Attribute
{
    /// <summary>
    /// Creates a new instance of <see cref="SerializationPropertyNameAttribute"/>.
    /// </summary>
    /// <param name="propertyName">Name the member uses in serialized documents.</param>
    public SerializationPropertyNameAttribute(string propertyName)
    {
        PropertyName = propertyName;
    }

    /// <summary>
    /// Gets or sets the serialized member name.
    /// </summary>
    public string PropertyName { get; set; }
}
