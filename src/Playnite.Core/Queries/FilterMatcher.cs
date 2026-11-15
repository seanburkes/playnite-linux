using Playnite.Core.Models;

namespace Playnite.Core.Queries;

// Playnite/Database/GameDatabase_Filters.cs:50-378, with no database/UI dependency.
public sealed class FilterMatcher(FilterSettings filterSettings, bool useFuzzyNameMatch = false,
    IQueryClock? clock = null, IDateSegmentCalculator? segmentCalculator = null)
{
    private static readonly Guid MaxGuid = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public IEnumerable<Game> Filter(IEnumerable<Game> games) => games.Where(Match);

    public bool Match(Game game)
    {
        return (filterSettings.IsInstalled == filterSettings.IsUnInstalled ||
                (filterSettings.IsInstalled && game.IsInstalled) ||
                (filterSettings.IsUnInstalled && !game.IsInstalled)) &&
            (!filterSettings.Favorite || game.Favorite) &&
            filterSettings.Hidden == game.Hidden &&
            (filterSettings.Library?.IsSet != true || filterSettings.Library.Ids!.Contains(game.PluginId)) &&
            MatchName(game) &&
            (filterSettings.ReleaseYear?.IsSet != true || filterSettings.ReleaseYear.Values!.Contains(
                game.ReleaseDate == null ? FilterSettings.MissingFieldString : game.ReleaseYear.ToString()!)) &&
            MatchEnumField(filterSettings.PlayTime, (int)game.PlaytimeCategory) &&
            MatchEnumField(filterSettings.InstallSize, (int)game.InstallSizeGroup) &&
            (string.IsNullOrEmpty(filterSettings.Version) ||
                game.Version?.Contains(filterSettings.Version, StringComparison.OrdinalIgnoreCase) == true) &&
            MatchSingle(filterSettings.CompletionStatuses, game.CompletionStatusId) &&
            MatchDate(filterSettings.LastActivity, game.LastActivity) &&
            MatchDate(filterSettings.RecentActivity, game.RecentActivity) &&
            MatchDate(filterSettings.Added, game.Added) &&
            MatchDate(filterSettings.Modified, game.Modified) &&
            MatchEnumField(filterSettings.UserScore, (int)game.UserScoreGroup) &&
            MatchEnumField(filterSettings.CommunityScore, (int)game.CommunityScoreGroup) &&
            MatchEnumField(filterSettings.CriticScore, (int)game.CriticScoreGroup) &&
            MatchList(filterSettings.Series, game.SeriesIds) &&
            MatchList(filterSettings.Region, game.RegionIds) &&
            MatchSingle(filterSettings.Source, game.SourceId) &&
            MatchList(filterSettings.AgeRating, game.AgeRatingIds) &&
            MatchList(filterSettings.Genre, game.GenreIds) &&
            MatchList(filterSettings.Platform, game.PlatformIds) &&
            MatchList(filterSettings.Publisher, game.PublisherIds) &&
            MatchList(filterSettings.Developer, game.DeveloperIds) &&
            MatchList(filterSettings.Category, game.CategoryIds) &&
            MatchList(filterSettings.Tag, game.TagIds) &&
            MatchList(filterSettings.Feature, game.FeatureIds);
    }

    private bool MatchName(Game game)
    {
        var name = filterSettings.Name;
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        if (string.IsNullOrEmpty(game.Name))
        {
            return false;
        }

        if (name.Length >= 2 && name[0] == '^')
        {
            return game.GetNameGroup() == name[1];
        }

        if (!useFuzzyNameMatch || name[0] == '!')
        {
            // v10 drops the first character even when it is not '!'.
            return game.Name.IndexOf(name[1..], StringComparison.OrdinalIgnoreCase) >= 0;
        }

        return TextSearch.MatchTextFilter(name, game.Name, true);
    }

    private static bool MatchEnumField(EnumFilterItemProperties? filter, int value)
        => filter?.IsSet != true || filter.Values!.Contains(value);

    private bool MatchDate(EnumFilterItemProperties? filter, DateTime? value)
    {
        if (filter?.IsSet != true)
        {
            return true;
        }

        if (clock == null || segmentCalculator == null)
        {
            throw new InvalidOperationException("Date filters require an explicit clock and segment calculator.");
        }

        return MatchEnumField(filter, (int)segmentCalculator.GetSegment(value, clock.Now));
    }

    private static bool MatchSingle(IdItemFilterItemProperties? filter, Guid id)
    {
        if (filter?.IsSet != true)
        {
            return true;
        }

        if (filter.Ids?.Count > 0)
        {
            return filter.Ids.Contains(id) || (id == MaxGuid && filter.Ids.Contains(Guid.Empty));
        }

        return true;
    }

    private bool MatchList(IdItemFilterItemProperties? filter, List<Guid>? ids)
    {
        if (filter?.IsSet != true)
        {
            return true;
        }

        if (filter.Ids?.Count > 0)
        {
            bool HasId(Guid id) => id == Guid.Empty ? ids == null || ids.Count == 0 : ids?.Contains(id) == true;
            return filterSettings.UseAndFilteringStyle ? filter.Ids.All(HasId) : filter.Ids.Any(HasId);
        }

        return true;
    }
}
