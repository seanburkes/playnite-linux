namespace Playnite.Core.Database;

internal static class LibrarySchema
{
    internal const string Version1 = """
        CREATE TABLE tag(Id TEXT PRIMARY KEY NOT NULL, Name TEXT, LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));
        CREATE TABLE genre(Id TEXT PRIMARY KEY NOT NULL, Name TEXT, LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));
        CREATE TABLE company(Id TEXT PRIMARY KEY NOT NULL, Name TEXT, LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));
        CREATE TABLE feature(Id TEXT PRIMARY KEY NOT NULL, Name TEXT, LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));
        CREATE TABLE series(Id TEXT PRIMARY KEY NOT NULL, Name TEXT, LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));
        CREATE TABLE category(Id TEXT PRIMARY KEY NOT NULL, Name TEXT, LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));
        CREATE TABLE source(Id TEXT PRIMARY KEY NOT NULL, Name TEXT, LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));
        CREATE TABLE age_rating(Id TEXT PRIMARY KEY NOT NULL, Name TEXT, LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));
        CREATE TABLE region(Id TEXT PRIMARY KEY NOT NULL, Name TEXT, LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));
        CREATE TABLE completion_status(Id TEXT PRIMARY KEY NOT NULL, Name TEXT, LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));
        CREATE TABLE platform(
            Id TEXT PRIMARY KEY NOT NULL, Name TEXT, SpecificationId TEXT, Icon TEXT, Cover TEXT, Background TEXT,
            LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));

        CREATE TABLE game(
            Id TEXT PRIMARY KEY NOT NULL CHECK(length(Id)=36 AND Id=lower(Id)),
            Name TEXT, SortingName TEXT, GameId TEXT, PluginId TEXT NOT NULL,
            BackgroundImage TEXT, Description TEXT, Notes TEXT, Icon TEXT, CoverImage TEXT,
            InstallDirectory TEXT, Version TEXT, Manual TEXT,
            Hidden INTEGER NOT NULL, Favorite INTEGER NOT NULL, EnableSystemHdr INTEGER NOT NULL,
            IncludeLibraryPluginAction INTEGER NOT NULL, IsInstalled INTEGER NOT NULL, OverrideInstallState INTEGER NOT NULL,
            UseGlobalPreScript INTEGER NOT NULL, UseGlobalPostScript INTEGER NOT NULL, UseGlobalGameStartedScript INTEGER NOT NULL,
            PreScript TEXT, PostScript TEXT, GameStartedScript TEXT,
            SourceId TEXT REFERENCES source(Id), CompletionStatusId TEXT REFERENCES completion_status(Id),
            UserScore INTEGER, CriticScore INTEGER, CommunityScore INTEGER,
            ReleaseYear INTEGER, ReleaseMonth INTEGER, ReleaseDay INTEGER,
            LastActivity TEXT, Added TEXT, Modified TEXT, LastSizeScanDate TEXT,
            Playtime TEXT NOT NULL CHECK(length(Playtime)=20 AND Playtime NOT GLOB '*[^0-9]*' AND Playtime<='18446744073709551615'),
            PlayCount TEXT NOT NULL CHECK(length(PlayCount)=20 AND PlayCount NOT GLOB '*[^0-9]*' AND PlayCount<='18446744073709551615'),
            InstallSize TEXT CHECK(InstallSize IS NULL OR (length(InstallSize)=20 AND InstallSize NOT GLOB '*[^0-9]*' AND InstallSize<='18446744073709551615')),
            GenreIdsPresent INTEGER NOT NULL, PlatformIdsPresent INTEGER NOT NULL, PublisherIdsPresent INTEGER NOT NULL,
            DeveloperIdsPresent INTEGER NOT NULL, CategoryIdsPresent INTEGER NOT NULL, TagIdsPresent INTEGER NOT NULL,
            FeatureIdsPresent INTEGER NOT NULL, SeriesIdsPresent INTEGER NOT NULL, AgeRatingIdsPresent INTEGER NOT NULL,
            RegionIdsPresent INTEGER NOT NULL, LinksPresent INTEGER NOT NULL, GameActionsPresent INTEGER NOT NULL, RomsPresent INTEGER NOT NULL,
            LegacyJson TEXT NOT NULL CHECK(json_valid(LegacyJson) AND json_type(LegacyJson)='object'));

        CREATE TABLE game_tag(GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL, ItemId TEXT NOT NULL REFERENCES tag(Id), PRIMARY KEY(GameId, Position));
        CREATE TABLE game_genre(GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL, ItemId TEXT NOT NULL REFERENCES genre(Id), PRIMARY KEY(GameId, Position));
        CREATE TABLE game_platform(GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL, ItemId TEXT NOT NULL REFERENCES platform(Id), PRIMARY KEY(GameId, Position));
        CREATE TABLE game_publisher(GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL, ItemId TEXT NOT NULL REFERENCES company(Id), PRIMARY KEY(GameId, Position));
        CREATE TABLE game_developer(GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL, ItemId TEXT NOT NULL REFERENCES company(Id), PRIMARY KEY(GameId, Position));
        CREATE TABLE game_category(GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL, ItemId TEXT NOT NULL REFERENCES category(Id), PRIMARY KEY(GameId, Position));
        CREATE TABLE game_feature(GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL, ItemId TEXT NOT NULL REFERENCES feature(Id), PRIMARY KEY(GameId, Position));
        CREATE TABLE game_series(GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL, ItemId TEXT NOT NULL REFERENCES series(Id), PRIMARY KEY(GameId, Position));
        CREATE TABLE game_age_rating(GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL, ItemId TEXT NOT NULL REFERENCES age_rating(Id), PRIMARY KEY(GameId, Position));
        CREATE TABLE game_region(GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL, ItemId TEXT NOT NULL REFERENCES region(Id), PRIMARY KEY(GameId, Position));
        CREATE TABLE game_link(
            GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL,
            Name TEXT, Url TEXT, PRIMARY KEY(GameId, Position));
        CREATE TABLE game_rom(
            GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL,
            Name TEXT, Path TEXT, PRIMARY KEY(GameId, Position));
        CREATE TABLE game_action(
            GameId TEXT NOT NULL REFERENCES game(Id) ON DELETE CASCADE, Position INTEGER NOT NULL,
            Type INTEGER NOT NULL, Name TEXT, Path TEXT, Arguments TEXT, WorkingDir TEXT,
            AdditionalArguments TEXT, OverrideDefaultArgs INTEGER NOT NULL, IsPlayAction INTEGER NOT NULL,
            EmulatorId TEXT NOT NULL, EmulatorProfileId TEXT, TrackingMode INTEGER NOT NULL,
            TrackingPath TEXT, Script TEXT, InitialTrackingDelay INTEGER NOT NULL, TrackingFrequency INTEGER NOT NULL,
            PRIMARY KEY(GameId, Position));
        """;

    internal const string Version2 = """
        CREATE INDEX game_provider ON game(PluginId, GameId);
        CREATE INDEX game_sort_name ON game(SortingName, Name, Id);
        CREATE INDEX game_last_activity ON game(LastActivity);
        CREATE INDEX game_playtime ON game(Playtime);
        CREATE INDEX game_source ON game(SourceId);
        CREATE INDEX game_completion_status ON game(CompletionStatusId);
        CREATE INDEX game_tag_item ON game_tag(ItemId);
        CREATE INDEX game_genre_item ON game_genre(ItemId);
        CREATE INDEX game_platform_item ON game_platform(ItemId);
        CREATE INDEX game_publisher_item ON game_publisher(ItemId);
        CREATE INDEX game_developer_item ON game_developer(ItemId);
        CREATE INDEX game_category_item ON game_category(ItemId);
        CREATE INDEX game_feature_item ON game_feature(ItemId);
        CREATE INDEX game_series_item ON game_series(ItemId);
        CREATE INDEX game_age_rating_item ON game_age_rating(ItemId);
        CREATE INDEX game_region_item ON game_region(ItemId);
        """;
}
