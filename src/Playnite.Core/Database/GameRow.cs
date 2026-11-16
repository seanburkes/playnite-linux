using Playnite.Core.Models;

namespace Playnite.Core.Database;

internal sealed class GameRow
{
    internal const string Columns = """
        Id, Name, SortingName, GameId, PluginId, BackgroundImage, Description, Notes, Icon, CoverImage,
        InstallDirectory, Version, Manual, Hidden, Favorite, EnableSystemHdr, IncludeLibraryPluginAction,
        IsInstalled, OverrideInstallState, UseGlobalPreScript, UseGlobalPostScript, UseGlobalGameStartedScript,
        PreScript, PostScript, GameStartedScript, SourceId, CompletionStatusId, UserScore, CriticScore, CommunityScore,
        ReleaseYear, ReleaseMonth, ReleaseDay, LastActivity, Added, Modified, LastSizeScanDate, Playtime, PlayCount, InstallSize,
        GenreIdsPresent, PlatformIdsPresent, PublisherIdsPresent, DeveloperIdsPresent, CategoryIdsPresent, TagIdsPresent,
        FeatureIdsPresent, SeriesIdsPresent, AgeRatingIdsPresent, RegionIdsPresent, LinksPresent, GameActionsPresent, RomsPresent, LegacyJson
        """;
    internal const string Parameters = """
        @Id, @Name, @SortingName, @GameId, @PluginId, @BackgroundImage, @Description, @Notes, @Icon, @CoverImage,
        @InstallDirectory, @Version, @Manual, @Hidden, @Favorite, @EnableSystemHdr, @IncludeLibraryPluginAction,
        @IsInstalled, @OverrideInstallState, @UseGlobalPreScript, @UseGlobalPostScript, @UseGlobalGameStartedScript,
        @PreScript, @PostScript, @GameStartedScript, @SourceId, @CompletionStatusId, @UserScore, @CriticScore, @CommunityScore,
        @ReleaseYear, @ReleaseMonth, @ReleaseDay, @LastActivity, @Added, @Modified, @LastSizeScanDate, @Playtime, @PlayCount, @InstallSize,
        @GenreIdsPresent, @PlatformIdsPresent, @PublisherIdsPresent, @DeveloperIdsPresent, @CategoryIdsPresent, @TagIdsPresent,
        @FeatureIdsPresent, @SeriesIdsPresent, @AgeRatingIdsPresent, @RegionIdsPresent, @LinksPresent, @GameActionsPresent, @RomsPresent, @LegacyJson
        """;

    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? SortingName { get; set; }
    public string? GameId { get; set; }
    public string PluginId { get; set; } = "";
    public string? BackgroundImage { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public string? Icon { get; set; }
    public string? CoverImage { get; set; }
    public string? InstallDirectory { get; set; }
    public string? Version { get; set; }
    public string? Manual { get; set; }
    public bool Hidden { get; set; }
    public bool Favorite { get; set; }
    public bool EnableSystemHdr { get; set; }
    public bool IncludeLibraryPluginAction { get; set; }
    public bool IsInstalled { get; set; }
    public bool OverrideInstallState { get; set; }
    public bool UseGlobalPreScript { get; set; }
    public bool UseGlobalPostScript { get; set; }
    public bool UseGlobalGameStartedScript { get; set; }
    public string? PreScript { get; set; }
    public string? PostScript { get; set; }
    public string? GameStartedScript { get; set; }
    public string? SourceId { get; set; }
    public string? CompletionStatusId { get; set; }
    public int? UserScore { get; set; }
    public int? CriticScore { get; set; }
    public int? CommunityScore { get; set; }
    public int? ReleaseYear { get; set; }
    public int? ReleaseMonth { get; set; }
    public int? ReleaseDay { get; set; }
    public string? LastActivity { get; set; }
    public string? Added { get; set; }
    public string? Modified { get; set; }
    public string? LastSizeScanDate { get; set; }
    public string Playtime { get; set; } = "";
    public string PlayCount { get; set; } = "";
    public string? InstallSize { get; set; }
    public bool GenreIdsPresent { get; set; }
    public bool PlatformIdsPresent { get; set; }
    public bool PublisherIdsPresent { get; set; }
    public bool DeveloperIdsPresent { get; set; }
    public bool CategoryIdsPresent { get; set; }
    public bool TagIdsPresent { get; set; }
    public bool FeatureIdsPresent { get; set; }
    public bool SeriesIdsPresent { get; set; }
    public bool AgeRatingIdsPresent { get; set; }
    public bool RegionIdsPresent { get; set; }
    public bool LinksPresent { get; set; }
    public bool GameActionsPresent { get; set; }
    public bool RomsPresent { get; set; }
    public string LegacyJson { get; set; } = "{}";

    internal static GameRow FromGame(StoredGame item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Value);
        var game = item.Value;
        StorageValues.RequireIdentity(game.Id);
        return new GameRow
        {
            Id = game.Id.ToString("D"), Name = game.Name, SortingName = game.SortingName, GameId = game.GameId,
            PluginId = game.PluginId.ToString("D"), BackgroundImage = game.BackgroundImage, Description = game.Description,
            Notes = game.Notes, Icon = game.Icon, CoverImage = game.CoverImage, InstallDirectory = game.InstallDirectory,
            Version = game.Version, Manual = game.Manual, Hidden = game.Hidden, Favorite = game.Favorite,
            EnableSystemHdr = game.EnableSystemHdr, IncludeLibraryPluginAction = game.IncludeLibraryPluginAction,
            IsInstalled = game.IsInstalled, OverrideInstallState = game.OverrideInstallState,
            UseGlobalPreScript = game.UseGlobalPreScript, UseGlobalPostScript = game.UseGlobalPostScript,
            UseGlobalGameStartedScript = game.UseGlobalGameStartedScript, PreScript = game.PreScript,
            PostScript = game.PostScript, GameStartedScript = game.GameStartedScript,
            SourceId = StorageValues.Reference(game.SourceId), CompletionStatusId = StorageValues.Reference(game.CompletionStatusId),
            UserScore = game.UserScore, CriticScore = game.CriticScore, CommunityScore = game.CommunityScore,
            ReleaseYear = game.ReleaseDate?.Year, ReleaseMonth = game.ReleaseDate?.Month, ReleaseDay = game.ReleaseDate?.Day,
            LastActivity = StorageValues.Timestamp(game.LastActivity), Added = StorageValues.Timestamp(game.Added),
            Modified = StorageValues.Timestamp(game.Modified), LastSizeScanDate = StorageValues.Timestamp(game.LastSizeScanDate),
            Playtime = StorageValues.Unsigned(game.Playtime), PlayCount = StorageValues.Unsigned(game.PlayCount),
            InstallSize = game.InstallSize.HasValue ? StorageValues.Unsigned(game.InstallSize.Value) : null,
            GenreIdsPresent = game.GenreIds != null, PlatformIdsPresent = game.PlatformIds != null,
            PublisherIdsPresent = game.PublisherIds != null, DeveloperIdsPresent = game.DeveloperIds != null,
            CategoryIdsPresent = game.CategoryIds != null, TagIdsPresent = game.TagIds != null,
            FeatureIdsPresent = game.FeatureIds != null, SeriesIdsPresent = game.SeriesIds != null,
            AgeRatingIdsPresent = game.AgeRatingIds != null, RegionIdsPresent = game.RegionIds != null,
            LinksPresent = game.Links != null, GameActionsPresent = game.GameActions != null, RomsPresent = game.Roms != null,
            LegacyJson = item.LegacyJson,
        };
    }

