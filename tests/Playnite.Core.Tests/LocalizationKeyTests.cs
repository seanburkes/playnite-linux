using System.Text.Json;
using System.Xml;
using Playnite;
using Playnite.Core.Tests.Serialization;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class LocalizationKeyTests
{
    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "golden", "loc", "english-v10.json");

    private static readonly string[] FrozenInputs =
    [
        "legacy/source/Playnite/Localization/LocSource.xaml",
        "legacy/source/Playnite/Localization/LocalizationKeys.cs",
        "legacy/build/buildLocConstants.ps1",
    ];

    private static Dictionary<string, string> GoldenEntries()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GoldenPath));
        return document.RootElement.GetProperty("entries").Deserialize<Dictionary<string, string>>()!;
    }

    /// <summary>Resolves a golden provenance path against the repository, never a test-output copy.</summary>
    private static string RepositoryFile(string relativePath) =>
        Path.Combine(RepositoryPaths.Root, Path.Combine(relativePath.Split('/')));

    [Fact]
    public void English_catalog_and_declarations_match_the_complete_frozen_golden()
    {
        var golden = GoldenEntries();
        using var source = File.OpenText(RepositoryFile(FrozenInputs[0]));
        var extracted = LocalizationKeySet.Extract(source);
        Assert.Equal(golden.OrderBy(pair => pair.Key, StringComparer.Ordinal), extracted.Entries);
        Assert.Equal(extracted.Entries, LocalizationKeySet.English.Entries);
        var fields = typeof(LOC).GetFields();
        Assert.Equal(golden.Count, fields.Length);
        foreach (var (key, value) in golden)
        {
            Assert.Equal(key, typeof(LOC).GetField(LocalizationKeySet.GetConstantName(key))!.GetRawConstantValue());
            Assert.Equal(value, LocalizationKeySet.English.Resolve(key));
        }
    }

    [Fact]
    public void Golden_input_hashes_bind_to_the_frozen_legacy_files_at_their_exact_paths()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GoldenPath));
        var provenance = document.RootElement.GetProperty("provenance");
        var inputs = provenance.GetProperty("inputs");
        Assert.Equal(
            FrozenInputs.Order(StringComparer.Ordinal),
            inputs.EnumerateObject().Select(input => input.Name).Order(StringComparer.Ordinal));
        foreach (var relativePath in FrozenInputs)
        {
            var path = RepositoryFile(relativePath);
            Assert.True(File.Exists(path), $"Frozen legacy input is missing: {path}");
            Assert.Equal(inputs.GetProperty(relativePath).GetString(), RepositoryPaths.Sha256(path));
        }

        Assert.Equal(GoldenEntries().Count, provenance.GetProperty("keyCount").GetInt32());
    }

    [Fact]
    public void English_compared_with_an_independently_extracted_English_has_an_empty_diff()
    {
        using var source = File.OpenText(RepositoryFile(FrozenInputs[0]));
        var diff = LocalizationKeySet.English.Diff(LocalizationKeySet.Extract(source));
        Assert.True(diff.IsEmpty);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void A_removed_English_key_is_reported_without_other_changes()
    {
        var after = new LocalizationKeySet(LocalizationKeySet.English.Entries.Where(pair => pair.Key != LOC.ExitAppLabel));
        var diff = LocalizationKeySet.English.Diff(after);
        Assert.False(diff.IsEmpty);
        Assert.Equal([LOC.ExitAppLabel], diff.Removed);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Diff_is_ordinal_sorted_and_compares_values_including_whitespace()
    {
        var before = Set(("z", "old"), ("a", "old"), ("A", "same"), ("gone", "removed"));
        var after = Set(("new-z", "added"), ("A", "same"), ("z", "old "), ("new-A", "added"), ("a", "OLD"));
        var diff = before.Diff(after);
        Assert.Equal(["new-A", "new-z"], diff.Added);
        Assert.Equal(["gone"], diff.Removed);
        Assert.Equal(["a", "z"], diff.Changed);
    }

    [Theory]
    [InlineData("LOCAditionalFilters", "AditionalFilters")]
    [InlineData("LanguageName", "LanguageName")]
    [InlineData("NameFilterUseFuzzyMatching", "NameFilterUseFuzzyMatching")]
    [InlineData("NameFilterUseFuzzyMatchingTooltip", "NameFilterUseFuzzyMatchingTooltip")]
    [InlineData("locCase", "locCase")]
    [InlineData("LOCLOCName", "LOCName")]
    public void Constant_naming_removes_exactly_one_case_sensitive_prefix(string key, string name)
    {
        Assert.Equal(name, LocalizationKeySet.GetConstantName(key));
    }

    [Fact]
    public void Resolution_preserves_missing_marker_case_sensitivity_and_empty_translation_fallback()
    {
        var english = Set(("LOCName", "English"));
        Assert.Equal("Translated", english.Resolve("LOCName", Set(("LOCName", "Translated"))));
        Assert.Equal("English", english.Resolve("LOCName", Set(("LOCName", ""))));
        Assert.Equal("English", english.Resolve("LOCName", Set()));
        Assert.Equal(" ", english.Resolve("LOCName", Set(("LOCName", " "))));
        Assert.Equal("<!locName!>", english.Resolve("locName"));
        Assert.Equal("<!Missing!>", english.Resolve("Missing"));
        Assert.Equal("Addon", english.Resolve("AddonKey", Set(("AddonKey", "Addon"))));
    }

    [Fact]
    public void Extraction_preserves_entities_unicode_and_space_and_ignores_empty_or_unkeyed_declarations()
    {
        using var source = new StringReader(Wrap("""
            <!-- comment -->
            <sys:String x:Key="LOCText" xml:space="preserve">  Héllo &amp; &lt;world&gt;&#10;line  </sys:String>
            <sys:String x:Key="LOCEmpty"></sys:String>
            <sys:String>Unkeyed</sys:String>
            <sys:String x:Key="LOCSpaces"> </sys:String>
            """));
        var set = LocalizationKeySet.Extract(source);
        Assert.Equal(2, set.Entries.Count);
        Assert.Equal("  Héllo & <world>\nline  ", set.Resolve("LOCText"));
        Assert.Equal(" ", set.Resolve("LOCSpaces"));
        Assert.Equal(-1, source.Peek()); // Input remains readable (not disposed) at EOF.
    }

    [Fact]
    public void Invalid_xml_wrong_root_duplicate_keys_and_external_entities_fail_closed()
    {
        Assert.Throws<XmlException>(() => LocalizationKeySet.Extract(new StringReader("<broken>")));
        Assert.Throws<FormatException>(() => LocalizationKeySet.Extract(new StringReader("<root/>")));
        Assert.Throws<ArgumentException>(() => LocalizationKeySet.Extract(new StringReader(Wrap("""
            <sys:String x:Key="LOCA">a</sys:String><sys:String x:Key="LOCA">b</sys:String>
            """))));
        Assert.Throws<XmlException>(() => LocalizationKeySet.Extract(new StringReader("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///does-not-exist'>]><root>&x;</root>")));
    }

    [Fact]
    public void Key_set_copies_input_and_rejects_duplicates_and_null_values()
    {
        var input = new Dictionary<string, string> { ["key"] = "original" };
        var set = new LocalizationKeySet(input);
        input["key"] = "mutated";
        Assert.Equal("original", set.Resolve("key"));
        Assert.Throws<ArgumentException>(() => Set(("key", "a"), ("key", "b")));
        Assert.Throws<ArgumentNullException>(() => Set(("key", null!)));
        Assert.Throws<ArgumentException>(() => Set(("", "value")));
    }

    [Fact]
    public void Seeded_sets_obey_identity_reversal_and_insertion_order_independence()
    {
        var random = new Random(7007);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var beforeEntries = Enumerable.Range(0, 30).Where(_ => random.Next(2) == 0)
                .Select(i => KeyValuePair.Create($"LOC{i}", random.Next(3).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            var afterEntries = Enumerable.Range(0, 30).Where(_ => random.Next(2) == 0)
                .Select(i => KeyValuePair.Create($"LOC{i}", random.Next(3).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            var before = new LocalizationKeySet(beforeEntries);
            var after = new LocalizationKeySet(afterEntries);
            Assert.True(before.Diff(before).IsEmpty);
            var forward = before.Diff(after);
            var reverse = after.Diff(before);
            Assert.Equal(forward.Added, reverse.Removed);
            Assert.Equal(forward.Removed, reverse.Added);
            Assert.Equal(forward.Changed, reverse.Changed);
            var reordered = new LocalizationKeySet(beforeEntries.Reverse()).Diff(new LocalizationKeySet(afterEntries.Reverse()));
            Assert.Equal(forward.Added, reordered.Added);
            Assert.Equal(forward.Removed, reordered.Removed);
            Assert.Equal(forward.Changed, reordered.Changed);
        }
    }

    [Fact]
    public void Seeded_xml_text_round_trips_through_extraction()
    {
        var random = new Random(1134);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            const string alphabet = "Aa <>&\"é\n\t";
            var value = new string(Enumerable.Range(0, random.Next(1, 100)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var escaped = System.Security.SecurityElement.Escape(value);
            using var source = new StringReader(Wrap($"<sys:String x:Key=\"LOCValue\" xml:space=\"preserve\">{escaped}</sys:String>"));
            Assert.Equal(value, LocalizationKeySet.Extract(source).Resolve("LOCValue"));
        }
    }

    private static LocalizationKeySet Set(params (string Key, string Value)[] entries) =>
        new(entries.Select(pair => KeyValuePair.Create(pair.Key, pair.Value)));

    private static string Wrap(string body) =>
        "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:sys=\"clr-namespace:System;assembly=mscorlib\">" + body + "</ResourceDictionary>";
}
