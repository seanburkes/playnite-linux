using System.Collections;
using System.Globalization;
using Playnite.Core.Models;
using Playnite.SDK.Models;

namespace Playnite.Core.Queries;

// DesktopCollectionView.cs:230-234 and FullscreenCollectionView.cs:59-62:
// direction applies only to the primary key; Name is always ascending on ties.
public sealed class GameComparer : IComparer<Game>
{
    private readonly SortOrder order;
    private readonly bool descending;
    private readonly Comparer comparer = new(CultureInfo.CurrentCulture);

    public GameComparer(SortOrder order, bool descending = false)
    {
        if (!Enum.IsDefined(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        this.order = order;
        this.descending = descending;
    }

    public int Compare(Game? x, Game? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        var result = CompareField(x, y);
        if (result != 0)
        {
            return descending ? -Math.Sign(result) : Math.Sign(result);
        }

        return order == SortOrder.Name ? 0 : comparer.Compare(Name(x), Name(y));
    }

    public IOrderedEnumerable<Game> Sort(IEnumerable<Game> games) => games.OrderBy(g => g, this);

    private static string Name(Game game) => string.IsNullOrEmpty(game.SortingName) ? game.Name : game.SortingName;

    private int CompareField(Game x, Game y) => order switch
    {
        SortOrder.Name => comparer.Compare(Name(x), Name(y)),
        SortOrder.LastActivity => comparer.Compare(x.LastActivity, y.LastActivity),
        SortOrder.ReleaseDate => comparer.Compare(x.ReleaseDate, y.ReleaseDate),
        SortOrder.Version => comparer.Compare(x.Version, y.Version),
        SortOrder.PlayCount => x.PlayCount.CompareTo(y.PlayCount),
        SortOrder.Playtime => x.Playtime.CompareTo(y.Playtime),
        SortOrder.UserScore => comparer.Compare(x.UserScore, y.UserScore),
        SortOrder.CriticScore => comparer.Compare(x.CriticScore, y.CriticScore),
        SortOrder.CommunityScore => comparer.Compare(x.CommunityScore, y.CommunityScore),
        SortOrder.Added => comparer.Compare(x.Added, y.Added),
        SortOrder.Modified => comparer.Compare(x.Modified, y.Modified),
        SortOrder.IsInstalled => x.IsInstalled.CompareTo(y.IsInstalled),
        SortOrder.Hidden => x.Hidden.CompareTo(y.Hidden),
        SortOrder.Favorite => x.Favorite.CompareTo(y.Favorite),
        SortOrder.InstallDirectory => comparer.Compare(x.InstallDirectory, y.InstallDirectory),
        SortOrder.InstallSize => comparer.Compare(x.InstallSize, y.InstallSize),
        SortOrder.RecentActivity => comparer.Compare(x.RecentActivity, y.RecentActivity),
        SortOrder.RomList => comparer.Compare(RomList(x), RomList(y)),
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };

    private static string RomList(Game game) => game.Roms?.Count > 0 ? string.Join(", ", game.Roms.Select(a => a.Path)) : string.Empty;

}

public static class SearchResults
{
    // DefaultSearchContext.GetSearchResults:205-210, on already eligible games.
    // Search ranking uses display Name, not the library's SortingName override.
    public static IEnumerable<Game> Rank(IEnumerable<Game> games, string searchTerm)
        => games.OrderBy(a => a.Name.GetLevenshteinDistanceIgnoreCase(searchTerm))
            .ThenBy(a => a.Name).ThenByDescending(a => a.IsInstalled).Take(60);
}
