using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Playnite.Core.Serialization;
using Playnite.Steam;

namespace Playnite.Fuzz;

/// <summary>
/// Byte-preserving VDF/ACF target using the existing mutator, timeout classifier and budget policy.
/// Run by the unfiltered SteamParserTests suite in linux-ci; seeds are synthetic, hash-pinned files.
/// </summary>
public static class SteamFuzzTarget
{
    public static IReadOnlyList<FuzzSeed> Load(string root)
    {
        var seeds = new List<FuzzSeed>();
        foreach (var line in File.ReadAllLines(Path.Combine(root, "manifest.sha256")))
        {
            var parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2 || parts[0].Length != 64 || Path.GetFileName(parts[1]) != parts[1]
                || Path.GetExtension(parts[1]) is not (".acf" or ".vdf"))
            {
                throw new InvalidOperationException("Invalid Steam corpus manifest.");
            }

            var bytes = File.ReadAllBytes(Path.Combine(root, parts[1]));
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), parts[0], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Steam corpus drift: {parts[1]}");
            }

            seeds.Add(new(Path.GetExtension(parts[1])[1..], parts[1], bytes));
        }

        var files = Directory.EnumerateFiles(root).Where(p => Path.GetExtension(p) is ".acf" or ".vdf")
            .Select(Path.GetFileName).Order(StringComparer.Ordinal);
        if (seeds.Count == 0 || !files.SequenceEqual(seeds.Select(s => s.RelativePath).Order(StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("Steam corpus manifest/file set mismatch.");
        }

        return seeds.AsReadOnly();
    }

    /// <summary>Parse the exact mutated bytes; malformed UTF-8 must reach the strict reader unchanged.</summary>
    public static object Parse(byte[] bytes, int maxBytes)
    {
        try
        {
            var root = KeyValues.Parse(bytes, "fuzz.vdf", maxBytes);
            _ = SteamLibraryParser.Parse(bytes, "libraryfolders.vdf", maxBytes);
            _ = SteamManifestParser.Parse(bytes, "appmanifest_42.acf", maxBytes);
            _ = SteamManifestParser.Parse(bytes, "appmanifest_43.acf", maxBytes);
            return root;
        }
        catch (KeyValuesException exception)
        {
            throw new SerializationException("VDF/ACF", exception.Reason, exception);
        }
    }

    public static FuzzRunResult Run(FuzzOptions options, TextWriter output)
    {
        options.Validate();
        var seeds = Load(options.CorpusRoot);
        var digest = FuzzCorpus.Digest(seeds);
        var mutator = new FuzzMutator(options.Seed);
        output.WriteLine($"STEAM FUZZ START seed=0x{options.Seed:X16} corpus_sha256={digest} seconds={options.Seconds} iterations={options.Iterations}");
        var watch = Stopwatch.StartNew();
        long cases = 0, clean = 0, rejected = 0;
        var peak = GC.GetTotalMemory(forceFullCollection: true);
        CheckMemory();
        while (cases < options.Iterations && watch.Elapsed < TimeSpan.FromSeconds(options.Seconds))
        {
            var seed = seeds[(int)(cases % seeds.Count)];
            var mustReject = cases % 97 == 0 || cases % 193 == 0 || cases % 389 == 0;
            var bytes = cases % 97 == 0 ? new byte[] { 0xFF, 0xFE, 0xC0, 0x80 }
                : cases % 193 == 0 ? Encoding.UTF8.GetBytes("root {" + string.Concat(Enumerable.Repeat("a {", 512)) + new string('}', 513))
                : cases % 389 == 0 ? new byte[options.MaxInputBytes + 1]
                : mutator.Next(seed.Bytes);
            var result = FuzzExecution.Execute("byte-input", _ =>
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    return Parse(bytes, options.MaxInputBytes);
                }
                finally
                {
                    // A transient allocation guard complements the harness's retained-heap ceiling.
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (allocated > 2_097_152L + 128L * options.MaxInputBytes)
                    {
                        throw new FuzzFailureException($"Steam case allocated {allocated} bytes.");
                    }
                }
            }, options.CaseTimeoutMilliseconds);
            cases++;
            if (result.IsFailure || (mustReject && result.Status != FuzzCaseStatus.Rejected))
            {
                throw new FuzzFailureException($"Steam case {cases}: {result.Status}: {result.Detail}",
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
            $"STEAM FUZZ COMPLETE cases={cases} clean={clean} rejected={rejected} elapsed_ms={run.ElapsedMilliseconds} peak_managed_bytes={peak} corpus_sha256={digest} budget={budget}"));
        return run;

        void CheckMemory()
        {
            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
            if (peak > options.MaxManagedBytes)
            {
                throw new FuzzFailureException($"Steam managed heap exceeds {options.MaxManagedBytes} bytes.");
            }
        }
    }
}
