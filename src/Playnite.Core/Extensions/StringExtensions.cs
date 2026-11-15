using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace Playnite;

// Adapted from legacy/source/Playnite/Common/Extensions/StringExtensions.cs.
public static class StringExtensions
{
    public static string MD5(this string s)
    {
        using var provider = System.Security.Cryptography.MD5.Create();
        var builder = new StringBuilder();
        foreach (byte b in provider.ComputeHash(Encoding.UTF8.GetBytes(s)))
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    public static bool IsNullOrEmpty(this string? source) => string.IsNullOrEmpty(source);

    public static bool IsNullOrWhiteSpace(this string? source) => string.IsNullOrWhiteSpace(source);

    // Display formatting: the caller supplies the culture formerly read from CurrentCulture.
    public static string Format(this string source, CultureInfo culture, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return string.Format(culture, source, args);
    }

    public static string TrimEndString(this string source, string value, StringComparison comp = StringComparison.Ordinal)
    {
        if (!source.EndsWith(value, comp))
        {
            return source;
        }

        return source.Remove(source.LastIndexOf(value, comp));
    }

    public static string? RemoveTrademarks(this string? str, string remplacement = "")
    {
        if (string.IsNullOrEmpty(str))
        {
            return str;
        }

        return Regex.Replace(str, @"[™©®]", remplacement);
    }

    public static bool IsStartOfStringAcronym(this string? acronymStart, string? input)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(acronymStart)
            || acronymStart.Length < 2 || acronymStart.Length > input.Length)
        {
            return false;
        }

        for (int i = 0; i < acronymStart.Length; i++)
        {
            if (!char.IsLetterOrDigit(acronymStart[i]))
            {
                return false;
            }
        }