    internal Game ToGame() => new(Guid.Parse(Id), GameId!)
    {
        Name = Name, SortingName = SortingName, PluginId = Guid.Parse(PluginId), BackgroundImage = BackgroundImage,
        Description = Description, Notes = Notes, Icon = Icon, CoverImage = CoverImage, InstallDirectory = InstallDirectory,
        Version = Version, Manual = Manual, Hidden = Hidden, Favorite = Favorite, EnableSystemHdr = EnableSystemHdr,
        IncludeLibraryPluginAction = IncludeLibraryPluginAction, IsInstalled = IsInstalled, OverrideInstallState = OverrideInstallState,
        UseGlobalPreScript = UseGlobalPreScript, UseGlobalPostScript = UseGlobalPostScript,
        UseGlobalGameStartedScript = UseGlobalGameStartedScript, PreScript = PreScript, PostScript = PostScript,
        GameStartedScript = GameStartedScript, SourceId = StorageValues.Reference(SourceId),
        CompletionStatusId = StorageValues.Reference(CompletionStatusId), UserScore = UserScore,
        CriticScore = CriticScore, CommunityScore = CommunityScore,
        ReleaseDate = ReleaseYear == null ? null : ReleaseDay.HasValue
            ? new ReleaseDate(ReleaseYear.Value, ReleaseMonth!.Value, ReleaseDay.Value)
            : ReleaseMonth.HasValue ? new ReleaseDate(ReleaseYear.Value, ReleaseMonth.Value) : new ReleaseDate(ReleaseYear.Value),
        LastActivity = StorageValues.Timestamp(LastActivity), Added = StorageValues.Timestamp(Added),
        Modified = StorageValues.Timestamp(Modified), LastSizeScanDate = StorageValues.Timestamp(LastSizeScanDate),
        Playtime = StorageValues.Unsigned(Playtime), PlayCount = StorageValues.Unsigned(PlayCount),
        InstallSize = InstallSize == null ? null : StorageValues.Unsigned(InstallSize),
    };
}
