using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;

namespace Playnite.Steam;

/// <summary>A case-preserving KeyValues scalar or ordered object; offsets are UTF-8 byte offsets.</summary>
public sealed record KeyValue(string Key, string? Text, KeyValuesObject? Object, int ByteOffset);

/// <summary>Entries in file order, with exact-case lookup. Duplicate keys cannot be parsed.</summary>
public sealed class KeyValuesObject
{
    internal KeyValuesObject(List<KeyValue> entries)
    {
        Entries = entries.AsReadOnly();
        Fields = new ReadOnlyDictionary<string, KeyValue>(entries.ToDictionary(e => e.Key, StringComparer.Ordinal));
    }

    public IReadOnlyList<KeyValue> Entries { get; }
    public IReadOnlyDictionary<string, KeyValue> Fields { get; }
    public KeyValue? Find(string key) => Fields.GetValueOrDefault(key);
    public string? Text(string key) => Find(key)?.Text;
}

/// <summary>A rejected document, never a partial syntax tree. Message contains no scalar values.</summary>
public sealed class KeyValuesException : Exception
{
    internal KeyValuesException(string file, string reason, int offset, int line, string? key = null)
        : base($"{file}: {reason} at byte {offset}, line {line}" + (key is null ? string.Empty : $" (key {key})"))
    {
        File = file;
        Reason = reason;
        ByteOffset = offset;
        Line = line;
        Key = key;
    }

    public string File { get; }
    public string Reason { get; }
    public int ByteOffset { get; }
    public int Line { get; }
    public string? Key { get; }
}

/// <summary>Bounded, strict UTF-8 Valve text KeyValues. No disk, network or process access.</summary>
public static class KeyValues
{
    public const int DefaultMaxBytes = 65_536;
    public const int MaxDepth = 64;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static KeyValue Parse(ReadOnlySpan<byte> bytes, string file, int maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (bytes.Length > maxBytes)
        {
            throw new KeyValuesException(file, "size-limit", 0, 1);
        }

        try
        {
            _ = Utf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            var offset = Math.Max(0, exception.Index);
            throw new KeyValuesException(file, "invalid-utf8", offset, LineAt(bytes, offset));
        }

        // Validate controls even in comments, so skipped text cannot hide malformed input.
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if ((b < 32 && b is not (9 or 10 or 13)) || b == 127)
            {
                throw new KeyValuesException(file, "control-character", i, LineAt(bytes, i));
            }

            if (b == 13 && (i + 1 == bytes.Length || bytes[i + 1] != 10))
            {
                throw new KeyValuesException(file, "lone-cr", i, LineAt(bytes, i));
            }

            if (b == 0xC2 && i + 1 < bytes.Length && bytes[i + 1] is >= 0x80 and <= 0x9F)
            {
                throw new KeyValuesException(file, "control-character", i, LineAt(bytes, i));
            }
        }

