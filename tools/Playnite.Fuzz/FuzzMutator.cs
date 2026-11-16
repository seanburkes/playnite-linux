namespace Playnite.Fuzz;

/// <summary>
/// Deterministic xorshift mutation stream over the committed corpus.
/// </summary>
/// <remarks>
/// The generator is the same shape the serialisation unit's bounded fuzz test used: a fixed
/// 64-bit state, no clock, environment or collection-order input, and a bounded output length.
/// A failure can therefore be replayed from the seed and the case index without keeping CI
/// state. Mutations stay within <see cref="MaximumLength"/> so a single case cannot turn into
/// an unbounded allocation before the parser's own size limit sees it.
/// </remarks>
public sealed class FuzzMutator
{
    /// <summary>Maximum bytes in one mutated document.</summary>
    public const int MaximumLength = 8 * 1024;

    private ulong state;

    /// <summary>Creates a generator from a 64-bit seed.</summary>
    /// <param name="seed">Seed; zero is replaced with the default constant.</param>
    public FuzzMutator(ulong seed)
    {
        state = seed == 0 ? 0x5EED_1A2B_003C_004DUL : seed;
    }

    /// <summary>Produces the next mutated document for a corpus seed.</summary>
    /// <param name="seed">Base document bytes.</param>
    /// <returns>A new byte array; the input is never modified.</returns>
    public byte[] Next(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var data = (byte[])seed.Clone();
        var operations = 1 + (int)(NextState() % 4);

        for (var operation = 0; operation < operations; operation++)
        {
            if (data.Length == 0)
            {
                data = [RandomByte()];
                continue;
            }

            switch (NextState() % 6)
            {
                case 0:
                    data[(int)(NextState() % (ulong)data.Length)] ^= (byte)(1 << (int)(NextState() % 8));
                    break;
                case 1:
                    data[(int)(NextState() % (ulong)data.Length)] = RandomByte();
                    break;
                case 2:
                    var removeAt = (int)(NextState() % (ulong)data.Length);
                    var removeCount = Math.Min(1 + (int)(NextState() % 8), data.Length - removeAt);
                    data = [.. data[..removeAt], .. data[(removeAt + removeCount)..]];
                    break;
                case 3:
                    Insert(ref data);
                    break;
                case 4:
                    data = data[..(int)(NextState() % (ulong)data.Length)];
                    break;
                default:
                    var copyAt = (int)(NextState() % (ulong)data.Length);
                    var copyCount = Math.Min(1 + (int)(NextState() % 8), data.Length - copyAt);
                    Insert(ref data, data[copyAt..(copyAt + copyCount)]);
                    break;
            }

            if (data.Length > MaximumLength)
            {
                data = data[..MaximumLength];
            }
        }

        return data;
    }

    /// <summary>Generates a reproducible sequence for tests and reproduction tooling.</summary>
    /// <param name="seed">Base document bytes.</param>
    /// <param name="seedValue">Generator seed.</param>
    /// <param name="count">Number of documents to generate.</param>
    /// <returns>Base64 encodings of the generated documents, in order.</returns>
    public static IReadOnlyList<string> Sequence(byte[] seed, ulong seedValue, int count)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var mutator = new FuzzMutator(seedValue);
        var result = new string[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = Convert.ToBase64String(mutator.Next(seed));
        }

        return result;
    }

    private void Insert(ref byte[] data, byte[]? slice = null)
    {
        slice ??= [RandomByte(), RandomByte(), RandomByte()];
        var at = (int)(NextState() % (ulong)(data.Length + 1));
        data = [.. data[..at], .. slice, .. data[at..]];
    }

    private byte RandomByte()
    {
        ReadOnlySpan<byte> alphabet = "{}[]:=,-\"'\\\n\r\t 0123456789abcdefnul~!&*"u8;
        return alphabet[(int)(NextState() % (ulong)alphabet.Length)];
    }

    private ulong NextState()
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return state * 2685821657736338717UL;
    }
}
