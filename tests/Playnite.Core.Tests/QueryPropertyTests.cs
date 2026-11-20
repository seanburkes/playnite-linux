using System.Collections.ObjectModel;
using System.Globalization;
using Playnite.Core.Models;
using Playnite.Core.Queries;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class QueryPropertyTests
{
    public static IEnumerable<object[]> SortCases => Enum.GetValues<SortOrder>()
        .SelectMany(order => new[] { new object[] { order, false }, new object[] { order, true } });

    [Theory]
    [MemberData(nameof(SortCases))]
    public void Sort_is_total_transitive_stable_and_commutes_with_filter(SortOrder order, bool descending)
    {
        var random = new Random(3006);
        var comparer = new GameComparer(order, descending);
        var matcher = new FilterMatcher(new FilterSettings { Favorite = true });
        for (var trial = 0; trial < 80; trial++)
        {
            var games = Enumerable.Range(0, 24).Select(i => Generate(random, i)).ToArray();
            foreach (var x in games)
            {
                Assert.Equal(0, comparer.Compare(x, x));
                var y = games[random.Next(games.Length)];
                var z = games[random.Next(games.Length)];
                Assert.Equal(-Math.Sign(comparer.Compare(y, x)), Math.Sign(comparer.Compare(x, y)));
                if (comparer.Compare(x, y) <= 0 && comparer.Compare(y, z) <= 0)
                {
                    Assert.True(comparer.Compare(x, z) <= 0);
                }
            }

            var sorted = comparer.Sort(games).ToArray();
            for (var i = 1; i < sorted.Length; i++)
            {
                Assert.True(comparer.Compare(sorted[i - 1], sorted[i]) <= 0);
                if (comparer.Compare(sorted[i - 1], sorted[i]) == 0)
                {
                    Assert.True(Array.IndexOf(games, sorted[i - 1]) < Array.IndexOf(games, sorted[i]));
                }
            }

            Assert.Equal(sorted.Select(g => g.Id), comparer.Sort(sorted).Select(g => g.Id));
            Assert.Equal(matcher.Filter(sorted).Select(g => g.Id), comparer.Sort(matcher.Filter(games)).Select(g => g.Id));
        }
    }

    [Fact]
    public void Filters_are_idempotent_and_GUID_membership_has_v10_AND_OR_semantics()
    {
        var random = new Random(6003);
        for (var trial = 0; trial < 500; trial++)
        {
            var ids = Enumerable.Range(0, random.Next(6)).Select(_ => Id(random.Next(5))).ToList();
            var wanted = Enumerable.Range(0, random.Next(1, 5)).Select(_ => Id(random.Next(5))).ToList();
            var game = Generate(random, trial);
            game.Hidden = false;
            game.GenreIds = trial % 7 == 0 ? null! : ids;
            foreach (var and in new[] { false, true })
            {
                var matcher = new FilterMatcher(new FilterSettings
                {
                    Genre = new() { Ids = wanted }, UseAndFilteringStyle = and
                });
                bool Contains(Guid id) => id == Guid.Empty ? game.GenreIds?.Count is null or 0 : game.GenreIds?.Contains(id) == true;
                Assert.Equal(and ? wanted.All(Contains) : wanted.Any(Contains), matcher.Match(game));
                var once = matcher.Filter([game]).Select(g => g.Id).ToArray();
                Assert.Equal(once, matcher.Filter(matcher.Filter([game])).Select(g => g.Id));
            }
        }
    }

    [Fact]
    public void Search_and_field_equality_preserve_case_and_the_correct_symmetries()
    {
        var random = new Random(300600);
        for (var trial = 0; trial < 1500; trial++)
        {
            var a = Word(random);
            var b = Word(random);
            Assert.Equal(GameFieldComparer.StringEquals(a, b), GameFieldComparer.StringEquals(b, a));
            Assert.True(GameFieldComparer.StringEquals(a, a.ToUpperInvariant()));
            Assert.True(GameFieldComparer.StringEquals(a, string.Join(" - ", a.ToCharArray())));
            Assert.True(TextSearch.MatchTextFilter(a, a.ToUpperInvariant(), true));
            Assert.Equal(TextSearch.MatchTextFilter(a, b, true),
                TextSearch.MatchTextFilter(a.ToUpperInvariant(), b.ToUpperInvariant(), true));
            Assert.Equal(a.GetLevenshteinDistanceIgnoreCase(b), b.GetLevenshteinDistanceIgnoreCase(a));
        }

        // Matching is directional; do not assert symmetry for a substring query.
        Assert.True(TextSearch.MatchTextFilter("doom", "doom eternal", true));
        Assert.False(TextSearch.MatchTextFilter("doom eternal", "doom", true));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(" ", null, true)]
    [InlineData("x", null, false)]
    [InlineData("cafe", "Café Racer", true)]
    [InlineData("racer cafe", "Café Racer", true)]
    [InlineData("hl", "Half Life 2", true)]
    [InlineData("hl", "Half-Life 2", false)]
    [InlineData("hl", "Half\tLife 2", false)]
    [InlineData("foo foo", "foo bar", true)]
    [InlineData("abcde", "abcd", true)]
    public void Search_pins_v10_token_acronym_and_similarity_quirks(string? filter, string? target, bool expected)
        => Assert.Equal(expected, TextSearch.MatchTextFilter(filter, target, true));

    [Theory]
    [InlineData("Zdoom", "doom", false, true)]
    [InlineData("x", "anything", false, true)]
    [InlineData("!doom", "DOOM Eternal", true, true)]
    [InlineData("^Dignored", "doom", true, true)]
    [InlineData("^d", "doom", true, false)]
    public void Name_filter_pins_unconditional_first_character_removal_and_group_case(string term, string name, bool fuzzy, bool expected)
        => Assert.Equal(expected, new FilterMatcher(new FilterSettings { Name = term }, fuzzy).Match(new Game(Id(1), "1", name)));

    [Fact]
    public void Hidden_is_an_equality_filter_and_installation_toggles_cancel_each_other()
    {
        foreach (var installed in new[] { false, true })
        foreach (var hidden in new[] { false, true })
        foreach (var a in new[] { false, true })
        foreach (var b in new[] { false, true })
        {
            var game = new Game(Id(1), "1") { IsInstalled = installed, Hidden = hidden };
            Assert.Equal(!hidden && (a == b || a == installed), new FilterMatcher(new FilterSettings { IsInstalled = a, IsUnInstalled = b }).Match(game));
            Assert.Equal(hidden && (a == b || a == installed), new FilterMatcher(new FilterSettings { Hidden = true, IsInstalled = a, IsUnInstalled = b }).Match(game));
        }
    }

    [Fact]
    public void Missing_single_ID_preserves_the_MaxGuid_workaround_but_library_does_not()
    {
        var max = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var game = new Game(Id(1), "1") { CompletionStatusId = max, SourceId = max, PluginId = max };
        Assert.True(new FilterMatcher(new FilterSettings { CompletionStatuses = new() { Ids = [Guid.Empty] }, Source = new() { Ids = [Guid.Empty] } }).Match(game));
        Assert.False(new FilterMatcher(new FilterSettings { Library = new() { Ids = [Guid.Empty] } }).Match(game));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Secondary_sort_is_always_ascending_effective_name_and_equal_keys_keep_input_order(bool descending)
    {
        var games = new[]
        {
            new Game(Id(1), "1", "Z") { SortingName = "A", Playtime = 5 },
            new Game(Id(2), "2", "B") { Playtime = 5 },
            new Game(Id(3), "3", "A") { Playtime = 5 }
        };
        Assert.Equal(new[] { Id(1), Id(3), Id(2) }, new GameComparer(SortOrder.Playtime, descending).Sort(games).Select(g => g.Id));
    }

    [Theory]
    [InlineData(SortOrder.UserScore)]
    [InlineData(SortOrder.InstallSize)]
    [InlineData(SortOrder.LastActivity)]
    [InlineData(SortOrder.ReleaseDate)]
    [InlineData(SortOrder.Version)]
    public void Missing_scalar_values_sort_first_ascending_and_last_descending(SortOrder order)
    {
        var absent = new Game(Id(1), "1", "Z");
        var present = new Game(Id(2), "2", "A") { UserScore = 0, InstallSize = 0, LastActivity = new DateTime(2000, 1, 1), ReleaseDate = new ReleaseDate(2000), Version = "" };
        Assert.True(new GameComparer(order).Compare(absent, present) < 0);
        Assert.True(new GameComparer(order, true).Compare(absent, present) > 0);
    }

    [Fact]
    public void Named_list_comparison_is_joined_ordered_case_sensitive_and_missing_last()
    {
        var a = new Genre(Id(1)) { Name = "A" };
        var b = new Genre(Id(2)) { Name = "B" };
        var list = new ComparableDbItemList<Genre>([a, b]);
        Assert.Equal(string.Join(", ", new[] { a, b }).CompareTo("B, A"), list.CompareTo(new List<Genre> { b, a }));
        Assert.Equal(-1, list.CompareTo(new List<Genre>()));
        Assert.Equal(1, new ComparableDbItemList<Genre>(null).CompareTo(list));
        Assert.Equal(-1, list.CompareTo(null));
        Assert.Throws<InvalidCastException>(() => list.CompareTo("A, B"));
    }

    [Fact]
    public void Named_lists_have_a_total_order_on_lists_and_preserve_current_culture_joined_comparison()
    {
        var random = new Random(630063);
        ComparableDbItemList<Genre> List() => new(Enumerable.Range(0, random.Next(5))
            .Select(i => new Genre(Id(i)) { Name = Word(random) }));
        for (var trial = 0; trial < 2000; trial++)
        {
            var x = List();
            var y = List();
            var z = List();
            Assert.Equal(0, x.CompareTo(x));
            Assert.Equal(-Math.Sign(y.CompareTo(x)), Math.Sign(x.CompareTo(y)));
            if (x.CompareTo(y) <= 0 && y.CompareTo(z) <= 0)
            {
                Assert.True(x.CompareTo(z) <= 0);
            }

            if (x.Count > 0 && y.Count > 0)
            {
                Assert.Equal(string.Join(", ", x).CompareTo(string.Join(", ", y)), x.CompareTo(y));
            }
        }
    }

    public static IEnumerable<object[]> ScalarSortFields => new Dictionary<SortOrder, (string, object, object)>
    {
        [SortOrder.Name] = ("Name", "A", "Z"),
        [SortOrder.LastActivity] = ("LastActivity", new DateTime(2000, 1, 1), new DateTime(2001, 1, 1)),
        [SortOrder.ReleaseDate] = ("ReleaseDate", new ReleaseDate(2000), new ReleaseDate(2001)),
        [SortOrder.Version] = ("Version", "10", "2"), // string, not semantic-version ordering
        [SortOrder.PlayCount] = ("PlayCount", 0UL, ulong.MaxValue),
        [SortOrder.Playtime] = ("Playtime", 0UL, ulong.MaxValue),
        [SortOrder.UserScore] = ("UserScore", 0, 100),
        [SortOrder.CriticScore] = ("CriticScore", 0, 100),
        [SortOrder.CommunityScore] = ("CommunityScore", 0, 100),
        [SortOrder.Added] = ("Added", new DateTime(2000, 1, 1), new DateTime(2001, 1, 1)),
        [SortOrder.Modified] = ("Modified", new DateTime(2000, 1, 1), new DateTime(2001, 1, 1)),
        [SortOrder.IsInstalled] = ("IsInstalled", false, true),
        [SortOrder.Hidden] = ("Hidden", false, true),
        [SortOrder.Favorite] = ("Favorite", false, true),
        [SortOrder.InstallDirectory] = ("InstallDirectory", "/a", "/z"),
        [SortOrder.InstallSize] = ("InstallSize", 0UL, ulong.MaxValue),
        [SortOrder.RecentActivity] = ("Added", new DateTime(2000, 1, 1), new DateTime(2001, 1, 1)),
        [SortOrder.RomList] = ("Roms", new ObservableCollection<GameRom> { new() { Path = "a" } }, new ObservableCollection<GameRom> { new() { Path = "z" } })
    }.Select(pair => new object[] { pair.Key, pair.Value.Item1, pair.Value.Item2, pair.Value.Item3 });

    [Theory]
    [MemberData(nameof(ScalarSortFields))]
    public void Each_sort_key_uses_the_correct_primary_stored_field(SortOrder order, string field, object low, object high)
    {
        var a = new Game(Id(1), "1", "Z");
        var b = new Game(Id(2), "2", "A");
        typeof(Game).GetProperty(field)!.SetValue(a, low);
        typeof(Game).GetProperty(field)!.SetValue(b, high);
        Assert.True(new GameComparer(order).Compare(a, b) < 0);
        Assert.True(new GameComparer(order, true).Compare(a, b) > 0);
    }

    [Fact]
    public void Field_equality_distinguishes_empty_from_normalized_empty_and_retains_hash_quirk()
    {
        Assert.True(GameFieldComparer.StringEquals(null, ""));
        Assert.False(GameFieldComparer.StringEquals("", " - "));
        Assert.True(GameFieldComparer.StringEquals(" - ", "-"));
        Assert.False(GameFieldComparer.StringEquals("cafe", "café"));
        Assert.Throws<ArgumentNullException>(() => GameFieldComparer.Instance.GetHashCode(null));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(GameFieldComparer.StringEquals("I", "i"));
            Assert.Equal("I".ToLower().GetHashCode(), GameFieldComparer.Instance.GetHashCode("I"));
            Assert.NotEqual(GameFieldComparer.Instance.GetHashCode("I"), GameFieldComparer.Instance.GetHashCode("i"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Search_rank_uses_distance_then_display_name_then_installed_and_caps_at_sixty()
    {
        var a = new Game(Id(1), "1", "doom") { SortingName = "Z" };
        var b = new Game(Id(2), "2", "doom") { SortingName = "A", IsInstalled = true };
        var c = new Game(Id(3), "3", "doom") { IsInstalled = true };
        Assert.Equal(new[] { b.Id, c.Id, a.Id }, SearchResults.Rank([a, b, c], "DOOM").Select(g => g.Id));
        Assert.Equal(60, SearchResults.Rank(Enumerable.Range(0, 100).Select(i => new Game(Id(i), i.ToString(), "doom")), "doom").Count());
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static string Word(Random random) => new(Enumerable.Range(0, random.Next(1, 15)).Select(_ => "abcde ijklmn-opqr"[random.Next(16)]).ToArray());

    private static Game Generate(Random random, int i)
    {
        string? Text() => random.Next(4) switch { 0 => null, 1 => "", 2 => "a", _ => "B" };
        DateTime? Date() => random.Next(3) == 0 ? null : new DateTime(2020, 1, 1).AddDays(random.Next(3));
        int? Score() => random.Next(3) == 0 ? null : random.Next(101);
        return new Game(Id(i + 1), i.ToString(), Text()!)
        {
            SortingName = Text()!, Version = Text()!, InstallDirectory = Text()!,
            LastActivity = Date(), Added = Date(), Modified = Date(),
            ReleaseDate = random.Next(3) == 0 ? null : new ReleaseDate(2000 + random.Next(3)),
            UserScore = Score(), CriticScore = Score(), CommunityScore = Score(),
            PlayCount = (ulong)random.Next(3), Playtime = (ulong)random.Next(3),
            InstallSize = random.Next(3) == 0 ? null : (ulong)random.Next(3),
            Favorite = random.Next(2) == 0, Hidden = random.Next(2) == 0, IsInstalled = random.Next(2) == 0,
            Roms = random.Next(2) == 0 ? null! : new ObservableCollection<GameRom> { new() { Path = Text()! } }
        };
    }
}
