using Playnite.Platform.Linux.Integration;
using Xunit;

namespace Playnite.Core.Tests.Integration;

public sealed class PlayniteUriTests
{
    [Theory]
    [InlineData("playnite://start/0f8fad5b-d9cb-469f-a165-70867728950e", "start", new[] { "0f8fad5b-d9cb-469f-a165-70867728950e" })]
    [InlineData("playnite://start/id/2", "start", new[] { "id", "2" })]
    [InlineData("PLAYNITE://Search/hello+world", "Search", new[] { "hello world" })]
    [InlineData("playnite://search/%E6%96%87%E5%AD%97", "search", new[] { "文字" })]
    [InlineData("playnite://installaddon/https%3A%2F%2Fexample.test%2Fa.psm", "installaddon", new[] { "https://example.test/a.psm" })]
    [InlineData("playnite://start//double", "start", new[] { "double" })]
    [InlineData("playnite://search/", "search", new string[0])]
    public void WellFormedApplicationUrisParse(string value, string source, string[] arguments)
    {
        Assert.True(PlayniteUri.TryParse(value, out var uri, out var error), error);
        Assert.NotNull(uri);
        Assert.Equal(source, uri.Source);
        Assert.Equal(arguments, uri.Arguments);
    }

    [Fact]
    public void MaximumLengthsAreInclusive()
    {
        var argument = new string('a', PlayniteUri.MaxArgumentLength);
        Assert.True(PlayniteUri.TryParse("playnite://search/" + argument, out _, out var error), error);

        var tooLong = new string('a', PlayniteUri.MaxArgumentLength + 1);
        Assert.False(PlayniteUri.TryParse("playnite://search/" + tooLong, out _, out error));
        Assert.Contains("exceeds", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://start/id")]
    [InlineData("playnite:/start")]
    [InlineData("playnite://")]
    [InlineData("playnite:///")]
    [InlineData("playnite://start/a b")]
    [InlineData("playnite://start/a\tb")]
    [InlineData("playnite://start/a\\b")]
    [InlineData("playnite://start/a?query")]
    [InlineData("playnite://start/a#fragment")]
    [InlineData("playnite://start/%ZZ")]
    [InlineData("playnite://start/%2")]
    [InlineData("playnite://start/%00")]
    [InlineData("playnite://start/%0A")]
    [InlineData("playnite://start/%FF")]
    [InlineData("playnite://../id")]
    [InlineData("playnite://./id")]
    [InlineData("playnite://start with space/id")]
    [InlineData("playnite://start/id\u0001")]
    public void MalformedInputIsRejectedWithADiagnostic(string? value)
    {
        Assert.False(PlayniteUri.TryParse(value, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TooManyArgumentsAreRejected()
    {
        var segments = Enumerable.Range(0, PlayniteUri.MaxArguments + 1).Select(index => "a" + index);
        var value = "playnite://search/" + string.Join('/', segments);
        Assert.False(PlayniteUri.TryParse(value, out _, out var error));
        Assert.Contains("more than", error, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlongUriIsRejected()
    {
        var value = "playnite://search/" + new string('a', PlayniteUri.MaxLength);
        Assert.False(PlayniteUri.TryParse(value, out _, out var error));
        Assert.Contains("exceeds", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellMetacharactersStayLiteralAndAreNeverExecuted()
    {
        using var temporary = new IntegrationTemporaryDirectory();
        var marker = temporary.Path + "/executed";
        var value = "playnite://search/%24%28touch%20" + Uri.EscapeDataString(marker) + "%29";

        Assert.True(PlayniteUri.TryParse(value, out var uri, out var error), error);
        Assert.Equal("$(touch " + marker + ")", Assert.Single(uri!.Arguments));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void SourceIsNeverPercentDecoded()
    {
        Assert.False(PlayniteUri.TryParse("playnite://st%61rt/id", out _, out var error));
        Assert.Contains("source", error, StringComparison.Ordinal);
    }
}