        var acronymIndex = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (char.IsLetterOrDigit(input[i]) && (i == 0 || input[i - 1] == ' '))
            {
                if (char.ToUpperInvariant(input[i]) != char.ToUpperInvariant(acronymStart[acronymIndex]))
                {
                    return false;
                }

                acronymIndex++;
                if (acronymIndex == acronymStart.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string RemoveUnlessThatEmptiesTheString(string input, string pattern)
    {
        var output = Regex.Replace(input, pattern, string.Empty);
        return string.IsNullOrWhiteSpace(output) ? input : output;
    }

    public static string NormalizeGameName(this string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var newName = name.RemoveTrademarks()!;
        newName = newName.Replace("_", " ");
        newName = newName.Replace(".", " ");
        newName = newName.Replace('’', '\'');
        newName = RemoveUnlessThatEmptiesTheString(newName, @"\[.*?\]");
        newName = RemoveUnlessThatEmptiesTheString(newName, @"\(.*?\)");
        newName = Regex.Replace(newName, @"\s*:\s*", ": ");
        newName = Regex.Replace(newName, @"\s+", " ");
        if (Regex.IsMatch(newName, @",\s*The$"))
        {
            newName = "The " + Regex.Replace(newName, @",\s*The$", "", RegexOptions.IgnoreCase);
        }

        return newName.Trim();
    }

    // Static-only compatibility API: modern string.Contains would shadow an extension.
    public static bool Contains(string? str, string value, StringComparison comparisonType)
    {
        return str?.IndexOf(value, 0, comparisonType) != -1;
    }

    public static bool ContainsAny(this string? str, char[] chars) => str?.IndexOfAny(chars) >= 0;

    public static bool IsHttpUrl(this string? str)
    {
        return !string.IsNullOrWhiteSpace(str) && Regex.IsMatch(str, @"^https?:\/\/", RegexOptions.IgnoreCase);
    }

    public static bool IsUri(this string? str)
    {
        return !string.IsNullOrWhiteSpace(str) && Uri.IsWellFormedUriString(str, UriKind.Absolute);
    }

    public static string? UrlEncode(this string? str)
    {
        return string.IsNullOrWhiteSpace(str) ? str : HttpUtility.UrlPathEncode(str);
    }

    // Static-only for the same reason as Contains; preserve legacy validation order.
    public static string Replace(string str, string oldValue, string? newValue, StringComparison comparisonType)
    {
        ArgumentNullException.ThrowIfNull(str);
        if (str.Length == 0)
        {
            return str;
        }

        ArgumentNullException.ThrowIfNull(oldValue);
        if (oldValue.Length == 0)
        {
            throw new ArgumentException("String cannot be of zero length.");
        }

        var result = new StringBuilder(str.Length);
        var isReplacementNullOrEmpty = string.IsNullOrEmpty(newValue);
        var start = 0;
        int foundAt;
        while ((foundAt = str.IndexOf(oldValue, start, comparisonType)) != -1)
        {
            if (foundAt - start != 0)
            {
                result.Append(str, start, foundAt - start);
            }

            if (!isReplacementNullOrEmpty)
            {
                result.Append(newValue);
            }

            start = foundAt + oldValue.Length;
            if (start == str.Length)
            {
                return result.ToString();
            }
        }

        result.Append(str, start, str.Length - start);
        return result.ToString();
    }

    public static bool ContainsInvariantCulture(this string source, string value, CompareOptions compareOptions)
    {
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(source, value, compareOptions) >= 0;
    }

    public static int GetLevenshteinDistanceIgnoreCase(this string source, string value)
    {
        return GetLevenshteinDistance(source, value, CaseInsensitiveCharComparer.Instance);
    }

    private static int GetLevenshteinDistance(string value1, string value2, IEqualityComparer<char> comparer)
    {
        if (value2.Length == 0)
        {
            return value1.Length;
        }

        var costs = new int[value2.Length];
        for (int i = 0; i < costs.Length;)
        {
            costs[i] = ++i;
        }

        for (int i = 0; i < value1.Length; i++)
        {
            int cost = i;
            int previousCost = i;
            char value1Char = value1[i];
            for (int j = 0; j < value2.Length; j++)
            {
                int currentCost = cost;
                cost = costs[j];
                if (!comparer.Equals(value1Char, value2[j]))
                {
                    if (previousCost < currentCost)
                    {
                        currentCost = previousCost;
                    }

                    if (cost < currentCost)
                    {
                        currentCost = cost;
                    }

                    ++currentCost;
                }

                costs[j] = currentCost;
                previousCost = currentCost;
            }
        }

        return costs[costs.Length - 1];
    }

    public static double GetJaroWinklerSimilarityIgnoreCase(this string str, string str2, double winklerWeightThreshold = 0.7)
    {
        var comparer = CaseInsensitiveCharComparer.Instance;
        var lLen1 = str.Length;
        var lLen2 = str2.Length;
        if (lLen1 == 0)
        {
            return lLen2 == 0 ? 1.0 : 0.0;
        }

        var lSearchRange = Math.Max(0, Math.Max(lLen1, lLen2) / 2 - 1);
        var lMatched1 = new bool[lLen1];
        var lMatched2 = new bool[lLen2];
        var lNumCommon = 0;
        for (var i = 0; i < lLen1; ++i)
        {
            var lStart = Math.Max(0, i - lSearchRange);
            var lEnd = Math.Min(i + lSearchRange + 1, lLen2);
            for (var j = lStart; j < lEnd; ++j)
            {
                if (lMatched2[j] || !comparer.Equals(str[i], str2[j]))
                {
                    continue;
                }

                lMatched1[i] = true;
                lMatched2[j] = true;
                ++lNumCommon;
                break;
            }
        }

        if (lNumCommon == 0)
        {
            return 0.0;
        }

        var lNumHalfTransposed = 0;
        var k = 0;
        for (var i = 0; i < lLen1; ++i)
        {
            if (!lMatched1[i])
            {
                continue;
            }

            while (!lMatched2[k])
            {
                ++k;
            }

            if (!comparer.Equals(str[i], str2[k]))
            {
                ++lNumHalfTransposed;
            }

            ++k;
        }

        var lNumTransposed = lNumHalfTransposed / 2;
        double lNumCommonD = lNumCommon;
        var lWeight = (lNumCommonD / lLen1 + lNumCommonD / lLen2
            + (lNumCommon - lNumTransposed) / lNumCommonD) / 3.0;
        if (lWeight <= winklerWeightThreshold)
        {
            return lWeight;
        }

        var lMax = Math.Min(4, Math.Min(str.Length, str2.Length));
        var lPos = 0;
        while (lPos < lMax && comparer.Equals(str[lPos], str2[lPos]))
        {
            ++lPos;
        }

        return lPos == 0 ? lWeight : lWeight + 0.1 * lPos * (1.0 - lWeight);
    }

    private sealed class CaseInsensitiveCharComparer : EqualityComparer<char>
    {
        internal static readonly CaseInsensitiveCharComparer Instance = new();
        public override bool Equals(char x, char y) => char.ToUpperInvariant(x) == char.ToUpperInvariant(y);
        public override int GetHashCode(char obj) => char.ToUpperInvariant(obj).GetHashCode();
    }
}
