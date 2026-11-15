namespace Playnite.Core.Serialization;

/// <summary>Format names used in failure messages.</summary>
internal static class FormatNames
{
    internal const string Json = "JSON";
    internal const string Yaml = "YAML";
    internal const string Toml = "TOML";
    internal const string Ini = "INI";
}

/// <summary>
/// Raised when a document cannot be read or written without losing information.
/// </summary>
/// <remarks>
/// The message always starts with the document format so a caller can present it without
/// guessing which parser failed. Windows and UTF-8 byte positions reported by the underlying
/// library are preserved in the message.
/// </remarks>
public sealed class SerializationException : Exception
{
    /// <summary>Creates an exception for a malformed or unrepresentable document.</summary>
    /// <param name="format">Document format name, for example <c>JSON</c>.</param>
    /// <param name="message">What failed and, where available, where.</param>
    public SerializationException(string format, string message)
        : base($"{format}: {message}")
    {
        Format = format;
    }

    /// <summary>Creates an exception that keeps the underlying parser failure.</summary>
    /// <param name="format">Document format name, for example <c>JSON</c>.</param>
    /// <param name="message">What failed and, where available, where.</param>
    /// <param name="innerException">Parser failure that explains the message.</param>
    public SerializationException(string format, string message, Exception innerException)
        : base($"{format}: {message}", innerException)
    {
        Format = format;
    }

    /// <summary>Gets the document format name carried by the message.</summary>
    public string Format { get; }
}
