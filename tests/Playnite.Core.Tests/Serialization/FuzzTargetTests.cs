using Playnite.Core.Serialization;
using Playnite.Fuzz;
using Xunit;

namespace Playnite.Core.Tests.Serialization;

/// <summary>
/// Proves the bounded fuzz target has teeth: real guards reject hostile documents, and the
/// harness classifies an unhandled parser exception and a hang as failures rather than as a
/// clean parse.
/// </summary>
public sealed class FuzzTargetTests
{
    private const int InputLimit = 64 * 1024;

    public static TheoryData<string> Formats => new() { "json", "yaml", "toml", "ini" };

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_deliberately_malformed_document_is_rejected_cleanly(string format)
    {
        var document = FuzzParsers.MalformedProbe(format);

        var exception = Assert.Throws<SerializationException>(
            () => FuzzParsers.Parse(format, document, InputLimit));

        Assert.Equal(format.ToUpperInvariant(), exception.Format);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Deep_nesting_that_would_overflow_a_naive_parser_is_rejected_cleanly(string format)
    {
        var document = FuzzParsers.DeepNestingProbe(format);

        var exception = Assert.Throws<SerializationException>(
            () => FuzzParsers.Parse(format, document, InputLimit));

        Assert.Equal(format.ToUpperInvariant(), exception.Format);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_document_over_the_limit_is_rejected_before_parsing(string format)
    {
        var document = FuzzParsers.OversizedProbe(InputLimit);

        var exception = Assert.Throws<SerializationException>(
            () => FuzzParsers.Parse(format, document, InputLimit));

        Assert.Contains("exceeds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_harness_reports_an_unhandled_parser_exception_as_a_failure()
    {
        var result = FuzzExecution.Execute(
            "{}",
            _ => throw new InvalidOperationException("naive parser crashed"),
            timeoutMilliseconds: 2_000);

        Assert.Equal(FuzzCaseStatus.UnhandledException, result.Status);
        Assert.True(result.IsFailure);
        Assert.Contains("InvalidOperationException", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_harness_reports_a_hang_as_a_failure()
    {
        using var gate = new ManualResetEventSlim(initialState: false);

        var result = FuzzExecution.Execute(
            "{}",
            _ =>
            {
                gate.Wait(TimeSpan.FromSeconds(30));
                return null;
            },
            timeoutMilliseconds: 200);
        gate.Set();

        Assert.Equal(FuzzCaseStatus.Hang, result.Status);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void The_harness_treats_a_serialization_exception_as_a_clean_rejection()
    {
        var result = FuzzExecution.Execute(
            "{}",
            _ => throw new SerializationException("JSON", "rejected by the parser contract"),
            timeoutMilliseconds: 2_000);

        Assert.Equal(FuzzCaseStatus.Rejected, result.Status);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public void The_mutation_stream_is_reproducible_from_the_recorded_seed()
    {
        var seed = System.Text.Encoding.UTF8.GetBytes("""{"Count":1,"Name":"héllo"}""");

        var first = FuzzMutator.Sequence(seed, 0x5EED_003C, 32);
        var second = FuzzMutator.Sequence(seed, 0x5EED_003C, 32);

        Assert.Equal(first, second);
        Assert.Contains(first, value => value != Convert.ToBase64String(seed));
    }

    [Fact]
    public void The_committed_corpus_verifies_against_its_manifest_for_every_format()
    {
        var seeds = FuzzCorpus.Load(RepositoryPaths.Combine("tests", "fixtures", "serialization-fuzz"));

        Assert.Equal(FuzzParsers.Formats.Order(), seeds.Select(seed => seed.Format).Distinct().Order());
        Assert.All(seeds, seed => Assert.True(seed.Bytes.Length > 0));
        Assert.Equal(64, FuzzCorpus.Digest(seeds).Length);
    }

    [Fact]
    public void A_minimum_budget_run_reports_the_completion_marker_and_smaller_budgets_are_rejected()
    {
        var corpusRoot = RepositoryPaths.Combine("tests", "fixtures", "serialization-fuzz");
        var options = new FuzzOptions
        {
            CorpusRoot = corpusRoot,
            Seed = 0x5EED_003C,
            Seconds = FuzzOptions.MinimumWallClockSeconds,
            Iterations = FuzzOptions.MinimumIterations,
            CaseTimeoutMilliseconds = 2_000,
            MaxInputBytes = InputLimit,
            MaxManagedBytes = 256L * 1024 * 1024,
        };

        using var output = new StringWriter();
        var result = FuzzRunner.Run(options, output);

        // Both budgets are at their documented minimums. Which one stops a valid run is a
        // wall-clock race, so assert the configured budget values and the completion marker
        // directly instead of asserting that the iteration budget necessarily won.
        var log = output.ToString();
        Assert.Contains(
            $"FUZZ START seed=0x{options.Seed:X16} seconds={FuzzOptions.MinimumWallClockSeconds} " +
            $"iterations={FuzzOptions.MinimumIterations} ",
            log,
            StringComparison.Ordinal);
        Assert.Contains($"FUZZ COMPLETE seed=0x{options.Seed:X16} ", log, StringComparison.Ordinal);
        Assert.True(result.Cases > 0);
        Assert.Equal(result.Cases, result.Clean + result.Rejected);
        Assert.Contains(result.Budget, new[] { "iterations", "wall-clock" });

        var tinySeconds = options with { Seconds = FuzzOptions.MinimumWallClockSeconds - 1 };
        var tinyIterations = options with { Iterations = FuzzOptions.MinimumIterations - 1 };
        Assert.Throws<ArgumentException>(tinySeconds.Validate);
        Assert.Throws<ArgumentException>(tinyIterations.Validate);
    }
}
