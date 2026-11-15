using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Playnite;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class UtilsGoldenTests
{
    public static TheoryData<string> Cases
    {
        get
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "utils-v10.json")));
            var data = new TheoryData<string>();
            foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                data.Add(item.GetRawText());
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Caller_backed_helpers_match_frozen_v10_source(string json)
    {
        using var document = JsonDocument.Parse(json);
        var c = document.RootElement;
        string? actual;
        try
        {
            actual = Run(c);
        }
        catch (Exception exception)
        {
            actual = "!" + exception.GetType().Name;
        }

        Assert.Equal(c.GetProperty("expected").GetString(), actual);
    }

    private static string? Run(JsonElement c)
    {
        var a = c.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray();
        var culture = CultureInfo.GetCultureInfo(c.GetProperty("culture").GetString()!);
        switch (c.GetProperty("op").GetString())
        {
            case "md5": return StringExtensions.MD5(a[0]!);
            case "empty": return StringExtensions.IsNullOrEmpty(a[0]).ToString();
            case "whitespace": return StringExtensions.IsNullOrWhiteSpace(a[0]).ToString();
            case "trademarks": return StringExtensions.RemoveTrademarks(a[0], a[1]!);
            case "normalize": return StringExtensions.NormalizeGameName(a[0]);
            case "trim": return StringExtensions.TrimEndString(a[0]!, a[1]!, Enum.Parse<StringComparison>(a[2]!));
            case "acronym": return StringExtensions.IsStartOfStringAcronym(a[0], a[1]).ToString();
            case "contains": return StringExtensions.Contains(a[0], a[1]!, StringComparison.OrdinalIgnoreCase).ToString();
            case "any": return StringExtensions.ContainsAny(a[0], a[1]?.ToCharArray()!).ToString();
            case "http": return StringExtensions.IsHttpUrl(a[0]).ToString();
            case "uri": return StringExtensions.IsUri(a[0]).ToString();
            case "url": return StringExtensions.UrlEncode(a[0]);
            case "replace": return StringExtensions.Replace(a[0]!, a[1]!, a[2], StringComparison.OrdinalIgnoreCase);
            case "invariant": return StringExtensions.ContainsInvariantCulture(a[0]!, a[1]!, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreSymbols).ToString();
            case "levenshtein": return StringExtensions.GetLevenshteinDistanceIgnoreCase(a[0]!, a[1]!).ToString(CultureInfo.InvariantCulture);
            case "jaro": return StringExtensions.GetJaroWinklerSimilarityIgnoreCase(a[0]!, a[1]!, double.Parse(a[2]!, CultureInfo.InvariantCulture)).ToString("R", CultureInfo.InvariantCulture);
            case "format": return StringExtensions.Format(a[0]!, culture, decimal.Parse(a[1]!, CultureInfo.InvariantCulture));
            case "enum": return EnumExtensions.GetDescription(Enum.Parse<Sample>(a[0]!), Localize);
            case "playtime": return PlayTimeFormatter.ToDisplayString(a[0] == null ? null : ulong.Parse(a[0]!, CultureInfo.InvariantCulture), bool.Parse(a[1]!), culture, Localize);
            case "date": return DateTimes.ToDisplayString(DateTime.Parse(a[0]!, CultureInfo.InvariantCulture), culture,
                DateTime.Parse(a[1]!, CultureInfo.InvariantCulture), Localize, culture.DateTimeFormat.ShortDatePattern, a[2], bool.Parse(a[3]!));
            case "parseDate":
                if (!DateTimes.TryParseDisplayDate(a[0], a[1]!, out var parsed))
                {
                    return "invalid";
                }

                return parsed?.ToString("O", CultureInfo.InvariantCulture) ?? "empty";
            default: throw new ArgumentException(c.GetProperty("op").GetString());
        }
    }

    private static string Localize(string key) => key switch
    {
        "LOCPlayedNone" => "none",
        "LOCPlayedSeconds" => "{0:N0} seconds",
        "LOCPlayedMinutes" => "{0:N0} minutes",
        "LOCPlayedHours" => "{0:N0} hours {1:N0} minutes",
        "LOCPlayedDays" => "{0:N0} days {1:N0} hours {2:N0} minutes",
        _ => "localized:" + key
    };

    [Flags]
    private enum Sample
    {
        None = 0,
        [Description("literal")] One = 1,
        [Description("LOCtwo")] Two = 2,
        Both = 3,
        Four = 4
    }

    [Fact]
    public void Date_parser_roundtrips_calendar_dates_and_rejects_fuzz_without_throwing()
    {
        var random = new Random(302);
        for (var i = 0; i < 2000; i++)
        {
            var date = new DateTime(random.Next(1, 10000), random.Next(1, 13), 1).AddDays(random.Next(28));
            Assert.True(DateTimes.TryParseDisplayDate(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "yyyy-MM-dd", out var parsed));
            Assert.Equal(date, parsed);
            Assert.Equal(DateTimeKind.Unspecified, parsed!.Value.Kind);

            var malformed = new string(Enumerable.Range(0, random.Next(1, 40)).Select(_ => (char)random.Next(32, 127)).ToArray());
            var valid = DateTimes.TryParseDisplayDate(malformed, "yyyy-MM-dd", out var fuzzed);
            Assert.Equal(DateTime.TryParseExact(malformed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var reference), valid);
            Assert.Equal(valid ? reference : (DateTime?)null, fuzzed);
        }
    }

    [Fact]
    public void Explicit_culture_is_independent_of_ambient_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            foreach (var ambient in new[] { "en-US", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(ambient);
                Assert.Equal("1,234.50", "{0:N2}".Format(CultureInfo.GetCultureInfo("en-US"), 1234.5m));
                Assert.Equal("12/31/2024", new DateTime(2024, 12, 31).ToDisplayString(CultureInfo.GetCultureInfo("en-US"),
                    new DateTime(2024, 12, 31), Localize, "M/d/yyyy"));
                Assert.Equal("1,000 hours 0 minutes", PlayTimeFormatter.ToDisplayString(3600000, false, CultureInfo.GetCultureInfo("en-US"), Localize));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Unnamed_flags_and_numeric_values_have_no_description()
    {
        Assert.Equal(string.Empty, ((UnnamedFlags)3).GetDescription(Localize));
        Assert.Equal("Four", ((Sample)4).GetDescription(Localize));
        Assert.Equal("Both", Sample.Both.GetDescription(Localize));
        Assert.Equal("One", UnnamedFlags.One.GetDescription(Localize));
    }

    [Flags]
    private enum UnnamedFlags { One = 1, Two = 2 }
}
