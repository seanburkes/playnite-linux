namespace Playnite.SDK.Models;

/// <summary>Stored-field grouping keys. Values follow the v10 GroupableField enumeration.</summary>
public enum LibraryGroupBy
{
    /// <summary>Do not group.</summary>
    None = 0,

    /// <summary>Group by last activity.</summary>
    LastActivity = 4,

    /// <summary>Group by release year.</summary>
    ReleaseYear = 6,

    /// <summary>Group by playtime.</summary>
    PlayTime = 14,

    /// <summary>Group by user score.</summary>
    UserScore = 16,

    /// <summary>Group by critic score.</summary>
    CriticScore = 17,

    /// <summary>Group by community score.</summary>
    CommunityScore = 18,

    /// <summary>Group by date added.</summary>
    Added = 19,

    /// <summary>Group by date modified.</summary>
    Modified = 20,

    /// <summary>Group by installation status.</summary>
    InstallationStatus = 22,

    /// <summary>Group by name.</summary>
    Name = 23,

    /// <summary>Group by install size.</summary>
    InstallSize = 25,

    /// <summary>Group by recent activity.</summary>
    RecentActivity = 26,
}
