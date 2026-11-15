namespace Playnite.Core.Queries;

// Matching-only settings from Playnite/Settings/FilterSettings.cs. UI notifications,
// persistence and SDK conversion belong to their respective boundary units.
public sealed class StringFilterItemProperties
{
    public List<string>? Values { get; set; }
    public bool IsSet => Values?.Any(a => !string.IsNullOrEmpty(a)) == true;
}

public sealed class EnumFilterItemProperties
{
    public List<int>? Values { get; set; }
    public bool IsSet => Values?.Count > 0;
}

public sealed class IdItemFilterItemProperties
{
    public List<Guid>? Ids { get; set; }
    // Expanded metadata text matching is deferred; only stored IDs are supported.
    public bool IsSet => Ids?.Count > 0;
}

public sealed class FilterSettings
{
    public const string MissingFieldString = "{}";
    public string? Name { get; set; }
    public string? Version { get; set; }
    public bool UseAndFilteringStyle { get; set; }
    public bool IsInstalled { get; set; }
    public bool IsUnInstalled { get; set; }
    public bool Hidden { get; set; }
    public bool Favorite { get; set; }
    public StringFilterItemProperties? ReleaseYear { get; set; }
    public IdItemFilterItemProperties? Genre { get; set; }
    public IdItemFilterItemProperties? Platform { get; set; }
    public IdItemFilterItemProperties? Publisher { get; set; }
    public IdItemFilterItemProperties? Developer { get; set; }
    public IdItemFilterItemProperties? Category { get; set; }
    public IdItemFilterItemProperties? Tag { get; set; }
    public IdItemFilterItemProperties? Series { get; set; }
    public IdItemFilterItemProperties? Region { get; set; }
    public IdItemFilterItemProperties? Source { get; set; }
    public IdItemFilterItemProperties? AgeRating { get; set; }
    public IdItemFilterItemProperties? Library { get; set; }
    public IdItemFilterItemProperties? CompletionStatuses { get; set; }
    public IdItemFilterItemProperties? Feature { get; set; }
    public EnumFilterItemProperties? UserScore { get; set; }
    public EnumFilterItemProperties? CriticScore { get; set; }
    public EnumFilterItemProperties? CommunityScore { get; set; }
    public EnumFilterItemProperties? LastActivity { get; set; }
    public EnumFilterItemProperties? RecentActivity { get; set; }
    public EnumFilterItemProperties? Added { get; set; }
    public EnumFilterItemProperties? Modified { get; set; }
    public EnumFilterItemProperties? PlayTime { get; set; }
    public EnumFilterItemProperties? InstallSize { get; set; }
}
