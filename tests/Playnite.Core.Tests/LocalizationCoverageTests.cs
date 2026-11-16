using System.Diagnostics;
using System.Text.Json;
using Playnite;
using Playnite.Core.Tests.Serialization;
using Xunit;
using Xunit.Sdk;

namespace Playnite.Core.Tests;

public sealed class LocalizationCoverageTests
{
    private const string EnglishPath = "legacy/source/Playnite/Localization/LocSource.xaml";
    private const string ReportPath = "docs/port/p3-loc-coverage.json";

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string RepositoryFile(string relativePath) =>
        Path.Combine(RepositoryPaths.Root, relativePath);

    private static string RelativePath(string path) =>
        Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/');

    private static string[] LocaleFiles() =>
        Directory.GetFiles(RepositoryFile("legacy/source/Playnite/Localization"), "*.xaml")
            .Where(path => Path.GetFileName(path) != "LocSource.xaml")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void AssertReportCoversEveryFrozenLanguage(IEnumerable<JsonElement> reported)
    {
        var expectedFiles = LocaleFiles();
        var entries = reported.ToArray();
        Assert.Equal(37, expectedFiles.Length);
        Assert.Equal(expectedFiles.Length, entries.Length);
        Assert.Equal(
            expectedFiles.Select(path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.Ordinal),
            entries.Select(entry => entry.GetProperty("language").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(
            expectedFiles.Select(RelativePath).Order(StringComparer.Ordinal),
            entries.Select(entry => entry.GetProperty("file").GetString()).Order(StringComparer.Ordinal));
    }

    private static LocalizationKeySet Set(params (string Key, string Value)[] entries) =>
        new(entries.Select(pair => KeyValuePair.Create(pair.Key, pair.Value)));

    private static IReadOnlyDictionary<string, string> Translation(params (string Key, string Value)[] entries)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            values.Add(key, value);
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string> Declarations(string relativePath)
    {
        using var reader = File.OpenText(RepositoryFile(relativePath));
        return LocalizationCoverage.ExtractDeclarations(reader);
    }

    [Fact]
    public void Committed_report_matches_every_frozen_language_and_its_hashes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile(ReportPath)));
        var provenance = document.RootElement.GetProperty("provenance");
        var inputs = provenance.GetProperty("inputs");
        var languages = document.RootElement.GetProperty("languages").EnumerateArray().ToArray();
        AssertReportCoversEveryFrozenLanguage(languages);

        var expectedPaths = LocaleFiles()
            .Select(RelativePath)
            .Append(EnglishPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedPaths, inputs.EnumerateObject().Select(input => input.Name).Order(StringComparer.Ordinal));
        Assert.Equal(languages.Length, provenance.GetProperty("languageCount").GetInt32());
        Assert.Equal(LocalizationKeySet.English.Entries.Count, provenance.GetProperty("keyCount").GetInt32());
        Assert.Equal(
            languages.Select(entry => entry.GetProperty("language").GetString()).Order(StringComparer.Ordinal),
            languages.Select(entry => entry.GetProperty("language").GetString()).ToArray());

        foreach (var relativePath in expectedPaths)
        {
            Assert.Equal(inputs.GetProperty(relativePath).GetString(), RepositoryPaths.Sha256(RepositoryFile(relativePath)));
        }

