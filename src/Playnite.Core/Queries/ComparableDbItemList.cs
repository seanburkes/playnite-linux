using Playnite.Core.Models;

namespace Playnite.Core.Queries;

// PlayniteSDK/Collections/ComparableList.cs:15-53, without the unused WPF import.
public sealed class ComparableDbItemList<T>(IEnumerable<T>? collection)
    : List<T>(collection ?? []), IComparable where T : DatabaseObject
{
    public int CompareTo(object? obj)
    {
        if (obj == null)
        {
            return -1;
        }

        var list2 = (List<T>)obj;
        if (Count == 0 && list2.Count == 0)
        {
            return 0;
        }

        if (list2.Count == 0 && Count > 0)
        {
            return -1;
        }

        if (Count == 0 && list2.Count > 0)
        {
            return 1;
        }

        return string.Join(", ", this).CompareTo(string.Join(", ", list2));
    }
}
