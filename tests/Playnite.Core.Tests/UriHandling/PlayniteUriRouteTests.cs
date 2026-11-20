using Playnite.Core.UriHandling;
using Xunit;
using Playnite.SDK;

namespace Playnite.Core.Tests.UriHandling;

public sealed class PlayniteUriRouteTests
{
    private const string GameId = "6a0d9f1e-4a2b-4c3d-8e9f-0123456789ab";
    private static readonly Guid Game = Guid.Parse(GameId);
    private const string EntityId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Start_with_action_index_is_parsed_and_validated()
    {
        Assert.True(PlayniteUriRoute.TryParse(
            $"playnite://playnite/start/{GameId}/2",
            out var route,
            out var error));

        Assert.Null(error);
        Assert.NotNull(route);
        Assert.Equal(PlayniteUriTarget.StartGame, route.Target);
        Assert.Equal("playnite", route.Source);
        Assert.Equal(Game, route.GameId);
        Assert.Equal(2, route.ActionIndex);
        Assert.Equal(["start", GameId, "2"], route.Arguments.ToArray());
    }

    [Fact]
    public void Start_without_action_index_keeps_the_action_absent()
    {
        Assert.True(PlayniteUriRoute.TryParse(
            $"playnite://playnite/start/{GameId}",
            out var route,
            out _));

        Assert.NotNull(route);
        Assert.Equal(Game, route.GameId);
        Assert.Null(route.ActionIndex);
    }

    [Fact]
    public void Show_game_is_parsed()
    {
        Assert.True(PlayniteUriRoute.TryParse(
            $"playnite://playnite/showgame/{GameId}",
            out var route,
            out _));

        Assert.NotNull(route);
        Assert.Equal(PlayniteUriTarget.ShowGame, route.Target);
        Assert.Equal(Game, route.GameId);
    }

    [Fact]
    public void Search_term_is_percent_decoded_and_plus_is_a_space()
    {
        Assert.True(PlayniteUriRoute.TryParse(
            "playnite://playnite/search/half+life%3A%20episode%20one",
            out var route,
            out _));

        Assert.NotNull(route);
        Assert.Equal(PlayniteUriTarget.Search, route.Target);
        Assert.Equal("half life: episode one", route.SearchTerm);
    }

    [Fact]
    public void Search_without_a_term_is_valid()
    {
        Assert.True(PlayniteUriRoute.TryParse("playnite://playnite/search", out var route, out _));

        Assert.NotNull(route);
        Assert.Equal(PlayniteUriTarget.Search, route.Target);
        Assert.Null(route.SearchTerm);
    }

    [Theory]
    [InlineData("desktop", PlayniteViewTarget.Desktop)]
    [InlineData("fullscreen", PlayniteViewTarget.Fullscreen)]
    public void View_target_is_parsed(string value, PlayniteViewTarget expected)
    {
        Assert.True(PlayniteUriRoute.TryParse(
            $"playnite://playnite/view/{value}",
            out var route,
            out _));

        Assert.NotNull(route);
        Assert.Equal(PlayniteUriTarget.ShowView, route.Target);
        Assert.Equal(expected, route.View);
    }

    [Theory]
    [InlineData("favorite", "true", PlayniteFilterField.Favorite, "true")]
    [InlineData("hidden", "false", PlayniteFilterField.Hidden, "false")]
    [InlineData("genre", EntityId, PlayniteFilterField.Genre, EntityId)]
    [InlineData("release-year", "2026", PlayniteFilterField.ReleaseYear, "2026")]
    [InlineData("name", "Portal+2", PlayniteFilterField.Name, "Portal 2")]
    public void Filter_target_is_parsed_and_normalized(
        string field,
        string value,
        PlayniteFilterField expectedField,
        string expectedValue)
    {
        Assert.True(PlayniteUriRoute.TryParse(
            $"playnite://playnite/filter/{field}/{value}",
            out var route,
            out var error));

        Assert.Null(error);
        Assert.NotNull(route);
        Assert.Equal(PlayniteUriTarget.Filter, route.Target);
        var filter = route.Filter;
        Assert.NotNull(filter);
        Assert.Equal(expectedField, filter.Field);
        Assert.Equal(expectedValue, filter.Value);
    }

    [Fact]
    public void Guid_filter_value_is_canonicalized()
    {
        const string compact = "11111111222233334444555555555555";
        Assert.True(PlayniteUriRoute.TryParse(
            $"playnite://playnite/filter/platform/{compact}",
            out var route,
            out _));

        Assert.NotNull(route);
        var filter = route.Filter;
        Assert.NotNull(filter);
        Assert.Equal(EntityId, filter.Value);
    }

    [Fact]
    public void Custom_source_is_parsed_with_every_argument()
    {
        Assert.True(PlayniteUriRoute.TryParse(
            "playnite://my-plugin/open/%E2%9C%93/a+b",
            out var route,
            out _));

        Assert.NotNull(route);
        Assert.Equal(PlayniteUriTarget.CustomSource, route.Target);
        Assert.Equal("my-plugin", route.Source);
        Assert.Equal(["open", "\u2713", "a b"], route.Arguments);
        Assert.Null(route.GameId);
    }

