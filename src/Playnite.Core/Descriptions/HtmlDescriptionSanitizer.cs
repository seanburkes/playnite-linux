using System.Globalization;
using System.Text;

namespace Playnite.Core.Descriptions;

/// <summary>One sanitised description and what was removed to make it safe.</summary>
public sealed record HtmlSanitizationResult(
    string Html,
    int RemovedElements = 0,
    int RemovedAttributes = 0,
    bool Truncated = false)
{
    /// <summary>True when nothing safe remains.</summary>
    public bool IsEmpty => Html.Length == 0;
}

/// <summary>The injectable sanitiser seam. A fake records calls and proves the service is isolated.</summary>
public interface IHtmlSanitizer
{
    /// <summary>Returns a safe subset of untrusted HTML. Never throws for malformed input.</summary>
    HtmlSanitizationResult Sanitize(string? html);
}

/// <summary>
/// Reduces an untrusted game description to a small, documented HTML subset.
/// </summary>
/// <remarks>
/// The output contains only <see cref="AllowedElements"/>, no attributes except one validated
/// <c>href</c> on <c>a</c>, no script, style, event handler, iframe, object, embed, SVG or other
/// active content, and therefore no remote resource injection. Anchors get
/// <c>rel="noreferrer noopener"</c>; only absolute <c>http</c>/<c>https</c> URLs without
/// credentials survive. Input is bounded and unterminated markup is escaped or dropped, so the
/// sanitizer is total: malformed or hostile input never throws and never reaches a browser raw.
/// </remarks>
public sealed class HtmlDescriptionSanitizer : IHtmlSanitizer
{
    /// <summary>Largest accepted input length in characters; longer input is truncated.</summary>
    public const int MaxInputLength = 256 * 1024;

    /// <summary>Largest output length before the sanitizer stops appending.</summary>
    public const int MaxOutputLength = 512 * 1024;

    /// <summary>Largest accepted element nesting depth.</summary>
    public const int MaxDepth = 64;

    /// <summary>Largest accepted href value in characters.</summary>
    public const int MaxHrefLength = 2048;

    /// <summary>Element names that survive, lower-case and ordinal.</summary>
    public static IReadOnlySet<string> AllowedElements { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "a", "b", "blockquote", "br", "code", "del", "div", "em", "h1", "h2", "h3", "h4",
        "h5", "h6", "hr", "i", "ins", "li", "ol", "p", "pre", "s", "span", "strong", "sub",
        "sup", "u", "ul",
    };

    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
    {
        "br", "hr",
    };

    // Elements whose contents are never display text in a browser. Dropping the tag but keeping
    // the text would leak script/style source into the description, so the content goes too.
    private static readonly HashSet<string> DropContentElements = new(StringComparer.Ordinal)
    {
        "embed", "iframe", "math", "noembed", "noframes", "noscript", "object", "plaintext",
        "script", "style", "svg", "template", "textarea", "title", "xmp",
    };

    private static readonly Dictionary<string, string> NamedEntities = new(StringComparer.Ordinal)
    {
        ["amp"] = "&",
        ["apos"] = "'",
        ["bull"] = "\u2022",
        ["copy"] = "\u00a9",
        ["deg"] = "\u00b0",
        ["divide"] = "\u00f7",
        ["euro"] = "\u20ac",
        ["frac12"] = "\u00bd",
        ["gt"] = ">",
        ["hellip"] = "\u2026",
        ["laquo"] = "\u00ab",
        ["larr"] = "\u2190",
        ["ldquo"] = "\u201c",
        ["lsquo"] = "\u2018",
        ["lt"] = "<",
        ["mdash"] = "\u2014",
        ["middot"] = "\u00b7",
        ["nbsp"] = "\u00a0",
        ["ndash"] = "\u2013",
        ["plusmn"] = "\u00b1",
        ["pound"] = "\u00a3",
        ["quot"] = "\"",
        ["raquo"] = "\u00bb",
        ["rarr"] = "\u2192",
        ["reg"] = "\u00ae",
        ["rdquo"] = "\u201d",
        ["rsquo"] = "\u2019",
        ["sect"] = "\u00a7",
        ["times"] = "\u00d7",
        ["trade"] = "\u2122",
        ["yen"] = "\u00a5",
    };

    /// <inheritdoc />
    public HtmlSanitizationResult Sanitize(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return new HtmlSanitizationResult(string.Empty);
        }

        var input = html.Length > MaxInputLength ? html[..MaxInputLength] : html;
        var truncated = input.Length != html.Length;
        var output = new StringBuilder(Math.Min(input.Length, 4096));
        var stack = new List<string>();
        var removedElements = 0;
        var removedAttributes = 0;
        var index = 0;

