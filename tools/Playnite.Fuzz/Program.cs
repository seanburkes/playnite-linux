using System.Globalization;

namespace Playnite.Fuzz;

/// <summary>Command-line entry point for the bounded serialisation fuzz target.</summary>
public static class Program
{
    private const string Usage =
        """
        Usage: Playnite.Fuzz --corpus <dir> [options]

        Runs a deterministic, bounded mutation fuzz target over the JSON, YAML, TOML and INI
        parser entry points in Playnite.Core.Serialization.

        Options:
          --corpus <dir>            Committed corpus root (required).
          --seed <value>            Mutation seed; decimal or 0x-prefixed hexadecimal.
          --seconds <n>             Wall-clock budget in seconds (default 120).
          --iterations <n>          Iteration budget; whichever budget is reached first stops it.
          --case-timeout-ms <n>     Per-case hang timeout (default 2000).
          --max-input-bytes <n>     Parser size limit used for this run (default 65536).
          --max-memory-mb <n>       Managed-heap ceiling (default 256).
          --help                    Print this text.

        A clean run prints a single "FUZZ COMPLETE" line. A crash, unhandled parser exception,
        hang, corpus drift, malformed probe acceptance or memory overrun exits nonzero and does
        not print that marker.
        """;

    /// <summary>Runs the target.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>0 on a clean completed run; 1 on a fuzz failure; 2 on a usage error.</returns>
    public static int Main(string[] args)
    {
        FuzzOptions? options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"FUZZ ERROR: {exception.Message}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options is null)
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        try
        {
            _ = FuzzRunner.Run(options, Console.Out);
            Console.Out.Flush();
            return 0;
        }
        catch (FuzzFailureException exception)
        {
            Console.Error.WriteLine($"FUZZ FAILURE: {exception.Message}");
            if (exception.Reproduction is { Length: > 0 } reproduction)
            {
                Console.Error.WriteLine(reproduction);
            }

            Console.Error.Flush();
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FUZZ ERROR: {exception.GetType().Name}: {exception.Message}");
            Console.Error.Flush();
            return 1;
        }
    }

    private static FuzzOptions? Parse(string[] args)
    {
        var options = new FuzzOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--help" or "-h":
                    return null;
                case "--corpus":
                    options = options with { CorpusRoot = NextValue(args, ref index, argument) };
                    break;
                case "--seed":
                    options = options with { Seed = ParseSeed(NextValue(args, ref index, argument)) };
                    break;
                case "--seconds":
                    options = options with { Seconds = ParseInt(NextValue(args, ref index, argument), argument) };
                    break;
                case "--iterations":
                    options = options with { Iterations = ParseLong(NextValue(args, ref index, argument), argument) };
                    break;
                case "--case-timeout-ms":
                    options = options with
                    {
                        CaseTimeoutMilliseconds = ParseInt(NextValue(args, ref index, argument), argument),
                    };
                    break;
                case "--max-input-bytes":
                    options = options with
                    {
                        MaxInputBytes = ParseInt(NextValue(args, ref index, argument), argument),
                    };
                    break;
                case "--max-memory-mb":
                    options = options with
                    {
                        MaxManagedBytes = ParseLong(NextValue(args, ref index, argument), argument) * 1024 * 1024,
                    };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{argument}'.");
            }
        }

        options.Validate();
        return options;
    }

    private static string NextValue(string[] args, ref int index, string argument)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Argument '{argument}' needs a value.");
        }

        return args[++index];
    }

    private static int ParseInt(string value, string argument)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new ArgumentException($"Argument '{argument}' needs an integer, got '{value}'.");
        }

        return result;
    }

    private static long ParseLong(string value, string argument)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new ArgumentException($"Argument '{argument}' needs an integer, got '{value}'.");
        }

        return result;
    }

    private static ulong ParseSeed(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }

        if (ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
        {
            return dec;
        }

        throw new ArgumentException($"Argument '--seed' needs a 64-bit integer, got '{value}'.");
    }
}
