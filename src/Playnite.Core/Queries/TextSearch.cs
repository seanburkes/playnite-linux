using System.Globalization;

namespace Playnite.Core.Queries;

public static class TextSearch
{
    // SearchViewModel.cs:668-715, including the literal-space splitter and early
    // similarity check before the length guard. No NormalizeGameName call in v10.
    public static bool MatchTextFilter(string? filter, string? toMatch,
        bool matchTargetAcronymStart, double minimumJaronWinklerSimilarity = 0.90)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(toMatch))
        {
            return false;
        }

        if (filter.GetJaroWinklerSimilarityIgnoreCase(toMatch) >= minimumJaronWinklerSimilarity)
        {
            return true;
        }

        if (filter.Length > toMatch.Length)
        {
            return false;
        }

        if (matchTargetAcronymStart && filter.IsStartOfStringAcronym(toMatch))
        {
            return true;
        }

        var filterSplit = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var toMatchSplit = toMatch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return filterSplit.All(word => toMatchSplit.Any(a => a.ContainsInvariantCulture(word,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols | CompareOptions.IgnoreNonSpace)));
    }
}
