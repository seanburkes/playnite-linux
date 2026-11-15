using Xunit;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Playnite.Core.Serialization;

namespace Playnite.Core.Tests.Serialization;

/// <summary>
/// Bounded, deterministic mutation fuzzing of the four parser entry points.
/// </summary>
/// <remarks>
/// The corpus under <c>tests/fixtures/serialization-fuzz</c> is committed and hash-pinned, the
/// mutation stream is a fixed-seed xorshift generator, and every mutated document must either
/// parse or throw <see cref="SerializationException"/>. A failure names the seed file, the
/// mutation index and the exact input as base64, so a CI failure is reproducible from this
/// repository alone. The run has both a fixed iteration budget and a wall-clock budget; overrun
/// fails the test instead of hanging CI.
/// </remarks>
public sealed class SerializationFuzzTests
{
    private const int MutationsPerSeed = 64;
    private const int MutationSeed = 0x5EED_003C;
    private const int InputByteBudget = 64 * 1024;
    private const int WallClockBudgetMilliseconds = 20_000;
    private const int MaximumMutatedLength = 8 * 1024;

    private static readonly string CorpusRoot = RepositoryPaths.Combine("tests", "fixtures", "serialization-fuzz");

    [Fact]
    public void Mutated_documents_either_parse_or_raise_a_serialization_exception_within_the_budget()
    {
        var corpus = LoadCorpus();
        Assert.NotEmpty(corpus);

        var failures = new List<string>();
        var mutations = 0;
        var stopwatch = Stopwatch.StartNew();
        var state = (ulong)MutationSeed;

        foreach (var seed in corpus)
        {
            for (var mutation = 0; mutation < MutationsPerSeed; mutation++)
            {
                var bytes = Mutate(seed.Bytes, ref state);
                var text = Encoding.UTF8.GetString(bytes);
                mutations++;

                try
                {
                    Parse(seed.Format, text);
                }
                catch (SerializationException)
                {
                    // A rejected document is the expected outcome for most mutations.
                }
                catch (Exception exception)
                {
                    failures.Add(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{seed.RelativePath} mutation {mutation}: {exception.GetType().Name}: {exception.Message}\n" +
                            $"input-base64: {Convert.ToBase64String(bytes)}"));
                }
            }
        }

        stopwatch.Stop();

