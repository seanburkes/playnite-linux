using System.Collections.ObjectModel;

namespace Playnite.Core.Models;

// Only the three helpers used by Game, from the frozen SDK/Extensions/ListExtensions.cs.
internal static class ModelCollections
{
    internal static ObservableCollection<T>? ToObservable<T>(this IEnumerable<T>? source)
        => source == null ? null : new ObservableCollection<T>(source);

    internal static bool IsListEqual<T>(this IEnumerable<T>? source, IEnumerable<T>? target)
    {
        if (source == null && target == null)
        {
            return true;
        }

        if (source == null || target == null || source.Count() != target.Count())
        {
            return false;
        }

        return !source.Except(target).Any() && !target.Except(source).Any();
    }

    internal static bool IsListEqualExact<T>(this IEnumerable<T>? source, IEnumerable<T>? target)
    {
        if (source == null && target == null)
        {
            return true;
        }

        if (source == null || target == null || source.Count() != target.Count())
        {
            return false;
        }

        return source.SequenceEqual(target);
    }
}
