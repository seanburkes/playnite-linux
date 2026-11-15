using Playnite.Core.Models;
using Playnite.Core.Queries;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class QueryDateAndFilterTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0);

    [Theory]
    [InlineData(null, PastTimeSegment.Never)]
    [InlineData(0.0, PastTimeSegment.Today)]
    [InlineData(0.4, PastTimeSegment.Today)]
    [InlineData(0.6, PastTimeSegment.Yesterday)]
    [InlineData(1.4, PastTimeSegment.Yesterday)]
    [InlineData(2.0, PastTimeSegment.PastWeek)]
    [InlineData(6.999, PastTimeSegment.PastWeek)]
    [InlineData(7.0, PastTimeSegment.PastMonth)]
    [InlineData(30.999, PastTimeSegment.PastMonth)]
    [InlineData(31.0, PastTimeSegment.PastYear)]
    [InlineData(364.999, PastTimeSegment.PastYear)]
    [InlineData(365.0, PastTimeSegment.MoreThenYear)]
    [InlineData(-2.0, PastTimeSegment.PastWeek)]
    public void Date_segments_keep_v10_calendar_checks_strict_elapsed_boundaries_and_future_quirk(double? daysAgo, PastTimeSegment expected)
        => Assert.Equal(expected, new DateSegmentCalculator().GetSegment(daysAgo == null ? null : Now.AddDays(-daysAgo.Value), Now));

    [Fact]
    public void Date_filters_use_caller_clock_and_calculator_for_each_stored_date()
    {
        var dates = new[] { Now.AddDays(-20), Now.AddDays(-10), Now.AddDays(-3) };
        var game = new Game(Guid.Empty, "1") { Added = dates[0], Modified = dates[1], LastActivity = dates[2] };
        var calculator = new RecordingCalculator();
        var filter = new EnumFilterItemProperties { Values = [(int)PastTimeSegment.Today] };
        var matcher = new FilterMatcher(new FilterSettings
        {
            Added = filter, Modified = filter, LastActivity = filter, RecentActivity = filter
        }, clock: new Clock(Now), segmentCalculator: calculator);
        Assert.True(matcher.Match(game));
        Assert.Equal(new DateTime?[] { dates[2], dates[2], dates[0], dates[1] }, calculator.Values);
        Assert.All(calculator.Clocks, clock => Assert.Equal(Now, clock));
    }

    [Fact]
    public void Active_date_filters_require_context_but_inactive_filters_do_not_read_it()
    {
        var game = new Game(Guid.Empty, "1");
        Assert.True(new FilterMatcher(new FilterSettings { Added = new() { Values = [] } }).Match(game));
        var settings = new FilterSettings { Added = new() { Values = [(int)PastTimeSegment.Never] } };
        Assert.Throws<InvalidOperationException>(() => new FilterMatcher(settings).Match(game));
        Assert.Throws<InvalidOperationException>(() => new FilterMatcher(settings, clock: new Clock(Now)).Match(game));
        Assert.True(new FilterMatcher(settings, clock: new Clock(Now), segmentCalculator: new DateSegmentCalculator()).Match(game));
    }

    [Fact]
    public void Date_bucket_filters_are_idempotent_under_a_fixed_clock_and_match_bucket_membership()
    {
        var random = new Random(63006);
        var calculator = new DateSegmentCalculator();
        for (var trial = 0; trial < 1000; trial++)
        {
            DateTime? value = trial % 11 == 0 ? null : Now.AddMinutes(random.Next(-700000, 10000));
            var game = new Game(Guid.Empty, "1") { Added = value };
            var selected = Enum.GetValues<PastTimeSegment>().Where(_ => random.Next(2) == 0).Select(a => (int)a).ToList();
            var matcher = new FilterMatcher(new FilterSettings { Added = new() { Values = selected } },
                clock: new Clock(Now), segmentCalculator: calculator);
            Assert.Equal(selected.Count == 0 || selected.Contains((int)calculator.GetSegment(value, Now)), matcher.Match(game));
            Assert.Equal(matcher.Filter([game]).Count(), matcher.Filter(matcher.Filter([game])).Count());
        }
    }

    public static IEnumerable<object[]> IdListFields => new[]
    {
        nameof(FilterSettings.Genre), nameof(FilterSettings.Platform), nameof(FilterSettings.Publisher),
        nameof(FilterSettings.Developer), nameof(FilterSettings.Category), nameof(FilterSettings.Tag),
        nameof(FilterSettings.Series), nameof(FilterSettings.Region), nameof(FilterSettings.AgeRating), nameof(FilterSettings.Feature)
    }.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(IdListFields))]
    public void Every_ID_list_filter_is_bound_to_its_corresponding_stored_field(string field)
    {
        var mappings = new Dictionary<string, string>
        {
            ["Genre"] = "GenreIds", ["Platform"] = "PlatformIds", ["Publisher"] = "PublisherIds",
            ["Developer"] = "DeveloperIds", ["Category"] = "CategoryIds", ["Tag"] = "TagIds",
            ["Series"] = "SeriesIds", ["Region"] = "RegionIds", ["AgeRating"] = "AgeRatingIds", ["Feature"] = "FeatureIds"
        };
        var id = Guid.Parse("00000001-0000-0000-0000-000000000000");
        var settings = new FilterSettings();
        typeof(FilterSettings).GetProperty(field)!.SetValue(settings, new IdItemFilterItemProperties { Ids = [id] });
        var matcher = new FilterMatcher(settings);
        var game = new Game(Guid.Empty, "1");
        Assert.False(matcher.Match(game));
        typeof(Game).GetProperty(mappings[field])!.SetValue(game, new List<Guid> { id });
        Assert.True(matcher.Match(game));
    }

    [Fact]
    public void Scalar_filters_conjoin_version_year_score_playtime_size_and_favorite()
    {
        var game = new Game(Guid.Empty, "1")
        {
            Version = "Beta-1.2", ReleaseDate = new ReleaseDate(2020), UserScore = 75,
            CommunityScore = 90, CriticScore = 50, Playtime = 3600, InstallSize = 1024, Favorite = true
        };
        var settings = new FilterSettings
        {
            Version = "BETA", ReleaseYear = new() { Values = ["2020"] }, Favorite = true,
            UserScore = new() { Values = [(int)game.UserScoreGroup] },
            CommunityScore = new() { Values = [(int)game.CommunityScoreGroup] },
            CriticScore = new() { Values = [(int)game.CriticScoreGroup] },
            PlayTime = new() { Values = [(int)game.PlaytimeCategory] },
            InstallSize = new() { Values = [(int)game.InstallSizeGroup] }
        };
        var matcher = new FilterMatcher(settings);
        Assert.True(matcher.Match(game));
        foreach (var field in new[] { "UserScore", "CommunityScore", "CriticScore", "PlayTime", "InstallSize" })
        {
            var property = typeof(FilterSettings).GetProperty(field)!;
            var previous = property.GetValue(settings);
            property.SetValue(settings, new EnumFilterItemProperties { Values = [int.MinValue] });
            Assert.False(matcher.Match(game));
            property.SetValue(settings, previous);
        }

        game.ReleaseDate = null;
        Assert.False(matcher.Match(game));
        settings.ReleaseYear.Values = [FilterSettings.MissingFieldString];
        Assert.True(matcher.Match(game));
        game.Version = null!;
        Assert.False(matcher.Match(game));
        settings.Version = "";
        Assert.True(matcher.Match(game));
        game.Favorite = false;
        Assert.False(matcher.Match(game));
    }

    private sealed class Clock(DateTime now) : IQueryClock
    {
        public DateTime Now => now;
    }

    private sealed class RecordingCalculator : IDateSegmentCalculator
    {
        public List<DateTime?> Values { get; } = [];
        public List<DateTime> Clocks { get; } = [];
        public PastTimeSegment GetSegment(DateTime? value, DateTime now)
        {
            Values.Add(value);
            Clocks.Add(now);
            return PastTimeSegment.Today;
        }
    }
}
