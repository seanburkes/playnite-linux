using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Playnite.Core.Serialization;
using Playnite.Steam;

namespace Playnite.Fuzz;

/// <summary>
/// Library-folder discovery target. Mutated synthetic `libraryfolders.vdf` bytes must produce a
/// bounded parser result or a clean rejection, never an exception outside the contract and never
/// a hang. Uses the existing mutator, timeout classifier and budget policy.
/// </summary>
public static class SteamDiscoveryFuzzTarget
{
    public static IReadOnlyList<FuzzSeed> Load(string root)
    {
        var seeds = new List<FuzzSeed>();
        foreach (var line in File.ReadAllLines(Path.Combine(root, "SHA256SUMS")))
        {
            var parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2 || parts[0].Length != 64 || Path.GetFileName(parts[1]) != parts[1]
                || Path.GetExtension(parts[1]) != ".vdf")
            {
                throw new InvalidOperationException("Invalid discovery corpus manifest.");
            }

            var bytes = File.ReadAllBytes(Path.Combine(root, parts[1]));
            if (!string.Equals(Convert.ToHexString(SHA256Hash(bytes)), parts[0], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Discovery corpus drift: {parts[1]}");
            }

            seeds.Add(new("libraryfolders", parts[1], bytes));
        }

        var files = Directory.EnumerateFiles(root, "*.vdf").Select(Path.GetFileName).Order(StringComparer.Ordinal);
        if (seeds.Count == 0 || !files.SequenceEqual(seeds.Select(s => s.RelativePath).Order(StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("Discovery corpus manifest/file set mismatch.");
        }

        return seeds.AsReadOnly();
    }

    /// <summary>Parse one mutated document and assert the discovery-facing parser invariants.</summary>
    public static object Parse(byte[] bytes, int maxBytes)
    {
        var result = SteamLibraryParser.Parse(bytes, "libraryfolders.vdf", maxBytes);
        if (result.Value is null)
        {
            throw new SerializationException("libraryfolders", result.Issues.Count == 0 ? "rejected" : result.Issues[0].Reason);
        }

        foreach (var library in result.Value.Libraries)
        {
            if (!Path.IsPathFullyQualified(library.Path))
            {
                throw new FuzzFailureException("A returned library path is not absolute.");
            }
        }

        if (result.Value.UnknownFields.Any(field => string.Equals(field.Key, "LastOwner", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FuzzFailureException("A private LastOwner field reached the unknown root bag.");
        }

        return result.Value;
    }

    public static FuzzRunResult Run(FuzzOptions options, TextWriter output)
    {
        options.Validate();
        if (options.MaxInputBytes >= Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The oversized probe must fit in a byte array.");
        }

        var seeds = Load(options.CorpusRoot);
        var digest = FuzzCorpus.Digest(seeds);
        var mutator = new FuzzMutator(options.Seed);
        output.WriteLine($"STEAM DISCOVERY FUZZ START seed=0x{options.Seed:X16} corpus_sha256={digest} seconds={options.Seconds} iterations={options.Iterations}");
        var watch = Stopwatch.StartNew();
        long cases = 0, clean = 0, rejected = 0;
        var peak = GC.GetTotalMemory(forceFullCollection: true);
        CheckMemory();
        while (cases < options.Iterations && watch.Elapsed < TimeSpan.FromSeconds(options.Seconds))
        {
            var seed = seeds[(int)(cases % seeds.Count)];
            var mustReject = cases % 97 == 0 || cases % 193 == 0 || cases % 389 == 0;
            var bytes = cases % 97 == 0 ? new byte[] { 0xFF, 0xFE, 0xC0, 0x80 }
                : cases % 193 == 0 ? Encoding.UTF8.GetBytes("libraryfolders {" + string.Concat(Enumerable.Repeat("a {", 512)) + new string('}', 513))
                : cases % 389 == 0 ? new byte[options.MaxInputBytes + 1]
                : mutator.Next(seed.Bytes);
            var result = FuzzExecution.Execute("libraryfolders", _ =>
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    return Parse(bytes, options.MaxInputBytes);
                }
                finally
                {
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (allocated > 2_097_152L + 128L * options.MaxInputBytes)
                    {
                        throw new FuzzFailureException($"Discovery case allocated {allocated} bytes.");
                    }
                }
            }, options.CaseTimeoutMilliseconds);
            cases++;
            if (result.IsFailure || (mustReject && result.Status != FuzzCaseStatus.Rejected))
            {
                throw new FuzzFailureException($"Discovery case {cases}: {result.Status}: {result.Detail}",
                    $"seed={seed.RelativePath} input-base64={Convert.ToBase64String(bytes)}");
            }

            if (result.Status == FuzzCaseStatus.Clean) { clean++; } else { rejected++; }
            if (cases % 1000 == 0)
            {
                CheckMemory();
            }
        }

        CheckMemory();
        var budget = watch.Elapsed >= TimeSpan.FromSeconds(options.Seconds) ? "wall-clock" : "iterations";
        var run = new FuzzRunResult(cases, clean, rejected, watch.ElapsedMilliseconds, peak, digest, budget);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"STEAM DISCOVERY FUZZ COMPLETE cases={cases} clean={clean} rejected={rejected} elapsed_ms={run.ElapsedMilliseconds} peak_managed_bytes={peak} corpus_sha256={digest} budget={budget}"));
        return run;

        void CheckMemory()
        {
            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
            if (peak > options.MaxManagedBytes)
            {
                throw new FuzzFailureException($"Discovery managed heap exceeds {options.MaxManagedBytes} bytes.");
            }
        }
    }

    private static byte[] SHA256Hash(byte[] bytes) => SHA256.HashData(bytes);
}
