namespace Playnite.Benchmarks;

/// <summary>Compares benchmark metrics against a recorded baseline using warn/fail tolerances.</summary>
public static class ToleranceGate
{
    public static ToleranceGateResult Compare(
        IReadOnlyList<MetricSummary> current,
        IReadOnlyList<MetricSummary> baseline,
        double warnRatio = 0.10,
        double failRatio = 0.20,
        double absoluteFloorMs = 0.25)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);
        if (baseline.Count == 0)
        {
            throw new ArgumentException("Baseline must contain at least one metric.", nameof(baseline));
        }

        if (!double.IsFinite(warnRatio) || warnRatio < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(warnRatio), "Warn ratio must be finite and non-negative.");
        }

        if (!double.IsFinite(failRatio) || failRatio < warnRatio)
        {
            throw new ArgumentOutOfRangeException(nameof(failRatio), "Fail ratio must be finite and at least warn ratio.");
        }

        if (!double.IsFinite(absoluteFloorMs) || absoluteFloorMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteFloorMs), "Absolute floor must be finite and non-negative.");
        }

        var currentMap = new Dictionary<string, MetricSummary>(StringComparer.Ordinal);
        foreach (var metric in current)
        {
            if (!double.IsFinite(metric.GateValue) || metric.GateValue < 0)
            {
                throw new ArgumentException($"Current metric '{metric.Name}' has an invalid gate value ({metric.GateValue}).", nameof(current));
            }

            if (!currentMap.TryAdd(metric.Name, metric))
            {
                throw new ArgumentException($"Duplicate metric name in current report: '{metric.Name}'.", nameof(current));
            }
        }

        var seenBaselineNames = new HashSet<string>(StringComparer.Ordinal);
        var comparisons = new List<ToleranceComparison>(baseline.Count);
        foreach (var expected in baseline)
        {
            if (!double.IsFinite(expected.GateValue) || expected.GateValue < 0)
            {
                throw new ArgumentException($"Baseline metric '{expected.Name}' has an invalid gate value ({expected.GateValue}).", nameof(baseline));
            }

            if (!seenBaselineNames.Add(expected.Name))
            {
                throw new ArgumentException($"Duplicate metric name in baseline report: '{expected.Name}'.", nameof(baseline));
            }

            if (!currentMap.TryGetValue(expected.Name, out var actual))
            {
                comparisons.Add(new ToleranceComparison(
                    expected.Name,
                    expected.GateStatistic,
                    expected.GateValue,
                    -1.0,
                    -1.0,
                    "missing-metric"));
                continue;
            }

            if (!string.Equals(expected.Unit, actual.Unit, StringComparison.Ordinal))
            {
                comparisons.Add(new ToleranceComparison(
                    expected.Name,
                    expected.GateStatistic,
                    expected.GateValue,
                    actual.GateValue,
                    0.0,
                    "unit-mismatch"));
                continue;
            }

            if (!string.Equals(expected.GateStatistic, actual.GateStatistic, StringComparison.Ordinal))
            {
                comparisons.Add(new ToleranceComparison(
                    expected.Name,
                    expected.GateStatistic,
                    expected.GateValue,
                    actual.GateValue,
                    0.0,
                    "statistic-mismatch"));
                continue;
            }

            var baselineValue = expected.GateValue;
            var currentValue = actual.GateValue;
            var deltaRatio = baselineValue <= 0.0
                ? (currentValue > 0.0 ? 1.0 : 0.0)
                : (currentValue - baselineValue) / baselineValue;

            string status;
            if (string.Equals(expected.Unit, "ms", StringComparison.Ordinal)
                && currentValue <= absoluteFloorMs
                && baselineValue <= absoluteFloorMs)
            {
                status = "pass";
            }
            else if (baselineValue <= 0.0)
            {
                status = currentValue > 0.0 ? "fail" : "pass";
            }
            else
            {
                status = deltaRatio > failRatio
                    ? "fail"
                    : deltaRatio > warnRatio
                        ? "warn"
                        : "pass";
            }

            comparisons.Add(new ToleranceComparison(
                expected.Name,
                expected.GateStatistic,
                baselineValue,
                currentValue,
                deltaRatio,
                status));
        }

        var passed = comparisons.All(c => c.Status is "pass" or "warn");
        var hasWarnings = comparisons.Any(c => c.Status == "warn");
        return new ToleranceGateResult(comparisons, passed, hasWarnings);
    }
}
