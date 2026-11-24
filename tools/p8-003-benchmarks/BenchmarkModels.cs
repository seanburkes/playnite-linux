using System.Text.Json;
using System.Text.Json.Serialization;

namespace Playnite.Benchmarks;

/// <summary>Execution mode for the p8-003 benchmark and soak harness.</summary>
public enum BenchmarkMode
{
    All,
    Bench,
    Soak,
}

/// <summary>Configuration for deterministic benchmark and soak runs.</summary>
public sealed record BenchmarkOptions
{
    public BenchmarkMode Mode { get; init; } = BenchmarkMode.All;
    public int LibrarySize { get; init; } = 1000;
    public int QueryIterations { get; init; } = 25;
    public int SoakCycles { get; init; } = 10;
    public int SessionCycles { get; init; } = 25;
    public int RealProcessCycles { get; init; } = 6;
    public int ArtworkIterations { get; init; } = 100;
    public double WarnThresholdRatio { get; init; } = 0.10;
    public double FailThresholdRatio { get; init; } = 0.20;
    public double AbsoluteFloorMs { get; init; } = 0.25;
    public uint Seed { get; init; } = 0x5038_0003;
    public string? BaselinePath { get; init; }
    public string? OutputPath { get; init; }

    /// <summary>Rejects vacuous budgets and invalid threshold configurations.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentException($"Unknown benchmark mode: {Mode}.", nameof(Mode));
        }

        if (LibrarySize < 100)
        {
            throw new ArgumentException("Library size must be at least 100 games.", nameof(LibrarySize));
        }

        if (QueryIterations < 5)
        {
            throw new ArgumentException("Query iterations must be at least 5.", nameof(QueryIterations));
        }

        if (SoakCycles < 3)
        {
            throw new ArgumentException("Soak cycles must be at least 3 so warm-up and steady halves exist.", nameof(SoakCycles));
        }

        if (SessionCycles < 5)
        {
            throw new ArgumentException("Session cycles must be at least 5.", nameof(SessionCycles));
        }

        if (RealProcessCycles != 0 && RealProcessCycles < 3)
        {
            throw new ArgumentException("Real process cycles must be 0 or at least 3 so warm-up and steady halves exist.", nameof(RealProcessCycles));
        }

        if (ArtworkIterations < 10)
        {
            throw new ArgumentException("Artwork iterations must be at least 10.", nameof(ArtworkIterations));
        }

        if (!double.IsFinite(WarnThresholdRatio) || WarnThresholdRatio < 0)
        {
            throw new ArgumentException("Warn threshold ratio must be finite and non-negative.", nameof(WarnThresholdRatio));
        }

        if (!double.IsFinite(FailThresholdRatio) || FailThresholdRatio < WarnThresholdRatio)
        {
            throw new ArgumentException("Fail threshold ratio must be finite and at least warn threshold.", nameof(FailThresholdRatio));
        }

        if (!double.IsFinite(AbsoluteFloorMs) || AbsoluteFloorMs < 0)
        {
            throw new ArgumentException("Absolute floor milliseconds must be finite and non-negative.", nameof(AbsoluteFloorMs));
        }

        if (BaselinePath is not null && string.IsNullOrWhiteSpace(BaselinePath))
        {
            throw new ArgumentException("Baseline path cannot be empty or whitespace.", nameof(BaselinePath));
        }

        if (OutputPath is not null && string.IsNullOrWhiteSpace(OutputPath))
        {
            throw new ArgumentException("Output path cannot be empty or whitespace.", nameof(OutputPath));
        }
    }
}

/// <summary>Summary statistics for a measured benchmark or memory metric.</summary>
public sealed record MetricSummary(
    string Name,
    string Unit,
    string GateStatistic,
    double Median,
    double P95,
    double Min,
    double Max,
    int Samples)
{
    /// <summary>The statistic compared against a baseline under the tolerance gate.</summary>
    [JsonIgnore]
    public double GateValue => string.Equals(GateStatistic, "p95", StringComparison.Ordinal) ? P95 : Median;
}

/// <summary>Computes exact distribution summaries from finite numeric samples.</summary>
public static class PercentileCalculator
{
    public static MetricSummary Summarize(
        string name,
        string unit,
        string gateStatistic,
        IReadOnlyList<double> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        if (gateStatistic is not ("median" or "p95"))
        {
            throw new ArgumentException("Gate statistic must be 'median' or 'p95'.", nameof(gateStatistic));
        }

        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        }

        var sorted = new double[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            var value = samples[i];
            if (!double.IsFinite(value))
            {
                throw new ArgumentException($"Sample {i} is not finite.", nameof(samples));
            }

            sorted[i] = value;
        }

        Array.Sort(sorted);
        var count = sorted.Length;
        var median = (count & 1) == 1
            ? sorted[count / 2]
            : (sorted[(count / 2) - 1] + sorted[count / 2]) / 2.0;
        var p95Index = Math.Clamp((int)Math.Ceiling(0.95 * count) - 1, 0, count - 1);
        var p95 = sorted[p95Index];

        return new MetricSummary(
            name,
            unit,
            gateStatistic,
            median,
            p95,
            sorted[0],
            sorted[^1],
            count);
    }
}

/// <summary>One resource observation captured during a soak loop.</summary>
public sealed record SoakSample(
    int Cycle,
    long OpenFileDescriptors,
    long VmRssKiB,
    long ManagedHeapBytes,
    double ElapsedMs);

/// <summary>Flattening evaluation over a sequence of soak samples.</summary>
public sealed record SoakCurve(
    string Name,
    IReadOnlyList<SoakSample> Samples,
    long SteadyFdDelta,
    double LateRssGrowthRatio,
    bool Flattened,
    string Verdict);

/// <summary>Comparison of one current metric against its baseline gate value.</summary>
public sealed record ToleranceComparison(
    string Metric,
    string GateStatistic,
    double BaselineValue,
    double CurrentValue,
    double DeltaRatio,
    string Status);

/// <summary>Aggregate result of comparing current metrics against a baseline report.</summary>
public sealed record ToleranceGateResult(
    IReadOnlyList<ToleranceComparison> Comparisons,
    bool Passed,
    bool HasWarnings);

/// <summary>Machine-readable output of a benchmark/soak execution.</summary>
public sealed record BenchmarkReport(
    int SchemaVersion,
    string CapturedAtUtc,
    uint Seed,
    int LibrarySize,
    IReadOnlyList<MetricSummary> Metrics,
    IReadOnlyList<SoakCurve> SoakCurves,
    ToleranceGateResult? ToleranceGate,
    bool OverallPassed);

/// <summary>Canonical JSON options for benchmark reports.</summary>
public static class BenchmarkJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