        foreach (var entry in languages)
        {
            var language = entry.GetProperty("language").GetString()!;
            var relativePath = entry.GetProperty("file").GetString()!;
            var committed = entry.Deserialize<LocalizationLanguageCoverage>(ReportOptions)!;
            var actual = LocalizationCoverage.Analyze(
                language,
                relativePath,
                LocalizationKeySet.English,
                Declarations(relativePath));
            Assert.Equal(
                JsonSerializer.Serialize(committed, ReportOptions),
                JsonSerializer.Serialize(actual, ReportOptions));
        }
    }

    [Fact]
    public void A_report_that_drops_a_language_is_rejected()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile(ReportPath)));
        var languages = document.RootElement.GetProperty("languages").EnumerateArray().ToArray();
        Assert.ThrowsAny<XunitException>(() => AssertReportCoversEveryFrozenLanguage(languages.Skip(1)));
    }

    [Fact]
    public void The_generator_check_reproduces_the_committed_report()
    {
        var startInfo = new ProcessStartInfo("python3")
        {
            WorkingDirectory = RepositoryPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("scripts/loc/generate_coverage.py");
        startInfo.ArgumentList.Add("--check");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cannot start the localisation coverage generator.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "The localisation coverage generator timed out.");
        Assert.True(
            process.ExitCode == 0,
            $"The localisation coverage generator failed with exit {process.ExitCode}: {output}{error}");
    }

    [Fact]
    public void English_remains_complete_and_unchanged()
    {
        var declarations = Declarations(EnglishPath);
        var coverage = LocalizationCoverage.Analyze("en", EnglishPath, LocalizationKeySet.English, declarations);
        Assert.Equal(1134, LocalizationKeySet.English.Entries.Count);
        Assert.Equal(1134, declarations.Count);
        Assert.Equal(1134, coverage.Present);
        Assert.Equal(0, coverage.Missing);
        Assert.Equal(0, coverage.Extra);
        Assert.Equal(0, coverage.Untranslated);
        Assert.Empty(coverage.MissingKeys);
        Assert.Empty(coverage.ExtraKeys);
        Assert.Empty(coverage.PlaceholderMismatches);
        Assert.True(LocalizationKeySet.English.Diff(new LocalizationKeySet(declarations)).IsEmpty);
    }

    [Fact]
    public void A_dropped_placeholder_is_a_defect()
    {
        var coverage = LocalizationCoverage.Analyze(
            "xx",
            "xx.xaml",
            Set(("LOCName", "Hello {0}, welcome {1}")),
            Translation(("LOCName", "Hallo {0}")));
        var mismatch = Assert.Single(coverage.PlaceholderMismatches);
        Assert.Equal("LOCName", mismatch.Key);
        Assert.Equal(["{0}", "{1}"], mismatch.EnglishPlaceholders);
        Assert.Equal(["{0}"], mismatch.TranslatedPlaceholders);
    }

    [Fact]
    public void A_renamed_placeholder_is_a_defect()
    {
        var coverage = LocalizationCoverage.Analyze(
            "xx",
            "xx.xaml",
            Set(("LOCName", "Open {0} and {1}")),
            Translation(("LOCName", "Ouvrir {1} puis {1}")));
        var mismatch = Assert.Single(coverage.PlaceholderMismatches);
        Assert.Equal("LOCName", mismatch.Key);
        Assert.Equal(["{0}", "{1}"], mismatch.EnglishPlaceholders);
        Assert.Equal(["{1}", "{1}"], mismatch.TranslatedPlaceholders);
    }

    [Fact]
    public void A_language_file_missing_keys_reports_exactly_those()
    {
        var coverage = LocalizationCoverage.Analyze(
            "xx",
            "xx.xaml",
            Set(("LOCA", "A"), ("LOCB", "B"), ("LOCC", "C")),
            Translation(("LOCA", "Aa")));
        Assert.Equal(1, coverage.Present);
        Assert.Equal(2, coverage.Missing);
        Assert.Equal(["LOCB", "LOCC"], coverage.MissingKeys);
        Assert.Empty(coverage.ExtraKeys);
        Assert.Equal(0, coverage.Untranslated);
        Assert.Empty(coverage.PlaceholderMismatches);
    }

    [Fact]
    public void Extra_keys_are_reported_separately_from_missing_keys()
    {
        var coverage = LocalizationCoverage.Analyze(
            "xx",
            "xx.xaml",
            Set(("LOCA", "A")),
            Translation(("LOCA", "Aa"), ("LOCZ", "Zz"), ("LOCM", "Mm")));
        Assert.Equal(0, coverage.Missing);
        Assert.Equal(2, coverage.Extra);
        Assert.Equal(["LOCM", "LOCZ"], coverage.ExtraKeys);
        Assert.Empty(coverage.MissingKeys);
    }

    [Fact]
    public void An_empty_translation_is_untranslated_and_falls_back_without_a_placeholder_defect()
    {
        var coverage = LocalizationCoverage.Analyze(
            "xx",
            "xx.xaml",
            Set(("LOCName", "Hello {0}")),
            Translation(("LOCName", "")));
        Assert.Equal(0, coverage.Present);
        Assert.Equal(0, coverage.Missing);
        Assert.Equal(1, coverage.Untranslated);
        Assert.Empty(coverage.PlaceholderMismatches);
    }

    [Fact]
    public void Whitespace_remains_a_translation_as_in_v10()
    {
        var coverage = LocalizationCoverage.Analyze(
            "xx",
            "xx.xaml",
            Set(("LOCName", "Hello")),
            Translation(("LOCName", " ")));
        Assert.Equal(1, coverage.Present);
        Assert.Equal(0, coverage.Untranslated);
        Assert.Empty(coverage.PlaceholderMismatches);
    }

    [Fact]
    public void Escaped_braces_are_literal_and_do_not_mask_a_dropped_placeholder()
    {
        Assert.Empty(LocalizationCoverage.ExtractPlaceholders("{{0}} and {{name}}"));
        Assert.Equal(["{0:N2}", "{0}"], LocalizationCoverage.ExtractPlaceholders("{0:N2} {0}"));
        Assert.Equal(["{0,-8}"], LocalizationCoverage.ExtractPlaceholders("{0,-8}"));
        Assert.Empty(LocalizationCoverage.ExtractPlaceholders("{name}"));
        Assert.Empty(LocalizationCoverage.ExtractPlaceholders("1}"));

        var coverage = LocalizationCoverage.Analyze(
            "xx",
            "xx.xaml",
            Set(("LOCName", "Value {{literal}} {0}")),
            Translation(("LOCName", "Valeur {{literal}}")));
        Assert.Single(coverage.PlaceholderMismatches);
    }

    [Fact]
    public void Coverage_is_ordinal_sorted_and_deterministic()
    {
        var english = Set(("LOCB", "B"), ("LOCA", "A {0}"), ("LOCC", "C"));
        var translated = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LOCZ"] = "Zz",
            ["LOCA"] = "Aa {1}",
            ["LOCC"] = "",
        };
        var first = LocalizationCoverage.Analyze("xx", "xx.xaml", english, translated);
        var second = LocalizationCoverage.Analyze("xx", "xx.xaml", english, translated);
        Assert.Equal(
            JsonSerializer.Serialize(first, ReportOptions),
            JsonSerializer.Serialize(second, ReportOptions));
        Assert.Equal(["LOCB"], first.MissingKeys);
        Assert.Equal(["LOCZ"], first.ExtraKeys);
        Assert.Equal("LOCA", Assert.Single(first.PlaceholderMismatches).Key);
    }
}