    [Theory]
    [InlineData("PLAYNITE://playnite/search/x")]
    [InlineData("Playnite://PLAYNITE/search/x")]
    public void Scheme_and_app_source_are_case_insensitive(string uri)
    {
        Assert.True(PlayniteUriRoute.TryParse(uri, out var route, out var error));

        Assert.Null(error);
        Assert.Equal(PlayniteUriTarget.Search, route!.Target);
    }

    [Theory]
    [InlineData("a.b_c~d-e")]
    [InlineData("plugin")]
    [InlineData("0")]
    public void Legal_source_names_are_accepted(string source)
    {
        Assert.True(PlayniteUriRoute.TryParse($"playnite://{source}", out var route, out _));
        Assert.Equal(source, route!.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/playnite")]
    [InlineData("playnite:/playnite/start/" + GameId)]
    [InlineData("playnite://")]
    [InlineData("playnite://playnite")]
    [InlineData("playnite://playnite?x=1")]
    [InlineData("playnite://playnite/start/" + GameId + "#x")]
    [InlineData("playnite://playnite/start/a\\b")]
    [InlineData("playnite://playnite/start/not-a-guid")]
    [InlineData("playnite://playnite/start/" + GameId + "/-1")]
    [InlineData("playnite://playnite/start/" + GameId + "/abc")]
    [InlineData("playnite://playnite/start/" + GameId + "/1000")]
    [InlineData("playnite://playnite/start/" + GameId + "/1/extra")]
    [InlineData("playnite://playnite/showgame")]
    [InlineData("playnite://playnite/showgame/not-a-guid")]
    [InlineData("playnite://playnite/search/one/two")]
    [InlineData("playnite://playnite/view")]
    [InlineData("playnite://playnite/view/mobile")]
    [InlineData("playnite://playnite/view/desktop/extra")]
    [InlineData("playnite://playnite/filter/favorite")]
    [InlineData("playnite://playnite/filter/favorite/maybe")]
    [InlineData("playnite://playnite/filter/genre/not-a-guid")]
    [InlineData("playnite://playnite/filter/release-year/26")]
    [InlineData("playnite://playnite/filter/release-year/0999")]
    [InlineData("playnite://playnite/filter/unknown/x")]
    [InlineData("playnite://playnite/filter/name/")]
    [InlineData("playnite://playnite/creatediag")]
    [InlineData("playnite://playnite/installaddon/https%3A%2F%2Fexample.com")]
    [InlineData("playnite://playnite/unknown")]
    [InlineData("playnite://pl@y/search/x")]
    [InlineData("playnite://../start")]
    [InlineData("playnite://a%2Fb")]
    public void Malformed_or_unsupported_routes_are_rejected(string? uri)
    {
        Assert.False(PlayniteUriRoute.TryParse(uri, out var route, out var error));
        Assert.Null(route);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("playnite://playnite/start/6a0d%GG1e")]
    [InlineData("playnite://playnite/start/%")]
    [InlineData("playnite://playnite/start/%2")]
    [InlineData("playnite://playnite/start/%FF")]
    [InlineData("playnite://playnite/start/%ED%A0%80")]
    public void Malformed_percent_or_utf8_is_rejected(string uri)
    {
        Assert.False(PlayniteUriRoute.TryParse(uri, out _, out _));
    }

    [Fact]
    public void Decoded_control_character_is_rejected()
    {
        Assert.False(PlayniteUriRoute.TryParse(
            "playnite://playnite/filter/name/a%0Ab",
            out _,
            out var error));

        Assert.Contains("control", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Raw_control_character_and_unescaped_whitespace_are_rejected()
    {
        Assert.False(PlayniteUriRoute.TryParse(
            "playnite://playnite/search/a\u0001b",
            out _,
            out _));
        Assert.False(PlayniteUriRoute.TryParse(
            "playnite://playnite/search/a b",
            out _,
            out _));
    }

    [Fact]
    public void Overlong_input_is_rejected()
    {
        var uri = "playnite://playnite/search/" + new string('a', PlayniteUriRoute.MaxLength);
        Assert.False(PlayniteUriRoute.TryParse(uri, out _, out var error));
        Assert.Contains("exceeds", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_arguments_are_rejected()
    {
        var arguments = string.Join('/', Enumerable.Repeat("a", PlayniteUriRoute.MaxArguments + 1));
        Assert.False(PlayniteUriRoute.TryParse("playnite://plugin/" + arguments, out _, out var error));
        Assert.Contains("arguments", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlong_argument_is_rejected()
    {
        var uri = "playnite://plugin/" + new string('a', PlayniteUriRoute.MaxArgumentLength + 1);
        Assert.False(PlayniteUriRoute.TryParse(uri, out _, out var error));
        Assert.Contains("Argument 1", error, StringComparison.Ordinal);
    }
}
