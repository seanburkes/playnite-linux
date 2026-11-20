using System.Globalization;
using Playnite.Core.Library;
using Playnite.Core.Queries;

namespace Playnite.Cli;

/// <summary>Invalid command-line input. The host prints the message and exits with code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Options shared by the list and search commands.</summary>
internal sealed record QueryOptions(string LibraryPath, int Offset, int? Limit, SortOrder SortBy, bool Descending)
{
    internal LibraryQuery ToQuery() => new(SortBy: SortBy, Descending: Descending, Offset: Offset, Limit: Limit);

    internal static QueryOptions Parse(string[] args, string usage)
    {
        string? library = null;
        var offset = 0;
        int? limit = null;
        var sortBy = SortOrder.Name;
        var descending = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--library":
                    library = Value(args, ref index, "--library", usage);
                    break;
                case "--offset":
                    offset = Bounded(Value(args, ref index, "--offset", usage), "--offset", 0, LibraryQuery.MaxOffset, usage);
                    break;
                case "--limit":
                    limit = Bounded(Value(args, ref index, "--limit", usage), "--limit", 1, LibraryQuery.MaxLimit, usage);
                    break;
                case "--sort":
                    sortBy = Sort(Value(args, ref index, "--sort", usage), usage);
                    break;
                case "--descending":
                    descending = true;
                    break;
                default:
                    throw new UsageException(usage);
            }
        }

        return new QueryOptions(library ?? throw new UsageException(usage), offset, limit, sortBy, descending);
    }

    private static string Value(string[] args, ref int index, string name, string usage)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new UsageException($"{name} needs a value.{Environment.NewLine}{usage}");
        }

        return args[++index];
    }

    private static int Bounded(string value, string name, int minimum, int maximum, string usage)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number < minimum || number > maximum)
        {
            throw new UsageException(
                $"{name} must be an integer from {minimum} to {maximum}, not '{value}'.{Environment.NewLine}{usage}");
        }

        return number;
    }

    private static SortOrder Sort(string value, string usage)
    {
        foreach (var name in Enum.GetNames<SortOrder>())
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<SortOrder>(name);
            }
        }

        throw new UsageException(
            $"Unknown sort key '{value}'. Valid keys: {string.Join(", ", Enum.GetNames<SortOrder>())}.{Environment.NewLine}{usage}");
    }
}
