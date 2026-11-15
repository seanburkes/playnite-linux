using System.Text;

namespace Playnite.Core.Serialization;

/// <summary>
/// Bounds the document sizes the serialization helpers accept.
/// </summary>
/// <remarks>
/// Config files, manifests and settings documents are small; an unbounded parser is an easy
/// denial of service. The limit is measured in UTF-8 bytes after the caller has decoded the
/// text, so it also bounds the parser's input even when the source file was larger.
/// </remarks>
public static class SerializationLimits
{
    /// <summary>
    /// Default maximum document size: 8 MiB of UTF-8 text.
    /// </summary>
    /// <remarks>
    /// The largest v10 document in the frozen corpus is the 14 KiB settings golden, so the
    /// default leaves three orders of magnitude of headroom while keeping a malicious file
    /// bounded.
    /// </remarks>
    public const int DefaultMaxDocumentBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Throws when <paramref name="document"/> exceeds <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="format">Document format name used in the failure message.</param>
    /// <param name="document">Text about to be parsed.</param>
    /// <param name="maxBytes">Maximum accepted UTF-8 byte count; must be positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    /// <exception cref="SerializationException">The document is over the limit.</exception>
    /// <summary>
    /// Removes a single leading UTF-8 byte order mark decoded as <c>U+FEFF</c>.
    /// </summary>
    /// <remarks>
    /// v10 read files through a <see cref="StreamReader"/> that consumed the mark. The string
    /// helpers accept text whose mark survived decoding, so they strip it before parsing.
    /// </remarks>
    internal static string WithoutByteOrderMark(string document)
    {
        return document.Length > 0 && document[0] == '\uFEFF' ? document[1..] : document;
    }

    internal static void CheckSize(string format, string document, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var bytes = Encoding.UTF8.GetByteCount(document);
        if (bytes > maxBytes)
        {
            throw new SerializationException(
                format,
                $"The document is {bytes} bytes, which exceeds the {maxBytes}-byte limit.");
        }
    }
}
