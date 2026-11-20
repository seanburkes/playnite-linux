using Playnite.Core.Models;
using Playnite.Core.Queries;

namespace Playnite.Core.Library;

/// <summary>
/// In-memory mirror of the paged reader's total order. Text fields compare with
/// <see cref="AsciiCaseInsensitiveComparer"/>, which reproduces SQLite's NOCASE collation.
/// </summary>
public sealed class PagedLibraryComparer : IComparer<Game>
{
    private readonly SortOrder order;
    private readonly bool descending;

    /// <summary>Creates the comparer for one sort key and direction.</summary>
    public PagedLibraryComparer(SortOrder order, bool descending = false)
    {
        if (!Enum.IsDefined(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        this.order = order;
        this.descending = descending;
    }

    /// <inheritdoc/>
    public int Compare(Game? x, Game? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        var result = CompareField(x, y);
        if (result != 0)
        {
            return descending ? -Math.Sign(result) : Math.Sign(result);
        }

        return order == SortOrder.Name ? 0 : AsciiCaseInsensitiveComparer.Instance.Compare(Name(x), Name(y));
    }

    private static string? Name(Game game)
        => string.IsNullOrEmpty(game.SortingName) ? game.Name : game.SortingName;

    private static string RomList(Game game)
        => game.Roms?.Count > 0 ? string.Join(", ", game.Roms.Select(rom => rom.Path)) : string.Empty;

    private int CompareField(Game x, Game y) => order switch
    {
        SortOrder.Name => AsciiCaseInsensitiveComparer.Instance.Compare(Name(x), Name(y)),
        SortOrder.LastActivity => Nullable.Compare(x.LastActivity, y.LastActivity),
        SortOrder.ReleaseDate => Nullable.Compare(x.ReleaseDate, y.ReleaseDate),
        SortOrder.Version => AsciiCaseInsensitiveComparer.Instance.Compare(x.Version, y.Version),
        SortOrder.PlayCount => x.PlayCount.CompareTo(y.PlayCount),
        SortOrder.Playtime => x.Playtime.CompareTo(y.Playtime),
        SortOrder.UserScore => Nullable.Compare(x.UserScore, y.UserScore),
        SortOrder.CriticScore => Nullable.Compare(x.CriticScore, y.CriticScore),
        SortOrder.CommunityScore => Nullable.Compare(x.CommunityScore, y.CommunityScore),
        SortOrder.Added => Nullable.Compare(x.Added, y.Added),
        SortOrder.Modified => Nullable.Compare(x.Modified, y.Modified),
        SortOrder.IsInstalled => x.IsInstalled.CompareTo(y.IsInstalled),
        SortOrder.Hidden => x.Hidden.CompareTo(y.Hidden),
        SortOrder.Favorite => x.Favorite.CompareTo(y.Favorite),
        SortOrder.InstallDirectory => AsciiCaseInsensitiveComparer.Instance.Compare(x.InstallDirectory, y.InstallDirectory),
        SortOrder.InstallSize => Nullable.Compare(x.InstallSize, y.InstallSize),
        SortOrder.RecentActivity => Nullable.Compare(x.RecentActivity, y.RecentActivity),
        SortOrder.RomList => AsciiCaseInsensitiveComparer.Instance.Compare(RomList(x), RomList(y)),
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    };
}

/// <summary>ASCII-only case folding followed by code-unit comparison, matching SQLite NOCASE.</summary>
internal sealed class AsciiCaseInsensitiveComparer : IComparer<string?>
{
    internal static readonly AsciiCaseInsensitiveComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var length = Math.Min(x.Length, y.Length);
        for (var i = 0; i < length; i++)
        {
            var left = Fold(x[i]);
            var right = Fold(y[i]);
            if (left != right)
            {
                return left < right ? -1 : 1;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    private static char Fold(char value)
        => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;
}