        return new Reader(bytes, file).Read();
    }

    /// <summary>Canonical text, not byte-for-byte whitespace reproduction.</summary>
    public static byte[] Write(KeyValue root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var output = new StringBuilder();
        WriteEntry(root, output);
        return Utf8.GetBytes(output.ToString());
    }

    private static void WriteEntry(KeyValue entry, StringBuilder output)
    {
        Quote(entry.Key, output);
        output.Append(' ');
        if (entry.Object is { } obj)
        {
            output.Append("{\n");
            foreach (var child in obj.Entries)
            {
                WriteEntry(child, output);
            }

            output.Append("}\n");
        }
        else
        {
            Quote(entry.Text!, output);
            output.Append('\n');
        }
    }

    private static void Quote(string text, StringBuilder output)
    {
        output.Append('"');
        foreach (var c in text)
        {
            output.Append(c switch
            {
                '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\t' => "\\t", '\r' => "\\r", _ => c.ToString(),
            });
        }

        output.Append('"');
    }

    private static int LineAt(ReadOnlySpan<byte> bytes, int offset)
    {
        var line = 1;
        for (var i = 0; i < Math.Min(offset, bytes.Length); i++)
        {
            if (bytes[i] == 10)
            {
                line++;
            }
        }

        return line;
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes, string file)
    {
        private readonly ReadOnlySpan<byte> bytes = bytes;
        private readonly string file = file;
        private int position;

        public KeyValue Read()
        {
            if (bytes.StartsWith("\uFEFF"u8))
            {
                position = 3;
            }

            Skip();
            var offset = position;
            var key = Token();
            Skip();
            if (!Take((byte)'{'))
            {
                Fail("expected-root-object");
            }

            var root = new KeyValue(key, null, Object(1), offset);
            Skip();
            if (position != bytes.Length)
            {
                Fail("trailing-garbage");
            }

            return root;
        }

        private KeyValuesObject Object(int depth)
        {
            if (depth > MaxDepth)
            {
                Fail("depth-limit");
            }

            var entries = new List<KeyValue>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                Skip();
                if (Take((byte)'}'))
                {
                    return new KeyValuesObject(entries);
                }

                if (position == bytes.Length)
                {
                    Fail("unclosed-object");
                }

                var offset = position;
                var key = Token();
                if (!keys.Add(key))
                {
                    throw new KeyValuesException(file, "duplicate-key", offset, LineAt(bytes, offset), key);
                }

                Skip();
                entries.Add(Take((byte)'{')
                    ? new KeyValue(key, null, Object(depth + 1), offset)
                    : new KeyValue(key, Token(), null, offset));
            }
        }

        private string Token()
        {
            if (position == bytes.Length || bytes[position] is (byte)'{' or (byte)'}')
            {
                Fail("expected-token");
            }

            // A BOM is only legal once, at the very start of the file.
            if (bytes[position..].StartsWith("\uFEFF"u8))
            {
                Fail("unexpected-bom");
            }

            var quoted = Take((byte)'"');
            var output = new ArrayBufferWriter<byte>();
            while (position < bytes.Length)
            {
                var b = bytes[position];
                if (quoted && b == '"')
                {
                    position++;
                    return Utf8.GetString(output.WrittenSpan);
                }

                if (!quoted && (b is 9 or 10 or 13 or 32 or (byte)'{' or (byte)'}' or (byte)'"' || Comment()))
                {
                    break;
                }

                if (quoted && b is 10 or 13)
                {
                    Fail("newline-in-string");
                }

                if (b == '\\')
                {
                    position++;
                    if (position == bytes.Length)
                    {
                        Fail("incomplete-escape");
                    }

                    b = bytes[position] switch
                    {
                        (byte)'\\' => (byte)'\\', (byte)'"' => (byte)'"', (byte)'n' => 10,
                        (byte)'t' => 9, (byte)'r' => 13, _ => 0,
                    };
                    if (b == 0)
                    {
                        Fail("unknown-escape");
                    }
                }

                output.GetSpan(1)[0] = b;
                output.Advance(1);
                position++;
            }

            if (quoted)
            {
                Fail("unclosed-string");
            }

            if (output.WrittenCount == 0)
            {
                Fail("expected-token");
            }

            return Utf8.GetString(output.WrittenSpan);
        }

        private bool Take(byte b)
        {
            if (position < bytes.Length && bytes[position] == b)
            {
                position++;
                return true;
            }

            return false;
        }

        private bool Comment() => position + 1 < bytes.Length && bytes[position] == '/' && bytes[position + 1] == '/';

        private void Skip()
        {
            while (position < bytes.Length)
            {
                if (bytes[position] is 9 or 10 or 13 or 32)
                {
                    position++;
                }
                else if (Comment())
                {
                    while (position < bytes.Length && bytes[position] != 10)
                    {
                        position++;
                    }
                }
                else
                {
                    break;
                }
            }
        }

        private void Fail(string reason) => throw new KeyValuesException(file, reason, position, LineAt(bytes, position));
    }
}
