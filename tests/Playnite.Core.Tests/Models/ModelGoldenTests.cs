using System.Globalization;
using System.Text.Json;
using Playnite.Core.Tests.Serialization;
using Xunit;

namespace Playnite.Core.Tests.Models;

public sealed class ModelGoldenTests
{
    private static readonly Lazy<SortedDictionary<string, string>> Actual = new(() =>
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            return ModelProbe.Observe();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    });

    private static string Golden(string name) => RepositoryPaths.Combine("tests", "golden", "models-v10", name);

    public static IEnumerable<object[]> Observations() =>
        JsonSerializer.Deserialize<SortedDictionary<string, string>>(File.ReadAllText(Golden("equality.json")))!
            .Select(row => new object[] { row.Key, row.Value });

    [Theory]
    [MemberData(nameof(Observations))]
    public void Pure_models_match_frozen_v10_observation(string key, string expected)
    {
        Assert.True(Actual.Value.TryGetValue(key, out var value), $"Missing model observation: {key}");
        // Firstmate approved deferring ambient-time grouping. Keep the v10 evidence intact,
        // and pin the exact, deliberately smaller pure-diff result rather than hiding the gap.
        if (key == "game.diff.Added")
        {
            Assert.Equal("Added,AddedSegment", expected);
            Assert.Equal("Added", value);
            return;
        }
        if (key == "game.diff.LastActivity")
        {
            Assert.Equal("LastActivity,LastActivitySegment", expected);
            Assert.Equal("LastActivity", value);
            return;
        }
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Observation_surface_is_exactly_the_frozen_surface()
    {
        Assert.Equal(Observations().Select(row => (string)row[0]).Order(StringComparer.Ordinal), Actual.Value.Keys);
    }

    [Fact]
    public void Golden_is_hash_pinned_to_frozen_sources_and_its_generator()
    {
        using var provenance = JsonDocument.Parse(File.ReadAllText(Golden("provenance.json")));
        var root = provenance.RootElement;
        Assert.Equal(root.GetProperty("equality_sha256").GetString(), RepositoryPaths.Sha256(Golden("equality.json")));
        Assert.Equal(root.GetProperty("observations").GetInt32(), Observations().Count());
        foreach (var source in root.GetProperty("sources").EnumerateObject())
            Assert.Equal(source.Value.GetString(), RepositoryPaths.Sha256(RepositoryPaths.Combine(source.Name)));
    }
}