        while (index < input.Length)
        {
            if (output.Length >= MaxOutputLength)
            {
                truncated = true;
                break;
            }

            if (input[index] != '<')
            {
                var start = index;
                while (index < input.Length && input[index] != '<')
                {
                    index++;
                }

                if (!AppendText(output, input[start..index]))
                {
                    truncated = true;
                    break;
                }

                continue;
            }

            if (StartsWith(input, index, "<!--"))
            {
                index = SkipComment(input, index);
                continue;
            }

            if (index + 1 < input.Length && (input[index + 1] == '!' || input[index + 1] == '?'))
            {
                index = SkipTo(input, index + 2, '>');
                continue;
            }

            if (index + 1 < input.Length && input[index + 1] == '/')
            {
                index = ReadEndTag(input, index, stack, output);
                continue;
            }

            var parseIndex = index + 1;
            var tagName = ReadName(input, ref parseIndex);
            if (tagName.Length == 0)
            {
                output.Append("&lt;");
                index++;
                continue;
            }

            var attributes = new List<(string Name, string Value)>();
            var selfClosing = false;
            if (!TryReadAttributes(input, ref parseIndex, attributes, ref selfClosing))
            {
                // Unterminated start tag: the rest cannot be trusted as markup.
                truncated = true;
                break;
            }

            index = parseIndex;
            var name = tagName.ToLowerInvariant();

            if (DropContentElements.Contains(name))
            {
                removedElements++;
                index = SkipElementContent(input, index, name);
                continue;
            }

            if (!AllowedElements.Contains(name) || stack.Count >= MaxDepth)
            {
                removedElements++;
                continue;
            }

            output.Append('<').Append(name);
            foreach (var attribute in attributes)
            {
                if (name != "a" || attribute.Name != "href")
                {
                    removedAttributes++;
                    continue;
                }

                var href = SafeHref(attribute.Value);
                if (href is null)
                {
                    removedAttributes++;
                    continue;
                }

                AppendAttribute(output, "href", href);
                output.Append(" rel=\"noreferrer noopener\"");
            }

            output.Append('>');
            if (!selfClosing && !VoidElements.Contains(name))
            {
                stack.Add(name);
            }
        }

        for (var depth = stack.Count - 1; depth >= 0; depth--)
        {
            output.Append("</").Append(stack[depth]).Append('>');
        }

