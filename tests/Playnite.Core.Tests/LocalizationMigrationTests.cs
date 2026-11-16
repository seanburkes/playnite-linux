using System.Diagnostics;
using System.Text.Json;
using Playnite;
using Playnite.Core.Tests.Serialization;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class LocalizationMigrationTests
{
    private static LocalizationKeySet Set(params (string Key, string Value)[] entries) =>
        new(entries.Select(pair => KeyValuePair.Create(pair.Key, pair.Value)));

    private static LocalizationKeyMigration Map(params (string Key, string? Target)[] entries) =>
        new(entries.Select(pair => KeyValuePair.Create(pair.Key, pair.Target)));

    private static string RepositoryFile(string path) => Path.Combine(RepositoryPaths.Root, path);

    [Fact]
    public void All_1134_frozen_English_keys_migrate_with_empty_sibling_diff_and_no_loss()
    {
        using var reader = File.OpenText(RepositoryFile("legacy/source/Playnite/Localization/LocSource.xaml"));
        var source = LocalizationKeySet.Extract(reader);
        var result = LocalizationKeyMigration.English.Apply(source, LocalizationKeySet.English);
        Assert.Equal(1134, source.Entries.Count);
        Assert.Equal(1134, LocalizationKeyMigration.English.Mapping.Count);
        Assert.Equal(1134, result.MappedCount);
        Assert.True(result.IsComplete);
        Assert.True(LocalizationKeySet.English.Diff(result.Migrated).IsEmpty);
        Assert.Equal(source.Entries, result.Migrated.Entries);
        Assert.Empty(result.Unmapped.Entries);
        Assert.Empty(result.Obsolete.Entries);
        Assert.Empty(result.MissingKeys);
        Assert.Empty(result.OrphanMappings);
        foreach (var key in source.Entries.Keys)
        {
            Assert.Equal(key, LocalizationKeyMigration.English.Mapping[key]);
            Assert.Equal(source.Resolve(key), result.Migrated.Resolve(key));
        }
    }

    [Fact]
    public void Removing_a_mapping_reports_the_original_entry_and_missing_destination()
    {
        var source = Set(("a", "A"), ("b", " B "));
        var result = Map(("a", "a")).Apply(source, source);
        Assert.False(result.IsComplete);
        Assert.Equal(1, result.MappedCount);
        Assert.Equal(" B ", result.Unmapped.Entries["b"]);
        Assert.Equal(["b"], result.MissingKeys);
        Assert.Empty(result.Obsolete.Entries);
    }

    [Fact]
    public void Missing_input_is_reported_without_fabricating_English_text()
    {
        var result = Map(("a", "a"), ("b", "b")).Apply(Set(("a", "translated")), Set(("a", "A"), ("b", "B")));
        Assert.False(result.IsComplete);
        Assert.Equal(["b"], result.MissingKeys);
        Assert.Single(result.Migrated.Entries);
        Assert.Equal("translated", result.Migrated.Entries["a"]);
    }

    [Fact]
    public void Unknown_orphan_input_is_ordinal_reported_with_its_original_text()
    {
        var result = Map(("a", "a")).Apply(Set(("a", "A"), ("z", "unknown"), ("A", "case")), Set(("a", "A")));
        Assert.False(result.IsComplete);
        Assert.Equal(["A", "z"], result.Unmapped.Entries.Keys);
        Assert.Equal("case", result.Unmapped.Entries["A"]);
        Assert.Equal("unknown", result.Unmapped.Entries["z"]);
        Assert.Empty(result.MissingKeys);
    }

    [Fact]
    public void Obsolete_keys_are_explicit_and_keep_their_values_in_diagnostics()
    {
        var result = Map(("old", null), ("a", "a")).Apply(Set(("old", "do not lose me"), ("a", "A")), Set(("a", "A")));
        Assert.False(result.IsComplete);
        Assert.Equal("do not lose me", result.Obsolete.Entries["old"]);
        Assert.Empty(result.Unmapped.Entries);
        Assert.Equal(1, result.MappedCount);
        Assert.Equal(2, result.MappedCount + result.Obsolete.Entries.Count + result.Unmapped.Entries.Count);
    }

    [Fact]
    public void A_mapping_to_a_nonexistent_target_is_reported_even_when_its_source_is_absent()
    {
        var migration = Map(("old", "absent"));
        var result = migration.Apply(Set(("old", "original")), Set());
        Assert.False(result.IsComplete);
        Assert.Equal(["old"], result.OrphanMappings);
        Assert.Equal("original", result.Unmapped.Entries["old"]);
        Assert.Empty(result.Migrated.Entries);
        Assert.Equal(["old"], migration.Apply(Set(), Set()).OrphanMappings);
    }

    [Fact]
    public void English_migration_is_idempotent_for_every_key_and_value()
    {
        var once = LocalizationKeyMigration.English.Apply(LocalizationKeySet.English, LocalizationKeySet.English);
        var twice = LocalizationKeyMigration.English.Apply(once.Migrated, LocalizationKeySet.English);
        Assert.True(once.IsComplete);
        Assert.True(twice.IsComplete);
        Assert.Equal(1134, twice.MappedCount);
        Assert.True(once.Migrated.Diff(twice.Migrated).IsEmpty);
    }

    [Fact]
    public void Renamed_destinations_are_fixed_points_on_a_second_application()
    {
        var migration = Map(("old", "new"));
        var target = Set(("new", "English"));
        var once = migration.Apply(Set(("old", "translated")), target);
        var twice = migration.Apply(once.Migrated, target);
        Assert.True(once.IsComplete);
        Assert.True(twice.IsComplete);
        Assert.Equal("translated", twice.Migrated.Entries["new"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  Héllo & <world>\nline  ")]
    public void Migration_preserves_empty_whitespace_and_unicode_without_fallback(string value)
    {
        var result = Map(("old", "new")).Apply(Set(("old", value)), Set(("new", "English")));
        Assert.Equal(value, result.Migrated.Entries["new"]);
    }

    [Fact]
    public void Colliding_destinations_duplicate_sources_and_rename_chains_fail_instead_of_overwriting()
    {
        Assert.Throws<ArgumentException>(() => Map(("a", "c"), ("b", "c")));
        Assert.Throws<ArgumentException>(() => Map(("a", "b"), ("a", "c")));
        Assert.Throws<ArgumentException>(() => Map(("a", "b"), ("b", "c")));
        Assert.Throws<ArgumentException>(() => Map(("a", "b"), ("b", null)));
        Assert.Throws<ArgumentException>(() => Map(("a", "b"), ("b", "a")));
        Assert.Throws<ArgumentException>(() => Map(("a", "b")).Apply(Set(("a", "old"), ("b", "new")), Set(("b", "target"))));
    }

    [Fact]
    public void Mapping_copies_inputs_and_exposes_no_mutable_dictionary()
    {
        var input = new Dictionary<string, string?> { ["a"] = "a" };
        var migration = new LocalizationKeyMigration(input);
        input["a"] = null;
        Assert.Equal("a", migration.Mapping["a"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string?>)migration.Mapping).Add("b", "b"));
    }

    [Fact]
    public void Invalid_arguments_fail_at_the_boundary()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalizationKeyMigration(null!));
        Assert.Throws<ArgumentException>(() => Map(("", "a")));
        Assert.Throws<ArgumentException>(() => Map(("a", "")));
        Assert.Throws<ArgumentNullException>(() => Map().Apply(null!, Set()));
        Assert.Throws<ArgumentNullException>(() => Map().Apply(Set(), null!));
    }

    [Fact]
    public void Committed_report_binds_mapping_groups_to_frozen_provenance_and_runtime_counts()
    {
        using var report = JsonDocument.Parse(File.ReadAllText(RepositoryFile("docs/port/p3-009-migration-report.json")));
        var root = report.RootElement;
        Assert.Equal(5, root.GetProperty("inputs").EnumerateObject().Count());
        foreach (var input in root.GetProperty("inputs").EnumerateObject())
        {
            Assert.Equal(input.Value.GetString(), RepositoryPaths.Sha256(RepositoryFile(input.Name)));
        }

        var result = LocalizationKeyMigration.English.Apply(LocalizationKeySet.English, LocalizationKeySet.English);
        var counts = root.GetProperty("counts");
        Assert.Equal(result.MappedCount, counts.GetProperty("mapped").GetInt32());
        Assert.Equal(result.Unmapped.Entries.Count, counts.GetProperty("unmapped").GetInt32());
        Assert.Equal(result.Obsolete.Entries.Count, counts.GetProperty("obsolete").GetInt32());
        Assert.Equal(1134, counts.GetProperty("source").GetInt32());
        Assert.Equal(1134, counts.GetProperty("target").GetInt32());
        Assert.Empty(root.GetProperty("unmapped").EnumerateArray());
        Assert.Empty(root.GetProperty("obsolete").EnumerateArray());
        Assert.Empty(root.GetProperty("orphanMappings").EnumerateArray());
        foreach (var diff in root.GetProperty("diff").EnumerateObject())
        {
            Assert.Empty(diff.Value.EnumerateArray());
        }

        using var data = JsonDocument.Parse(File.ReadAllText(RepositoryFile("src/Playnite.Core/Migrations/localization-v10.json")));
        var groups = data.RootElement.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal([1131, 3], groups.Select(group => group.GetProperty("mapping").EnumerateObject().Count()));
        Assert.Equal(["loc-prefixed", "unprefixed"], groups.Select(group => group.GetProperty("id").GetString()));
        foreach (var group in groups)
        {
            var provenance = group.GetProperty("provenance");
            Assert.Equal("legacy/source/Playnite/Localization/LocSource.xaml", provenance.GetProperty("source").GetString());
            Assert.Equal("legacy/source/Playnite/Localization/LocalizationKeys.cs", provenance.GetProperty("constants").GetString());
            Assert.Equal("legacy/build/buildLocConstants.ps1", provenance.GetProperty("generator").GetString());
        }
    }

    [Fact]
    public void Report_generation_is_deterministic_and_checked_in_CI_without_rebaselining()
    {
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(RepositoryFile("scripts/migrations/localization_report.py"));
        start.ArgumentList.Add("--check");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }
}
