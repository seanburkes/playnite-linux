using System.Text;

namespace Playnite.Platform.Linux.Integration;

/// <summary>
/// Parsed form of the application's <c>playnite://</c> URI. The scheme is reserved for the
/// host; sources select a command or plugin handler, exactly as the v10 handler did.
/// </summary>
public sealed record PlayniteUri
{
    public const string Scheme = "playnite";
    public const int MaxLength = 2048;
    public const int MaxArguments = 32;
    public const int MaxArgumentLength = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private PlayniteUri(string source, IReadOnlyList<string> arguments)
    {
        Source = source;
        Arguments = arguments;
    }

    public string Source { get; }
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Parses and validates an external URI. Rejects input that cannot be a well-formed
    /// application URI instead of handing it to a command dispatcher. No shell is involved.
    /// </summary>
    public static bool TryParse(string? value, out PlayniteUri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The URI is empty.";
            return false;
        }

        if (value.Length > MaxLength)
        {
            error = $"The URI exceeds {MaxLength} characters.";
            return false;
        }

        foreach (var character in value)
        {
            if (character is < ' ' or '\u007f')
            {
                error = "The URI contains a control character.";
                return false;
            }

            if (char.IsWhiteSpace(character))
            {
                error = "The URI contains unescaped whitespace.";
                return false;
            }
        }

        var prefix = Scheme + "://";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"The URI scheme must be {Scheme}://.";
            return false;
        }

        if (value.IndexOfAny(['?', '#', '\\']) >= 0)
        {
            error = "The URI must not contain a query, fragment or backslash.";
            return false;
        }

        var segments = value[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 1)
        {
            error = "The URI is missing a source.";
            return false;
        }

        var source = segments[0];
        if (source is "." or "..")
        {
            error = "The URI source is a traversal segment.";
            return false;
        }

        if (!IsValidSource(source))
        {
            error = "The URI source contains characters outside letters, digits, '.', '_', '~' and '-'.";
            return false;
        }

        var arguments = segments.Skip(1).ToArray();
        if (arguments.Length > MaxArguments)
        {
            error = $"The URI carries more than {MaxArguments} arguments.";
            return false;
        }

        var decoded = new string[arguments.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].Length > MaxArgumentLength)
            {
                error = $"Argument {index + 1} exceeds {MaxArgumentLength} characters.";
                return false;
            }

            if (!TryDecode(arguments[index], out var argument, out error))
            {
                return false;
            }

            decoded[index] = argument;
        }

        uri = new PlayniteUri(source, Array.AsReadOnly(decoded));
        return true;
    }

    private static bool IsValidSource(string source) => source.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '~' or '-');

    private static bool TryDecode(string value, out string decoded, out string? error)
    {
        decoded = string.Empty;
        error = null;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%' && (index + 2 >= value.Length
                || !char.IsAsciiHexDigit(value[index + 1])
                || !char.IsAsciiHexDigit(value[index + 2])))
            {
                error = "The URI contains an invalid percent escape.";
                return false;
            }
        }

        try
        {
            // v10 used HttpUtility.UrlDecode, which treats '+' as a space. Percent escapes are
            // decoded as bytes so malformed UTF-8 fails instead of becoming a replacement char.
            var bytes = new List<byte>(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] == '%')
                {
                    bytes.Add((byte)((HexValue(value[index + 1]) << 4) | HexValue(value[index + 2])));
                    index += 2;
                }
                else
                {
                    bytes.AddRange(StrictUtf8.GetBytes(value[index] == '+' ? " " : value[index].ToString()));
                }
            }

            decoded = StrictUtf8.GetString(bytes.ToArray());
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException or EncoderFallbackException or ArgumentException)
        {
            error = "The URI contains invalid UTF-8 or an invalid escape.";
            return false;
        }

        if (decoded.Any(character => character is < ' ' or '\u007f'))
        {
            error = "The URI argument decodes to a control character.";
            return false;
        }

        return true;
    }

    private static int HexValue(char character) => character <= '9'
        ? character - '0'
        : (character | 0x20) - 'a' + 10;
}