        return new HtmlSanitizationResult(output.ToString(), removedElements, removedAttributes, truncated);
    }

    private static int ReadEndTag(string input, int index, List<string> stack, StringBuilder output)
    {
        index += 2;
        SkipWhitespace(input, ref index);
        var name = ReadName(input, ref index).ToLowerInvariant();
        if (name.Length == 0)
        {
            output.Append("&lt;/");
            return index;
        }

        index = SkipTo(input, index, '>');
        var open = stack.LastIndexOf(name);
        if (open < 0)
        {
            return index;
        }

        for (var depth = stack.Count - 1; depth >= open; depth--)
        {
            output.Append("</").Append(stack[depth]).Append('>');
            stack.RemoveAt(depth);
        }

        return index;
    }

    private static bool TryReadAttributes(
        string input,
        ref int index,
        List<(string Name, string Value)> attributes,
        ref bool selfClosing)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (index < input.Length)
        {
            SkipWhitespace(input, ref index);
            if (index >= input.Length)
            {
                return false;
            }

            if (input[index] == '>')
            {
                index++;
                return true;
            }

            if (input[index] == '/')
            {
                index++;
                if (index < input.Length && input[index] == '>')
                {
                    index++;
                    selfClosing = true;
                    return true;
                }

                continue;
            }

            var name = ReadAttributeName(input, ref index);
            if (name.Length == 0)
            {
                index++;
                continue;
            }

            SkipWhitespace(input, ref index);
            var value = string.Empty;
            if (index < input.Length && input[index] == '=')
            {
                index++;
                SkipWhitespace(input, ref index);
                if (index >= input.Length)
                {
                    return false;
                }

                if (input[index] is '"' or '\'')
                {
                    var quote = input[index++];
                    var start = index;
                    while (index < input.Length && input[index] != quote)
                    {
                        index++;
                    }

                    if (index >= input.Length)
                    {
                        return false;
                    }

                    value = input[start..index];
                    index++;
                }
                else
                {
                    var start = index;
                    while (index < input.Length && !char.IsWhiteSpace(input[index]) && input[index] != '>')
                    {
                        index++;
                    }

                    value = input[start..index];
                }
            }

            var lower = name.ToLowerInvariant();
            if (seen.Add(lower))
            {
                attributes.Add((lower, value));
            }
        }

        return false;
    }

    private static string ReadName(string input, ref int index)
    {
        if (index >= input.Length || !char.IsAsciiLetter(input[index]))
        {
            return string.Empty;
        }

        var start = index;
        while (index < input.Length
            && (char.IsAsciiLetterOrDigit(input[index]) || input[index] == '-'))
        {
            index++;
        }

        return input[start..index];
    }

    private static string ReadAttributeName(string input, ref int index)
    {
        var start = index;
        while (index < input.Length
            && !char.IsWhiteSpace(input[index])
            && input[index] is not ('=' or '>' or '/' or '"' or '\'' or '<'))
        {
            index++;
        }

        return input[start..index];
    }

    private static void SkipWhitespace(string input, ref int index)
    {
        while (index < input.Length && char.IsWhiteSpace(input[index]))
        {
            index++;
        }
    }

    private static int SkipTo(string input, int index, char terminator)
    {
        var found = input.IndexOf(terminator, index);
        return found < 0 ? input.Length : found + 1;
    }

    private static int SkipComment(string input, int index)
    {
        var end = input.IndexOf("-->", index + 4, StringComparison.Ordinal);
        return end < 0 ? input.Length : end + 3;
    }

    private static int SkipElementContent(string input, int index, string name)
    {
        while (index < input.Length)
        {
            var candidate = input.IndexOf("</", index, StringComparison.Ordinal);
            if (candidate < 0)
            {
                return input.Length;
            }

            var afterName = candidate + 2 + name.Length;
            if (afterName <= input.Length
                && input.AsSpan(candidate + 2, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)
                && (afterName == input.Length
                    || char.IsWhiteSpace(input[afterName])
                    || input[afterName] is '>' or '/'))
            {
                return SkipTo(input, afterName, '>');
            }

            index = candidate + 2;
        }

        return input.Length;
    }

    private static bool StartsWith(string input, int index, string value) =>
        input.AsSpan(index).StartsWith(value, StringComparison.Ordinal);

    private static string? SafeHref(string value)
    {
        var decoded = DecodeEntities(value);
        var trimmed = decoded.Trim(' ', '\t', '\n', '\r', '\f');
        if (trimmed.Length == 0
            || trimmed.Length > MaxHrefLength
            || trimmed.Any(char.IsControl)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length > 0)
        {
            return null;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static bool AppendText(StringBuilder output, string text)
    {
        var decoded = DecodeEntities(text);
        foreach (var character in decoded)
        {
            if (output.Length >= MaxOutputLength)
            {
                return false;
            }

            switch (character)
            {
                case '&':
                    output.Append("&amp;");
                    break;
                case '<':
                    output.Append("&lt;");
                    break;
                case '>':
                    output.Append("&gt;");
                    break;
                case '\n':
                case '\r':
                case '\t':
                    output.Append(character);
                    break;
                default:
                    if (!char.IsControl(character))
                    {
                        output.Append(character);
                    }

                    break;
            }
        }

        return true;
    }

    private static void AppendAttribute(StringBuilder output, string name, string value)
    {
        output.Append(' ').Append(name).Append("=\"");
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    output.Append("&amp;");
                    break;
                case '<':
                    output.Append("&lt;");
                    break;
                case '>':
                    output.Append("&gt;");
                    break;
                case '"':
                    output.Append("&quot;");
                    break;
                default:
                    output.Append(character);
                    break;
            }
        }

        output.Append('"');
    }

    private static string DecodeEntities(string value)
    {
        if (!value.Contains('&'))
        {
            return value;
        }

        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '&')
            {
                decoded.Append(value[index]);
                continue;
            }

            var semicolon = -1;
            var limit = Math.Min(value.Length, index + 12);
            for (var cursor = index + 1; cursor < limit; cursor++)
            {
                if (value[cursor] == ';')
                {
                    semicolon = cursor;
                    break;
                }
            }

            if (semicolon < 0)
            {
                decoded.Append('&');
                continue;
            }

            var body = value.AsSpan(index + 1, semicolon - index - 1);
            if (body.Length == 0)
            {
                decoded.Append('&');
                continue;
            }

            if (body[0] == '#')
            {
                if (TryDecodeNumericEntity(body[1..], out var scalar))
                {
                    decoded.Append(char.ConvertFromUtf32(scalar));
                    index = semicolon;
                    continue;
                }

                decoded.Append('&');
                continue;
            }

            if (NamedEntities.TryGetValue(body.ToString(), out var named))
            {
                decoded.Append(named);
                index = semicolon;
                continue;
            }

            decoded.Append('&');
        }

        return decoded.ToString();
    }

    private static bool TryDecodeNumericEntity(ReadOnlySpan<char> body, out int scalar)
    {
        scalar = 0;
        if (body.Length == 0)
        {
            return false;
        }

        var hexadecimal = body[0] is 'x' or 'X';
        if (hexadecimal)
        {
            body = body[1..];
            if (body.Length == 0 || body.Length > 8)
            {
                return false;
            }

            foreach (var character in body)
            {
                if (!char.IsAsciiHexDigit(character))
                {
                    return false;
                }
            }

            if (!int.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out scalar))
            {
                return false;
            }
        }
        else
        {
            if (body.Length > 8 || body.ContainsAnyExcept("0123456789"))
            {
                return false;
            }

            if (!int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out scalar))
            {
                return false;
            }
        }

        return scalar is > 0 and <= 0x10ffff and not (>= 0xd800 and <= 0xdfff);
    }
}