        Assert.True(
            failures.Count == 0,
            $"The parsers threw a non-serialisation exception for {failures.Count} mutated document(s):\n\n" +
            string.Join("\n\n", failures.Take(5)));
        Assert.True(
            stopwatch.ElapsedMilliseconds < WallClockBudgetMilliseconds,
            $"Fuzzing {mutations} mutated documents took {stopwatch.ElapsedMilliseconds} ms, " +
            $"over the {WallClockBudgetMilliseconds} ms budget.");
    }

    [Fact]
    public void The_mutation_stream_is_reproducible_from_the_recorded_seed()
    {
        var seed = Encoding.UTF8.GetBytes("""{"Count":1,"Name":"héllo"}""");

        var first = Generate(seed, 16).Select(Convert.ToBase64String).ToArray();
        var second = Generate(seed, 16).Select(Convert.ToBase64String).ToArray();

        Assert.Equal(first, second);
        Assert.Contains(first, candidate => candidate != Convert.ToBase64String(seed));
    }

    [Fact]
    public void The_corpus_matches_the_recorded_manifest()
    {
        var manifestPath = Path.Combine(CorpusRoot, "manifest.sha256");
        var manifest = File.ReadAllLines(manifestPath)
            .Where(line => line.Length > 0)
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToDictionary(parts => parts[1].Replace('\\', '/'), parts => parts[0], StringComparer.Ordinal);

        var files = Directory.EnumerateFiles(CorpusRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("manifest.sha256", StringComparison.Ordinal)
                && !path.EndsWith("README.md", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(CorpusRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

        var recorded = files.ToArray();
        Assert.Equal(manifest.Keys.Order(StringComparer.Ordinal), recorded);
        foreach (var relative in recorded)
        {
            Assert.Equal(manifest[relative], RepositoryPaths.Sha256(Path.Combine(CorpusRoot, relative)));
        }
    }

    private static List<Seed> LoadCorpus()
    {
        var seeds = new List<Seed>();
        foreach (var format in new[] { "json", "yaml", "toml", "ini" })
        {
            var directory = Path.Combine(CorpusRoot, format);
            foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                seeds.Add(new Seed(
                    format,
                    Path.GetRelativePath(CorpusRoot, path).Replace('\\', '/'),
                    File.ReadAllBytes(path)));
            }
        }

        return seeds;
    }

    private static void Parse(string format, string text)
    {
        switch (format)
        {
            case "json":
                JsonSerialization.FromJson<SerializationSample>(text, InputByteBudget);
                break;
            case "yaml":
                YamlSerialization.FromYaml<SerializationSample>(text, InputByteBudget);
                break;
            case "toml":
                TomlSerialization.FromToml<TomlSample>(text, InputByteBudget);
                break;
            case "ini":
                IniParser.Parse(text, InputByteBudget);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown fuzz format.");
        }
    }

    private static IEnumerable<byte[]> Generate(byte[] seed, int count)
    {
        var state = (ulong)MutationSeed;
        for (var index = 0; index < count; index++)
        {
            yield return Mutate(seed, ref state);
        }
    }

    private static byte[] Mutate(byte[] seed, ref ulong state)
    {
        var data = (byte[])seed.Clone();
        var operations = 1 + (int)(Next(ref state) % 4);

        for (var operation = 0; operation < operations; operation++)
        {
            if (data.Length == 0)
            {
                data = [RandomByte(ref state)];
                continue;
            }

            switch (Next(ref state) % 6)
            {
                case 0:
                    data[(int)(Next(ref state) % (ulong)data.Length)] ^= (byte)(1 << (int)(Next(ref state) % 8));
                    break;
                case 1:
                    data[(int)(Next(ref state) % (ulong)data.Length)] = RandomByte(ref state);
                    break;
                case 2:
                    var removeAt = (int)(Next(ref state) % (ulong)data.Length);
                    var removeCount = Math.Min(1 + (int)(Next(ref state) % 8), data.Length - removeAt);
                    data = [.. data[..removeAt], .. data[(removeAt + removeCount)..]];
                    break;
                case 3:
                    Insert(ref data, next: ref state);
                    break;
                case 4:
                    data = data[..(int)(Next(ref state) % (ulong)data.Length)];
                    break;
                default:
                    var copyAt = (int)(Next(ref state) % (ulong)data.Length);
                    var copyCount = Math.Min(1 + (int)(Next(ref state) % 8), data.Length - copyAt);
                    Insert(ref data, next: ref state, data[copyAt..(copyAt + copyCount)]);
                    break;
            }

            if (data.Length > MaximumMutatedLength)
            {
                data = data[..MaximumMutatedLength];
            }
        }

        return data;
    }

    private static void Insert(ref byte[] data, ref ulong next, byte[]? slice = null)
    {
        slice ??= new[] { RandomByte(ref next), RandomByte(ref next), RandomByte(ref next) };
        var at = (int)(Next(ref next) % (ulong)(data.Length + 1));
        data = [.. data[..at], .. slice, .. data[at..]];
    }

    private static byte RandomByte(ref ulong state)
    {
        ReadOnlySpan<byte> alphabet = "{}[]:=,-\"'\\\n\r\t 0123456789abcdefnul~!&*"u8;
        return alphabet[(int)(Next(ref state) % (ulong)alphabet.Length)];
    }

    private static ulong Next(ref ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return state * 2685821657736338717UL;
    }

    private sealed record Seed(string Format, string RelativePath, byte[] Bytes);
}
