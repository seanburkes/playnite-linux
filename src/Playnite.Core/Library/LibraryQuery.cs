using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Playnite.Core.Database;
using Playnite.Core.Queries;

namespace Playnite.Core.Library;

/// <summary>Options shared by list and search. The default is all games in ascending name order.</summary>
public sealed record LibraryQuery(
    LibraryFilter? Filter = null,
    SortOrder SortBy = SortOrder.Name,
    bool Descending = false,
    LibraryGroupBy GroupBy = LibraryGroupBy.None,
    int Offset = 0,
    int? Limit = null)
{
    /// <summary>Largest accepted zero-based offset.</summary>
    public const int MaxOffset = 1_000_000;

    /// <summary>Largest accepted explicit page size.</summary>
    public const int MaxLimit = 10_000;
}

/// <summary>Stored-field grouping keys. Values follow the v10 GroupableField enumeration.</summary>
public enum LibraryGroupBy
{
    None = 0,
    LastActivity = 4,
    ReleaseYear = 6,
    PlayTime = 14,
    UserScore = 16,
    CriticScore = 17,
    CommunityScore = 18,
    Added = 19,
    Modified = 20,
    InstallationStatus = 22,
    Name = 23,
    InstallSize = 25,
    RecentActivity = 26,
}

/// <summary>One group within a page. Key is null for a missing release year.</summary>
public sealed record LibraryGroup(IComparable? Key, IReadOnlyList<StoredGame> Games);

/// <summary>Games and their optional groups in page order, with the total before paging.</summary>
public sealed record LibraryPage(IReadOnlyList<StoredGame> Games, int TotalCount, IReadOnlyList<LibraryGroup> Groups);

/// <summary>A snapshot of the supported v10 filter criteria.</summary>
public sealed class LibraryFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    // The query helpers expose computed flags such as IsSet.
                    for (var i = info.Properties.Count - 1; i >= 0; i--)
                    {
                        if (info.Properties[i].Set is null)
                        {
                            info.Properties.RemoveAt(i);
                        }
                    }
                },
            },
        },
    };

    private readonly FilterSettings settings;

    private LibraryFilter(FilterSettings settings)
    {
        this.settings = settings;
        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            settings.Name = null;
        }
        if (string.IsNullOrWhiteSpace(settings.Version))
        {
            settings.Version = null;
        }
    }

    /// <summary>Copies typed criteria so a caller can continue editing its filter settings.</summary>
    public static LibraryFilter FromSettings(FilterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = JsonSerializer.SerializeToElement(settings, JsonOptions);
        return new(snapshot.Deserialize<FilterSettings>(JsonOptions)!);
    }

    /// <summary>Binds named criteria. Unknown fields or invalid values throw JsonException.</summary>
    /// <remarks>Null, JSON null and blank input represent an omitted filter.</remarks>
    public static LibraryFilter? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var settings = JsonSerializer.Deserialize<FilterSettings>(json, JsonOptions);
        return settings is null ? null : new(settings);
    }

    internal FilterMatcher CreateMatcher(IQueryClock clock, IDateSegmentCalculator dateSegments)
        => new(settings, useFuzzyNameMatch: true, clock, dateSegments);
}
