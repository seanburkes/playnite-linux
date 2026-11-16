using System.Diagnostics;
using System.Globalization;

namespace Playnite.Fuzz;

/// <summary>Configuration for one bounded fuzz run.</summary>
public sealed record FuzzOptions
{
    /// <summary>Gets the committed corpus root.</summary>
    public string CorpusRoot { get; init; } = string.Empty;

    /// <summary>Gets the deterministic mutation seed.</summary>
    public ulong Seed { get; init; } = 0x5EED_1A2B_003C_004D;

    /// <summary>Gets the wall-clock budget in seconds.</summary>
    public int Seconds { get; init; } = 120;

    /// <summary>Gets the iteration budget; the run stops at whichever budget is reached first.</summary>
    public long Iterations { get; init; } = 50_000_000;

    /// <summary>Gets the per-case timeout in milliseconds.</summary>
    public int CaseTimeoutMilliseconds { get; init; } = 2_000;

    /// <summary>Gets the maximum accepted document size, also measuring the limit path.</summary>
    public int MaxInputBytes { get; init; } = Playnite.Core.Serialization.SerializationLimits.DefaultMaxDocumentBytes;

    /// <summary>Gets the managed-heap ceiling in bytes.</summary>
    public long MaxManagedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Validates the configuration before a run.</summary>
    /// <exception cref="ArgumentException">A value is out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CorpusRoot))
        {
            throw new ArgumentException("A corpus root is required.", nameof(CorpusRoot));
        }

        if (Seconds <= 0)
        {
            throw new ArgumentException("The wall-clock budget must be positive.", nameof(Seconds));
        }

        if (Iterations <= 0)
        {
            throw new ArgumentException("The iteration budget must be positive.", nameof(Iterations));
        }

        if (CaseTimeoutMilliseconds <= 0)
        {
            throw new ArgumentException("The case timeout must be positive.", nameof(CaseTimeoutMilliseconds));
        }

        if (MaxInputBytes <= 0)
        {
            throw new ArgumentException("The input limit must be positive.", nameof(MaxInputBytes));
        }

        if (MaxManagedBytes <= 0)
        {
            throw new ArgumentException("The memory limit must be positive.", nameof(MaxManagedBytes));
        }
    }
}

/// <summary>The evidence a completed bounded run reports.</summary>
/// <param name="Cases">Cases executed.</param>
/// <param name="Clean">Cases the parser accepted.</param>
/// <param name="Rejected">Cases the parser rejected with a serialisation exception.</param>
/// <param name="ElapsedMilliseconds">Measured wall-clock time.</param>
/// <param name="PeakManagedBytes">Highest managed heap observed after a full collection.</param>
/// <param name="CorpusDigest">Stable digest of the verified corpus.</param>
/// <param name="Budget">Which budget stopped the run: wall-clock or iterations.</param>
public sealed record FuzzRunResult(
    long Cases,
    long Clean,
    long Rejected,
    long ElapsedMilliseconds,
    long PeakManagedBytes,
    string CorpusDigest,
    string Budget);

/// <summary>A failure the target must surface with enough detail to replay it.</summary>
public sealed class FuzzFailureException : Exception
{
    /// <summary>Creates a failure.</summary>
    /// <param name="message">One-line description.</param>
    /// <param name="reproduction">Exact reproduction material, or null when a probe identifies it.</param>
    public FuzzFailureException(string message, string? reproduction = null)
        : base(message)
    {
        Reproduction = reproduction;
    }

    /// <summary>Gets exact reproduction material, or null.</summary>
    public string? Reproduction { get; }
}

/// <summary>
/// Runs the bounded, deterministic fuzz target over the four parser entry points.
/// </summary>
/// <remarks>
/// The loop stops only at a configured budget, and prints the completion marker after the
/// final memory check. A crash, unhandled parser exception, case timeout, corpus drift or
/// memory ceiling throws <see cref="FuzzFailureException"/> and no completion marker is
/// printed, so the CI step cannot mistake a partial run for a pass.
/// </remarks>
public static class FuzzRunner
{
    private const int MemoryCheckInterval = 1_000;

