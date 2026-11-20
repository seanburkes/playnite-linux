namespace Playnite.SDK.Models;

/// <summary>
/// Stored-field subset of the v10 sort keys; numeric values follow
/// <c>PlayniteSDK/Models/FilterPreset.cs</c>. Metadata/library-name sort keys remain deferred,
/// not reinterpreted as ID sorts.
/// </summary>
public enum SortOrder
{
    /// <summary>Sort by name.</summary>
    Name = 0,

    /// <summary>Sort by last activity.</summary>
    LastActivity = 4,

    /// <summary>Sort by release date.</summary>
    ReleaseDate = 6,

    /// <summary>Sort by version.</summary>
    Version = 12,

    /// <summary>Sort by play count.</summary>
    PlayCount = 15,

    /// <summary>Sort by playtime.</summary>
    Playtime = 16,

    /// <summary>Sort by user score.</summary>
    UserScore = 18,

    /// <summary>Sort by critic score.</summary>
    CriticScore = 19,

    /// <summary>Sort by community score.</summary>
    CommunityScore = 20,

    /// <summary>Sort by date added.</summary>
    Added = 21,

    /// <summary>Sort by date modified.</summary>
    Modified = 22,

    /// <summary>Sort by installed state.</summary>
    IsInstalled = 23,

    /// <summary>Sort by hidden state.</summary>
    Hidden = 24,

    /// <summary>Sort by favourite state.</summary>
    Favorite = 25,

    /// <summary>Sort by install directory.</summary>
    InstallDirectory = 26,

    /// <summary>Sort by install size.</summary>
    InstallSize = 28,

    /// <summary>Sort by recent activity.</summary>
    RecentActivity = 29,

    /// <summary>Sort by ROM list.</summary>
    RomList = 30,
}
