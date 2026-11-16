using System.Text.RegularExpressions;
using Playnite.Core.Models;

namespace Playnite.Core.Queries;

// Database/GameFieldComparer.cs. This is metadata equality, NOT search matching.
public sealed class GameFieldComparer : IEqualityComparer<string?>
{
    private static readonly Regex Regex = new(@"[\s-]", RegexOptions.Compiled);
    public static readonly GameFieldComparer Instance = new();

    public bool Equals(string? x, string? y) => StringEquals(x, y);

    public static bool StringEquals(string? x, string? y)
    {
        if (string.IsNullOrEmpty(x) && string.IsNullOrEmpty(y))
        {
            return true;
        }

        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y))
        {
            return false;
        }

        return string.Equals(Regex.Replace(x, ""), Regex.Replace(y, ""), StringComparison.InvariantCultureIgnoreCase);
    }

    public static bool FieldEquals<T>(T x, string? y) where T : DatabaseObject => StringEquals(x.Name, y);
    public static bool FieldEquals<T>(T x, T y) where T : DatabaseObject => StringEquals(x.Name, y.Name);

    // Preserve v10's current-culture hash and null exception, despite the mismatch
    // with invariant equality (notably Turkish I). Do not use this for query sorting.
    public int GetHashCode(string? obj) => Regex.Replace(obj!, "").ToLower().GetHashCode();
}