    /// <summary>Runs the target.</summary>
    /// <param name="options">Validated run configuration.</param>
    /// <param name="output">Progress and completion sink.</param>
    /// <returns>Evidence from the completed run.</returns>
    /// <exception cref="FuzzFailureException">A case or the run failed.</exception>
    public static FuzzRunResult Run(FuzzOptions options, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        options.Validate();

        var seeds = FuzzCorpus.Load(options.CorpusRoot);
        var digest = FuzzCorpus.Digest(seeds);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"FUZZ START seed=0x{options.Seed:X16} seconds={options.Seconds} iterations={options.Iterations} " +
            $"case_timeout_ms={options.CaseTimeoutMilliseconds} max_input_bytes={options.MaxInputBytes} " +
            $"max_managed_bytes={options.MaxManagedBytes} corpus_seeds={seeds.Count} corpus_sha256={digest}"));

        var mutator = new FuzzMutator(options.Seed);
        var stopwatch = Stopwatch.StartNew();
        var wallClock = TimeSpan.FromSeconds(options.Seconds);
        long cases = 0;
        long clean = 0;
        long rejected = 0;
        var peak = GC.GetTotalMemory(forceFullCollection: true);
        CheckMemory(peak, options, "before the run");

        while (cases < options.Iterations && stopwatch.Elapsed < wallClock)
        {
            var format = FuzzParsers.Formats[(int)(cases % FuzzParsers.Formats.Length)];
            var result = ExecuteCase(cases, format, seeds, mutator, options, out var repro);
            cases++;

            if (result.Status == FuzzCaseStatus.Clean)
            {
                clean++;
            }
            else if (result.Status == FuzzCaseStatus.Rejected)
            {
                rejected++;
            }

            if (result.IsFailure)
            {
                throw new FuzzFailureException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"case {cases} format={format} status={result.Status}: {result.Detail}"),
                    repro);
            }

            if (cases % MemoryCheckInterval == 0)
            {
                peak = Math.Max(peak, CheckMemory(peak, options, $"after {cases} cases"));
            }
        }

        stopwatch.Stop();
        peak = Math.Max(peak, CheckMemory(peak, options, "at completion"));
        var budget = stopwatch.Elapsed >= wallClock ? "wall-clock" : "iterations";
        var elapsed = stopwatch.ElapsedMilliseconds;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"FUZZ COMPLETE seed=0x{options.Seed:X16} cases={cases} clean={clean} rejected={rejected} " +
            $"elapsed_ms={elapsed} peak_managed_bytes={peak} corpus_sha256={digest} budget={budget}"));

        return new FuzzRunResult(cases, clean, rejected, elapsed, peak, digest, budget);
    }

    private static FuzzCaseResult ExecuteCase(
        long index,
        string format,
        IReadOnlyList<FuzzSeed> seeds,
        FuzzMutator mutator,
        FuzzOptions options,
        out string? reproduction)
    {
        // Structured probes are deterministic and exercise the guards a random mutation is
        // unlikely to reach: a malformed document, a naive-parser stack overflow, and the
        // byte limit itself.
        if (index % 97 == 0)
        {
            var document = FuzzParsers.MalformedProbe(format);
            reproduction = $"probe=malformed format={format} bytes={FuzzParsers.ByteCount(document)} " +
                $"input-base64={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(document))}";
            return ExpectRejected(format, document, options, reproduction);
        }

        if (index % 193 == 0)
        {
            var document = FuzzParsers.DeepNestingProbe(format);
            reproduction = $"probe=deep-nesting format={format} depth=512";
            return ExpectRejected(format, document, options, reproduction);
        }

        if (index % 389 == 0)
        {
            var document = FuzzParsers.OversizedProbe(options.MaxInputBytes);
            reproduction = $"probe=oversized format={format} max_input_bytes={options.MaxInputBytes}";
            return ExpectRejected(format, document, options, reproduction);
        }

        var seed = seeds[(int)(index % seeds.Count)];
        var bytes = mutator.Next(seed.Bytes);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        reproduction = string.Create(
            CultureInfo.InvariantCulture,
            $"corpus_seed={seed.RelativePath} input_bytes={bytes.Length} input-base64={Convert.ToBase64String(bytes)}");
        return FuzzExecution.Execute(
            text,
            document => FuzzParsers.Parse(format, document, options.MaxInputBytes),
            options.CaseTimeoutMilliseconds);
    }

    private static FuzzCaseResult ExpectRejected(
        string format,
        string document,
        FuzzOptions options,
        string reproduction)
    {
        var result = FuzzExecution.Execute(
            document,
            input => FuzzParsers.Parse(format, input, options.MaxInputBytes),
            options.CaseTimeoutMilliseconds);
        if (result.Status == FuzzCaseStatus.Rejected)
        {
            return result;
        }

        return new FuzzCaseResult(
            result.Status == FuzzCaseStatus.Clean ? FuzzCaseStatus.UnhandledException : result.Status,
            $"structured probe was not rejected cleanly: expected Rejected, got {result.Status}" +
            (result.Detail is null ? string.Empty : $" ({result.Detail})") +
            $"; {reproduction}");
    }

    private static long CheckMemory(long peak, FuzzOptions options, string when)
    {
        var current = GC.GetTotalMemory(forceFullCollection: true);
        peak = Math.Max(peak, current);
        if (current > options.MaxManagedBytes)
        {
            throw new FuzzFailureException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"managed heap reached {current} bytes {when}, over the {options.MaxManagedBytes}-byte limit"));
        }

        return peak;
    }
}
