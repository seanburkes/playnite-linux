using System.Diagnostics;
using Playnite.Core.Models;
using Xunit;
using PlatformModel = Playnite.Core.Models.Platform;

namespace Playnite.Core.Tests.Models;

public sealed class ModelBehaviorTests
{
    [Fact]
    public void Empty_entities_compare_equal_across_types_but_are_not_the_same_reference()
    {
        DatabaseObject[] values = [AgeRating.Empty, Category.Empty, Company.Empty, CompletionStatus.Empty,
            GameFeature.Empty, GameSource.Empty, Genre.Empty, PlatformModel.Empty, Region.Empty, Series.Empty, Tag.Empty];
        foreach (var a in values)
        foreach (var b in values)
        {
            Assert.True(a.Equals(b));
            Assert.Equal(0, a.GetHashCode());
            Assert.Equal(string.Empty, a.Name);
        }
        Assert.NotSame(AgeRating.Empty, Category.Empty);
    }

    [Fact]
    public void Mutable_identity_changes_equality_and_hash_without_a_name_change()
    {
        var a = new DatabaseObject(new Guid("12345678-1234-5678-90ab-cdef01234567"));
        var b = new Category(a.Id, "unrelated");
        Assert.True(a.Equals(b));
        a.Id = Guid.Empty;
        Assert.False(a.Equals(b));
        Assert.Equal(0, a.GetHashCode());
        Assert.Equal(b.Id.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Name", "name")]
    [InlineData("/Games/A", "/games/a")]
    [InlineData("é", "e\u0301")]
    public void Typed_string_equality_is_ordinal_and_does_not_normalize(string? a, string? b)
    {
        Assert.False(new Link(a!, "same").Equals(new Link(b!, "same")));
        Assert.False(new GameRom("same", a!).Equals(new GameRom("same", b!)));
        Assert.False(new GameAction { Name = a! }.Equals(new GameAction { Name = b! }));
    }

    [Fact]
    public void Entity_notifications_keep_v10_order_even_for_deferred_navigation_names()
    {
        var game = ModelProbe.NewGame();
        var events = new List<string?>();
        game.PropertyChanged += (_, e) => events.Add(e.PropertyName);
        game.GenreIds = [];
        game.UserScore = 30;
        game.Added = null;
        // Reassigning the same value still notifies in v10.
        game.Added = null;
        Assert.Equal(["GenreIds", "Genres", "UserScore", "UserScoreGroup", "UserScoreRating",
            "Added", "AddedSegment", "RecentActivity", "Added", "AddedSegment", "RecentActivity"], events);
    }

    [Fact]
    public void Copies_preserve_values_but_copydiff_preserves_collection_aliasing()
    {
        var source = ModelProbe.NewGame();
        source.Links = [new Link("Name", "https://example.invalid")];
        source.Roms = [new GameRom("ROM", "/games/a.rom")];
        source.GameActions = [new GameAction { Name = "Play" }];
        var copy = source.GetCopy();
        Assert.Empty(source.GetDifferences(copy));
        Assert.True(source.Equals(copy));
        Assert.NotSame(source, copy);
        Assert.NotSame(source.Links[0], copy.Links[0]);
        copy.Links[0].Name = "changed";
        Assert.Equal("Name", source.Links[0].Name);
        source.CopyDiffTo(copy);
        Assert.Same(source.Links, copy.Links);
        // Equal collections are not assigned just to acquire the source reference.
        Assert.NotSame(source.Roms, copy.Roms);
    }

    [Fact]
    public void Copydiff_base_name_mutates_before_a_derived_type_error()
    {
        var source = new Region(Guid.Empty, "source") { SpecificationId = "region" };
        var target = new Category(Guid.Empty, "target");
        Assert.Throws<ArgumentException>(() => source.CopyDiffTo(target));
        Assert.Equal("source", target.Name);
    }

    [Fact]
    public void Platform_and_region_copy_all_their_nonidentity_fields()
    {
        var source = new PlatformModel(Guid.Empty, "source")
            { SpecificationId = "platform", Icon = "icon", Cover = "cover", Background = "background" };
        var targetId = new Guid("12345678-1234-5678-90ab-cdef01234567");
        var target = new PlatformModel(targetId);
        source.CopyDiffTo(target);
        Assert.Equal("source", target.Name);
        Assert.Equal("platform", target.SpecificationId);
        Assert.Equal("icon", target.Icon);
        Assert.Equal("cover", target.Cover);
        Assert.Equal("background", target.Background);
        Assert.Equal(targetId, target.Id);
        var region = new Region(Guid.Empty, "source") { SpecificationId = "region" };
        var regionTarget = new Region(targetId);
        region.CopyDiffTo(regionTarget);
        Assert.Equal("region", regionTarget.SpecificationId);
        Assert.Equal("source", regionTarget.Name);
        Assert.Equal(targetId, regionTarget.Id);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("2024", true)]
    [InlineData("2024-2", true)]
    [InlineData("2024-2-29", true)]
    [InlineData("2023-2-29", false)]
    [InlineData("2024-13", false)]
    [InlineData("2024-1-1-extra", false)]
    public void Release_date_tryparse_keeps_the_v10_partial_date_contract(string? text, bool valid)
    {
        Assert.Equal(valid, ReleaseDate.TryDeserialize(text!, out var date));
        if (valid) Assert.Equal(ReleaseDate.Deserialize(text!), date);
        else Assert.Equal(ReleaseDate.Empty, date);
    }

    [Fact]
    public void Release_date_roundtrips_all_precisions_over_a_seeded_valid_date_domain()
    {
        var random = new Random(4004);
        for (var i = 0; i < 500; i++)
        {
            var year = random.Next(1, 10000);
            var month = random.Next(1, 13);
            var day = random.Next(1, DateTime.DaysInMonth(year, month) + 1);
            foreach (var value in new[] { new ReleaseDate(year), new ReleaseDate(year, month), new ReleaseDate(year, month, day) })
            {
                Assert.Equal(value, ReleaseDate.Deserialize(value.Serialize()));
                Assert.Equal(value.GetHashCode(), ReleaseDate.Deserialize(value.Serialize()).GetHashCode());
            }
        }
    }

    [Fact]
    public void Release_date_fuzz_is_bounded_and_tryparse_does_not_throw()
    {
        var random = new Random(4005);
        const string alphabet = "0123456789-+ \t\0é";
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            var text = new string(Enumerable.Range(0, random.Next(65))
                .Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var valid = ReleaseDate.TryDeserialize(text, out var date);
            if (valid) Assert.Equal(date, ReleaseDate.Deserialize(text));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"ReleaseDate fuzz exceeded budget at iteration {i}, seed 4005.");
        }
    }

    [Fact]
    public void Deferred_database_and_ambient_members_are_absent_instead_of_stubbed()
    {
        foreach (var name in new[] { "Genres", "Developers", "Publishers", "Tags", "Features", "Categories",
            "Platforms", "Series", "AgeRatings", "Regions", "Source", "CompletionStatus", "LastActivitySegment",
            "RecentActivitySegment", "AddedSegment", "ModifiedSegment", "DatabaseReference" })
            Assert.Null(typeof(Game).GetProperty(name, System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
        Assert.Null(typeof(Game).GetMethod("GetInstallDrive"));
        Assert.Null(typeof(Game).GetMethod("GetInstallDriveGroup"));
        Assert.Null(typeof(Game).GetConstructor(Type.EmptyTypes));
        Assert.Null(typeof(DatabaseObject).GetConstructor(Type.EmptyTypes));
    }
}
