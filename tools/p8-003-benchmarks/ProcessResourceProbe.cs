using System.Globalization;

namespace Playnite.Benchmarks;

/// <summary>Reads Linux <c>/proc</c> file descriptor and memory counters and evaluates soak curves.</summary>
public static class ProcessResourceProbe
{
    public const string DefaultProcSelfRoot = "/proc/self";

    public static long ReadOpenFileDescriptorCount(string procSelfRoot = DefaultProcSelfRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(procSelfRoot);
        var fdPath = Path.Combine(procSelfRoot, "fd");
        if (!Directory.Exists(fdPath))
        {
            throw new InvalidOperationException($"Open file descriptor directory not found: {fdPath}");
        }

        try
        {
            long count = 0;
            foreach (var _ in Directory.EnumerateFileSystemEntries(fdPath))
            {
                count++;
            }

            return count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Unable to enumerate open file descriptors under {fdPath}.", exception);
        }
    }

    public static long ReadVmRssKiB(string procSelfRoot = DefaultProcSelfRoot) =>
        ReadStatusKiB(procSelfRoot, "VmRSS:");

    public static long ReadVmHwmKiB(string procSelfRoot = DefaultProcSelfRoot) =>
        ReadStatusKiB(procSelfRoot, "VmHWM:");

    public static long ReadManagedHeapBytes() => GC.GetTotalMemory(forceFullCollection: false);

    public static long ReadStatusKiB(string procSelfRoot, string fieldPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(procSelfRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldPrefix);
        var statusPath = Path.Combine(procSelfRoot, "status");
        if (!File.Exists(statusPath))
        {
            throw new InvalidOperationException($"Process status file not found: {statusPath}");
        }

        foreach (var line in File.ReadLines(statusPath))
        {
            if (!line.StartsWith(fieldPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = line[fieldPrefix.Length..].Trim();
            var parts = remainder.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kib)
                && (parts.Length == 1 || string.Equals(parts[1], "kB", StringComparison.OrdinalIgnoreCase)))
            {
                return kib;
            }

            throw new InvalidDataException($"Malformed {fieldPrefix} line in {statusPath}: '{line}'.");
        }

        throw new InvalidDataException($"Missing {fieldPrefix} entry in {statusPath}.");
    }

    /// <summary>
    /// Checks that open file descriptors and RSS flatten after the excluded warm-up cycle.
    /// Cycle 0 is treated as warm-up; the remaining steady samples are split into first and
    /// second halves to distinguish expected initial allocation from unbounded growth.
    /// </summary>
    public static SoakCurve EvaluateSoakFlattening(
        string name,
        IReadOnlyList<SoakSample> samples,
        long maxFdDelta = 4,
        double maxLateRssGrowthRatio = 0.25)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 3)
        {
            throw new ArgumentException("At least 3 soak samples (1 warm-up + 2 steady) are required.", nameof(samples));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maxFdDelta);
        if (!double.IsFinite(maxLateRssGrowthRatio) || maxLateRssGrowthRatio < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLateRssGrowthRatio), "Growth ratio must be finite and non-negative.");
        }

        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].OpenFileDescriptors < 0 || samples[i].VmRssKiB < 0)
            {
                throw new ArgumentException($"Sample {i} has negative resource counters.", nameof(samples));
            }
        }

        var steadyCount = samples.Count - 1;
        var firstHalfCount = Math.Max(1, steadyCount / 2);
        var firstHalf = new SoakSample[firstHalfCount];
        var secondHalf = new SoakSample[steadyCount - firstHalfCount];
        for (var i = 0; i < firstHalfCount; i++)
        {
            firstHalf[i] = samples[1 + i];
        }

        for (var i = 0; i < secondHalf.Length; i++)
        {
            secondHalf[i] = samples[1 + firstHalfCount + i];
        }

        var minFirstFd = firstHalf.Min(s => s.OpenFileDescriptors);
        var maxSecondFd = secondHalf.Max(s => s.OpenFileDescriptors);
        var steadyFdDelta = maxSecondFd - minFirstFd;

        var firstRssMedian = MedianOf(firstHalf.Select(s => (double)s.VmRssKiB).ToArray());
        var secondRssMedian = MedianOf(secondHalf.Select(s => (double)s.VmRssKiB).ToArray());
        var lateRssGrowthRatio = firstRssMedian <= 0.0
            ? (secondRssMedian > 0.0 ? 1.0 : 0.0)
            : (secondRssMedian - firstRssMedian) / firstRssMedian;

        var fdOk = steadyFdDelta <= maxFdDelta;
        var rssOk = lateRssGrowthRatio <= maxLateRssGrowthRatio;
        var flattened = fdOk && rssOk;
        var verdict = (fdOk, rssOk) switch
        {
            (true, true) => "flat",
            (false, true) => "fd-growth",
            (true, false) => "rss-growth",
            (false, false) => "fd-and-rss-growth",
        };

        return new SoakCurve(name, samples, steadyFdDelta, lateRssGrowthRatio, flattened, verdict);
    }

    private static double MedianOf(double[] values)
    {
        Array.Sort(values);
        var count = values.Length;
        return (count & 1) == 1
            ? values[count / 2]
            : (values[(count / 2) - 1] + values[count / 2]) / 2.0;
    }
}
