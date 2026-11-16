namespace Playnite.Fuzz;

/// <summary>
/// A single bounded parse of one fuzz input.
/// </summary>
/// <remarks>
/// The target treats a <see cref="Playnite.Core.Serialization.SerializationException"/> as a
/// clean rejection. Every other exception, and a parse that does not return inside the case
/// timeout, is a failure the target must report rather than swallow. A process-level crash
/// (for example a stack overflow) is caught by the CI step, which requires the completion
/// marker below.
/// </remarks>
public static class FuzzExecution
{
    /// <summary>Runs one parser delegate under the case timeout.</summary>
    /// <param name="input">Document text to parse.</param>
    /// <param name="parse">Parser entry point, called exactly once.</param>
    /// <param name="timeoutMilliseconds">Maximum time the parser may run.</param>
    /// <returns>The classified outcome; never null.</returns>
    public static FuzzCaseResult Execute(
        string input,
        Func<string, object?> parse,
        int timeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);

        FuzzCaseResult? result = null;
        var task = Task.Run(() =>
        {
            try
            {
                _ = parse(input);
                result = new FuzzCaseResult(FuzzCaseStatus.Clean, null);
            }
            catch (Playnite.Core.Serialization.SerializationException)
            {
                result = new FuzzCaseResult(FuzzCaseStatus.Rejected, null);
            }
            catch (Exception exception)
            {
                result = new FuzzCaseResult(
                    FuzzCaseStatus.UnhandledException,
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        });

        if (!task.Wait(timeoutMilliseconds))
        {
            return new FuzzCaseResult(
                FuzzCaseStatus.Hang,
                $"the parser did not return within {timeoutMilliseconds} ms");
        }

        return result ?? new FuzzCaseResult(
            FuzzCaseStatus.UnhandledException,
            "the parser returned without producing a result");
    }
}

/// <summary>Classification of one fuzz case.</summary>
public enum FuzzCaseStatus
{
    /// <summary>The parser accepted the document.</summary>
    Clean,

    /// <summary>The parser rejected the document with a serialisation exception.</summary>
    Rejected,

    /// <summary>The parser raised an exception outside its documented contract.</summary>
    UnhandledException,

    /// <summary>The parser did not return inside the case timeout.</summary>
    Hang,
}

/// <summary>The outcome of one fuzz case.</summary>
/// <param name="Status">Classification of the case.</param>
/// <param name="Detail">Failure detail, or null for a clean/rejected case.</param>
public sealed record FuzzCaseResult(FuzzCaseStatus Status, string? Detail)
{
    /// <summary>Gets whether this outcome fails the target.</summary>
    public bool IsFailure => Status is FuzzCaseStatus.UnhandledException or FuzzCaseStatus.Hang;
}
