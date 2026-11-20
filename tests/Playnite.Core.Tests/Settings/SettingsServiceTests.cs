using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.Settings;
using Xunit;

namespace Playnite.Core.Tests.Settings;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "playnite-settings-service-" + Guid.NewGuid().ToString("N"));

    public SettingsServiceTests() => Directory.CreateDirectory(directory);

    private string Store => Path.Combine(directory, "config", "settings.json");

    [Fact]
    public async Task Missing_store_returns_documented_defaults_without_creating_a_file()
    {
        using var service = new PlayniteSettingsService(Store);

        var snapshot = await service.LoadAsync();

        Assert.False(snapshot.Exists);
        Assert.Equal(7, snapshot.Get(PlayniteSettingsKeys.Version));
        Assert.False(snapshot.Get(PlayniteSettingsKeys.StartInFullscreen));
        Assert.False(snapshot.Get(PlayniteSettingsKeys.StartMinimized));
        Assert.False(snapshot.Get(PlayniteSettingsKeys.MinimizeToTray));
        Assert.True(snapshot.Get(PlayniteSettingsKeys.CloseToTray));
        Assert.True(snapshot.Get(PlayniteSettingsKeys.EnableTray));
        Assert.True(snapshot.Get(PlayniteSettingsKeys.ShowSidebar));
        Assert.False(snapshot.Get(PlayniteSettingsKeys.FirstTimeWizardComplete));
        Assert.Equal("english", snapshot.Get(PlayniteSettingsKeys.Language));
        Assert.Equal("Playnite_builtin_DefaultDesktop", snapshot.Get(PlayniteSettingsKeys.Theme));
        Assert.Null(snapshot.Get(PlayniteSettingsKeys.DatabasePath));
        Assert.Null(snapshot.Get(PlayniteSettingsKeys.InstallInstanceId));
        Assert.False(snapshot.Get(PlayniteSettingsKeys.AutoBackupEnabled));
        Assert.Equal(AutoBackupFrequency.OnceAWeek, snapshot.Get(PlayniteSettingsKeys.AutoBackupFrequency));
        Assert.Equal(UpdateCheckFrequency.OnEveryStartup, snapshot.Get(PlayniteSettingsKeys.CheckForProgramUpdates));
        Assert.False(File.Exists(Store));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Load_real_settings_fixture_round_trips_v10_bytes_and_typed_values()
    {
        var source = await SeedGoldenConfigAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        await SettingsMigration.MigrateAsync(source, Store, []);
        var envelope = await File.ReadAllBytesAsync(Store);

        using var service = new PlayniteSettingsService(Store);
        var snapshot = await service.LoadAsync();

        Assert.True(snapshot.Exists);
        Assert.Equal(await File.ReadAllBytesAsync(source), SettingsMigration.ExportV10(envelope));
        Assert.Equal(7, snapshot.Get(PlayniteSettingsKeys.Version));
        Assert.Equal("english", snapshot.Get(PlayniteSettingsKeys.Language));
        Assert.Equal("Playnite_builtin_DefaultDesktop", snapshot.Get(PlayniteSettingsKeys.Theme));
        Assert.Equal("30000000-0000-0000-0000-000000000001", snapshot.Get(PlayniteSettingsKeys.InstallInstanceId));
        Assert.Equal(AutoBackupFrequency.OnceAWeek, snapshot.Get(PlayniteSettingsKeys.AutoBackupFrequency));
        Assert.True(snapshot.Settings.ContainsKey("ViewSettings"));
        Assert.Empty(snapshot.Legacy);
    }

    [Fact]
    public async Task Updating_a_real_fixture_preserves_every_other_value()
    {
        var source = await SeedGoldenConfigAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        await SettingsMigration.MigrateAsync(source, Store, []);
        using var service = new PlayniteSettingsService(Store);
        await service.LoadAsync();

        await service.UpdateAsync([SettingsUpdate.Set(PlayniteSettingsKeys.Language, "german")]);

        var expected = JObject.Parse(await File.ReadAllTextAsync(source));
        expected["Language"] = "german";
        var actual = JObject.Parse(Encoding.UTF8.GetString(
            SettingsMigration.ExportV10(await File.ReadAllBytesAsync(Store))));
        Assert.True(JToken.DeepEquals(expected, actual));
    }

    [Fact]
    public async Task Update_creates_a_store_from_documented_defaults()
    {
        using var service = new PlayniteSettingsService(Store);

        var snapshot = await service.UpdateAsync(
            [SettingsUpdate.Set(PlayniteSettingsKeys.Language, "german")]);

        Assert.True(snapshot.Exists);
        Assert.True(File.Exists(Store));
        using var reloaded = new PlayniteSettingsService(Store);
        var load = await reloaded.LoadAsync();
        Assert.Equal("german", load.Get(PlayniteSettingsKeys.Language));
        Assert.Equal(7, load.Get(PlayniteSettingsKeys.Version));
        Assert.True(load.Get(PlayniteSettingsKeys.CloseToTray));
    }

    [Fact]
    public async Task Update_preserves_unknown_and_legacy_properties_and_raises_one_change()
    {
        var source = await SeedV10Async("""
            {"Version":7,"Language":"english","AutoBackupDir":"D:\\Backups","Future":{"Paths":["Z:\\Extras"],"value":null}}
            """);
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        await SettingsMigration.MigrateAsync(source, Store, []);
        var events = new List<SettingsChangedEventArgs>();
        using var service = new PlayniteSettingsService(Store);
        service.Changed += (_, args) => events.Add(args);
        await service.LoadAsync();

        var updated = await service.UpdateAsync([SettingsUpdate.Set(PlayniteSettingsKeys.Language, "german")]);

        Assert.Equal("german", updated.Get(PlayniteSettingsKeys.Language));
        var single = Assert.Single(events);
        Assert.Equal(["Language"], single.ChangedProperties);
        Assert.Equal("german", single.Snapshot.Get(PlayniteSettingsKeys.Language));
        Assert.Equal("D:\\Backups", updated.Legacy["AutoBackupDir"]!.Value<string>());
        Assert.Equal("Z:\\Extras", updated.Legacy["Future"]!["Paths"]![0]!.Value<string>());
        Assert.Equal(JTokenType.Null, updated.Legacy["Future"]!["value"]!.Type);
        Assert.False(updated.Settings.ContainsKey("Future"));
        Assert.False(updated.Settings.ContainsKey("AutoBackupDir"));

        var exported = Encoding.UTF8.GetString(SettingsMigration.ExportV10(await File.ReadAllBytesAsync(Store)));
        Assert.Contains("\"Future\":{\"Paths\":[\"Z:\\\\Extras\"],\"value\":null}", exported);
        Assert.Contains("\"AutoBackupDir\":\"D:\\\\Backups\"", exported);
        Assert.Contains("\"Language\":\"german\"", exported);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Store)!, "*.tmp"));
    }

    [Fact]
    public async Task Updating_a_legacy_property_moves_it_to_settings_without_losing_siblings()
    {
        var source = await SeedV10Async("""
            {"Version":7,"DatabasePath":"Z:\\Unmapped\\Library","Future":{"keep":true}}
            """);
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        await SettingsMigration.MigrateAsync(source, Store, []);
        using var service = new PlayniteSettingsService(Store);
        await service.LoadAsync();

        var updated = await service.UpdateAsync(
            [SettingsUpdate.Set(PlayniteSettingsKeys.DatabasePath, "/games/library")]);

        Assert.Equal("/games/library", updated.Get(PlayniteSettingsKeys.DatabasePath));
        Assert.False(updated.Legacy.ContainsKey("DatabasePath"));
        Assert.True(updated.Legacy["Future"]!["keep"]!.Value<bool>());
        Assert.True(updated.Settings.ContainsKey("DatabasePath"));
    }

    [Fact]
    public async Task No_op_update_writes_nothing_and_raises_no_event()
    {
        var storage = new FakeStorage { Contents = await MigratedEnvelopeAsync() };
        var events = new List<SettingsChangedEventArgs>();
        using var service = new PlayniteSettingsService(storage);
        service.Changed += (_, args) => events.Add(args);
        await service.LoadAsync();

        var snapshot = await service.UpdateAsync(
            [SettingsUpdate.Set(PlayniteSettingsKeys.Language, "english")]);

        Assert.True(snapshot.Exists);
        Assert.Equal(2, storage.Reads);
        Assert.Equal(0, storage.Writes);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Empty_update_returns_the_current_snapshot_without_a_write()
    {
        var storage = new FakeStorage { Contents = await MigratedEnvelopeAsync() };
        using var service = new PlayniteSettingsService(storage);

        var snapshot = await service.UpdateAsync([]);

        Assert.True(snapshot.Exists);
        Assert.Equal(0, storage.Writes);
        Assert.Equal(1, storage.Reads);
    }

    [Fact]
    public async Task Corrupt_json_fails_with_a_clear_category_and_is_never_overwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        await File.WriteAllTextAsync(Store, "{ \"schemaVersion\": 1,");
        var before = await File.ReadAllBytesAsync(Store);
        using var service = new PlayniteSettingsService(Store);

        var load = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());
        Assert.Equal(SettingsFailure.Corrupt, load.Failure);
        Assert.Contains("not valid JSON", load.Message);
        var update = await Assert.ThrowsAsync<SettingsException>(() =>
            service.UpdateAsync([SettingsUpdate.Set(PlayniteSettingsKeys.Language, "german")]));
        Assert.Equal(SettingsFailure.Corrupt, update.Failure);
        Assert.Equal(before, await File.ReadAllBytesAsync(Store));
    }

    [Fact]
    public async Task Raw_v10_config_is_refused_as_an_explicit_migration_boundary()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        await File.WriteAllTextAsync(Store, """{"Version":7,"Language":"english"}""");
        var before = await File.ReadAllBytesAsync(Store);
        using var service = new PlayniteSettingsService(Store);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.LegacyShape, exception.Failure);
        Assert.Contains("SettingsMigration", exception.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(Store));
    }

    [Fact]
    public async Task Unsupported_schema_is_refused_without_writing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        await File.WriteAllTextAsync(Store, """{"schemaVersion":2,"settings":{},"legacy":{},"settingsUtf8":"e30=","report":{}}""");
        var before = await File.ReadAllBytesAsync(Store);
        using var service = new PlayniteSettingsService(Store);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.UnsupportedSchema, exception.Failure);
        Assert.Equal(before, await File.ReadAllBytesAsync(Store));
    }

    [Fact]
    public async Task Wrong_typed_value_is_rejected_by_load_validation()
    {
        var archived = Encoding.UTF8.GetBytes("""{"Version":7,"StartInFullscreen":"yes"}""");
        var envelope = Envelope("""{"Version":7,"StartInFullscreen":"yes"}""", "{}", archived);
        var storage = new FakeStorage { Contents = Encoding.UTF8.GetBytes(envelope) };
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.InvalidValue, exception.Failure);
        Assert.Contains("StartInFullscreen", exception.Message);
    }

    [Theory]
    [InlineData("2147483648")]           // int.MaxValue + 1, still a JSON long
    [InlineData("-2147483649")]          // int.MinValue - 1
    [InlineData("9223372036854775807")]  // long.MaxValue
    [InlineData("9223372036854775808")]  // long.MaxValue + 1
    [InlineData("-9223372036854775809")] // long.MinValue - 1
    public async Task Out_of_range_integer_is_rejected_at_load_with_the_key_name(string value)
    {
        var archived = $$"""{"Version":{{value}}}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope(archived, "{}", Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.InvalidValue, exception.Failure);
        Assert.Contains("Version", exception.Message);
    }

    [Fact]
    public async Task Out_of_range_integer_in_legacy_is_rejected_at_load()
    {
        var archived = """{"Version":2147483648}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope("{}", archived, Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.InvalidValue, exception.Failure);
        Assert.Contains("Version", exception.Message);
    }

    [Theory]
    [InlineData("2147483647", 2147483647)]
    [InlineData("-2147483648", -2147483648)]
    public async Task Int_boundary_values_are_accepted_and_read_exactly(string value, int expected)
    {
        var archived = $$"""{"Version":{{value}}}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope(archived, "{}", Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);

        var snapshot = await service.LoadAsync();

        Assert.Equal(expected, snapshot.Get(PlayniteSettingsKeys.Version));
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public async Task Long_boundary_values_read_exactly_through_a_long_key(string value, long expected)
    {
        var archived = $$"""{"UnmodeledLong":{{value}}}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope(archived, "{}", Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);

        var snapshot = await service.LoadAsync();

        Assert.Equal(expected, snapshot.Get(new SettingsKey<long>("UnmodeledLong", 0)));
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    public async Task Out_of_range_long_read_is_a_typed_error_not_an_overflow(string value)
    {
        var archived = $$"""{"UnmodeledLong":{{value}}}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope(archived, "{}", Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);
        var snapshot = await service.LoadAsync();

        var exception = Assert.Throws<SettingsException>(
            () => snapshot.Get(new SettingsKey<long>("UnmodeledLong", 0)));

        Assert.Equal(SettingsFailure.InvalidValue, exception.Failure);
        Assert.Contains("UnmodeledLong", exception.Message);
    }

    [Theory]
    [InlineData("\"7\"")]
    [InlineData("7.5")]
    [InlineData("2147483647.0")]
    [InlineData("1e400")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task Malformed_typed_values_are_rejected_at_load_with_the_key_name(string value)
    {
        var archived = $$"""{"Version":{{value}}}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope(archived, "{}", Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.InvalidValue, exception.Failure);
        Assert.Contains("Version", exception.Message);
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("3")]
    public async Task Out_of_range_enum_is_rejected_at_load_with_the_key_name(string value)
    {
        var archived = $$"""{"AutoBackupFrequency":{{value}}}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope(archived, "{}", Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.InvalidValue, exception.Failure);
        Assert.Contains("AutoBackupFrequency", exception.Message);
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("9223372036854775808")]
    public async Task Out_of_range_schema_version_is_unsupported_not_an_overflow(string value)
    {
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(
                $$$"""{"schemaVersion":{{{value}}},"settings":{},"legacy":{},"settingsUtf8":"e30=","report":{}}"""),
        };
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.UnsupportedSchema, exception.Failure);
    }

    public static IEnumerable<object[]> EveryDocumentedKeyWithAWrongJsonType()
    {
        yield return ["Version", "\"7\""];
        yield return ["StartInFullscreen", "1"];
        yield return ["StartMinimized", "\"false\""];
        yield return ["MinimizeToTray", "[]"];
        yield return ["CloseToTray", "{}"];
        yield return ["EnableTray", "0"];
        yield return ["ShowSidebar", "\"true\""];
        yield return ["FirstTimeWizardComplete", "1"];
        yield return ["Language", "1"];
        yield return ["Theme", "false"];
        yield return ["DatabasePath", "[]"];
        yield return ["InstallInstanceId", "{}"];
        yield return ["AutoBackupEnabled", "\"true\""];
        yield return ["AutoBackupFrequency", "\"OnceAWeek\""];
        yield return ["CheckForProgramUpdates", "1.5"];
    }

    [Theory]
    [MemberData(nameof(EveryDocumentedKeyWithAWrongJsonType))]
    public async Task Every_documented_key_rejects_a_wrong_json_type(string name, string value)
    {
        var archived = $$"""{"{{name}}":{{value}}}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope(archived, "{}", Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.InvalidValue, exception.Failure);
        Assert.Contains(name, exception.Message);
    }

    public static IEnumerable<object[]> EveryDocumentedKeyWithAnOverflowInteger()
    {
        foreach (var name in new[]
                 {
                     "Version", "StartInFullscreen", "StartMinimized", "MinimizeToTray", "CloseToTray",
                     "EnableTray", "ShowSidebar", "FirstTimeWizardComplete", "Language", "Theme",
                     "DatabasePath", "InstallInstanceId", "AutoBackupEnabled", "AutoBackupFrequency",
                     "CheckForProgramUpdates",
                 })
        {
            yield return [name, "9223372036854775808"];
        }
    }

    [Theory]
    [MemberData(nameof(EveryDocumentedKeyWithAnOverflowInteger))]
    public async Task Every_documented_key_rejects_an_integer_beyond_its_clr_range(string name, string value)
    {
        var archived = $$"""{"{{name}}":{{value}}}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope(archived, "{}", Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.InvalidValue, exception.Failure);
        Assert.Contains(name, exception.Message);
    }

    [Theory]
    [InlineData("1.7976931348623157E+308", double.MaxValue)]
    [InlineData("-1.7976931348623157E+308", double.MinValue)]
    [InlineData("0.5", 0.5)]
    [InlineData("7", 7.0)]
    public async Task Double_boundary_values_read_exactly_through_a_double_key(string value, double expected)
    {
        var archived = $$"""{"UnmodeledDouble":{{value}}}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope(archived, "{}", Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);

        var snapshot = await service.LoadAsync();

        Assert.Equal(expected, snapshot.Get(new SettingsKey<double>("UnmodeledDouble", 0)));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public async Task Double_range_escape_is_a_typed_error_not_an_overflow(string value)
    {
        var archived = $$"""{"UnmodeledDouble":{{value}}}""";
        var storage = new FakeStorage
        {
            Contents = Encoding.UTF8.GetBytes(Envelope(archived, "{}", Encoding.UTF8.GetBytes(archived))),
        };
        using var service = new PlayniteSettingsService(storage);
        var snapshot = await service.LoadAsync();

        var exception = Assert.Throws<SettingsException>(
            () => snapshot.Get(new SettingsKey<double>("UnmodeledDouble", 0)));

        Assert.Equal(SettingsFailure.InvalidValue, exception.Failure);
        Assert.Contains("UnmodeledDouble", exception.Message);
    }

    [Fact]
    public async Task Bom_prefixed_envelope_loads_and_updates_without_a_raw_parser_error()
    {
        var envelope = await MigratedEnvelopeAsync();
        var storage = new FakeStorage { Contents = [0xef, 0xbb, 0xbf, .. envelope] };
        using var service = new PlayniteSettingsService(storage);

        var loaded = await service.LoadAsync();
        var updated = await service.UpdateAsync(
            [SettingsUpdate.Set(PlayniteSettingsKeys.Language, "german")]);

        Assert.Equal(7, loaded.Get(PlayniteSettingsKeys.Version));
        Assert.Equal("german", updated.Get(PlayniteSettingsKeys.Language));
    }

    [Fact]
    public async Task Strict_json_parser_failure_is_reported_as_corrupt()
    {
        var archived = Encoding.UTF8.GetBytes("""{"Version":7}""");
        var envelope = $$$"""
            {"schemaVersion":1,"settings":{"Version":7,"UnmodeledDouble":NaN},"legacy":{},"settingsUtf8":"{{{Convert.ToBase64String(archived)}}}","report":{"rewrittenPaths":[],"unmappedPaths":[],"preservedUnknownProperties":[]}}
            """;
        var storage = new FakeStorage { Contents = Encoding.UTF8.GetBytes(envelope) };
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.LoadAsync());

        Assert.Equal(SettingsFailure.Corrupt, exception.Failure);
        // Newtonsoft accepts the lenient literal; the strict System.Text.Json boundary rejects it.
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task Unknown_property_name_is_not_written_into_settings()
    {
        var storage = new FakeStorage { Contents = await MigratedEnvelopeAsync() };
        using var service = new PlayniteSettingsService(storage);
        await service.LoadAsync();

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.UpdateAsync(
            [SettingsUpdate.Set(new SettingsKey<bool>("Future", false), true)]));

        Assert.Equal(SettingsFailure.InvalidValue, exception.Failure);
        Assert.Contains("unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task Write_failure_keeps_the_previous_snapshot_and_does_not_raise()
    {
        var storage = new FakeStorage
        {
            Contents = await MigratedEnvelopeAsync(),
            WriteError = new SettingsException(SettingsFailure.WriteUnavailable, "disk full"),
        };
        var events = new List<SettingsChangedEventArgs>();
        using var service = new PlayniteSettingsService(storage);
        service.Changed += (_, args) => events.Add(args);
        var loaded = await service.LoadAsync();
        var before = storage.Contents.ToArray();

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.UpdateAsync(
            [SettingsUpdate.Set(PlayniteSettingsKeys.Language, "german")]));

        Assert.Equal(SettingsFailure.WriteUnavailable, exception.Failure);
        Assert.Same(loaded, service.Current);
        Assert.Equal(before, storage.Contents);
        Assert.Empty(events);
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public async Task Read_failure_is_categorized_and_never_writes()
    {
        var storage = new FakeStorage
        {
            ReadError = new SettingsException(SettingsFailure.Unreadable, "permission denied"),
        };
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() =>
            service.UpdateAsync([SettingsUpdate.Set(PlayniteSettingsKeys.Language, "german")]));

        Assert.Equal(SettingsFailure.Unreadable, exception.Failure);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task Atomic_write_failure_leaves_the_target_untouched_and_no_stage()
    {
        var contents = await MigratedEnvelopeAsync();
        var target = Path.Combine(directory, "blocked-target");
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "marker");
        await File.WriteAllTextAsync(marker, "unchanged");
        var storage = new SettingsFileStorage(target);

        var exception = await Assert.ThrowsAsync<SettingsException>(() =>
            storage.WriteAtomicAsync(contents));

        Assert.Equal(SettingsFailure.WriteUnavailable, exception.Failure);
        Assert.Contains("could not be written", exception.Message);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(marker));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Unwritable_config_directory_is_reported_before_the_store_exists()
    {
        var blocker = Path.Combine(directory, "blocked");
        await File.WriteAllTextAsync(blocker, "not a directory");
        var storage = new SettingsFileStorage(Path.Combine(blocker, "settings.json"));
        using var service = new PlayniteSettingsService(storage);

        var exception = await Assert.ThrowsAsync<SettingsException>(() => service.UpdateAsync(
            [SettingsUpdate.Set(PlayniteSettingsKeys.Language, "german")]));

        Assert.Equal(SettingsFailure.WriteUnavailable, exception.Failure);
        Assert.Contains("could not be written", exception.Message);
        Assert.Equal("not a directory", await File.ReadAllTextAsync(blocker));
    }

    [Fact]
    public async Task Migration_is_explicit_atomic_and_refuses_to_replace_an_existing_store()
    {
        var source = await SeedGoldenConfigAsync();
        using var service = new PlayniteSettingsService(Store);
        var events = new List<SettingsChangedEventArgs>();
        service.Changed += (_, args) => events.Add(args);

        var migrated = await service.MigrateFromV10Async(source, []);
        var firstBytes = await File.ReadAllBytesAsync(Store);

        Assert.True(migrated.Exists);
        Assert.Equal(await File.ReadAllBytesAsync(source), SettingsMigration.ExportV10(firstBytes));
        Assert.Single(events);
        Assert.Equal("english", migrated.Get(PlayniteSettingsKeys.Language));

        var refusal = await Assert.ThrowsAsync<SettingsException>(() =>
            service.MigrateFromV10Async(source, []));
        Assert.Equal(SettingsFailure.AlreadyExists, refusal.Failure);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(Store));
    }

    [Fact]
    public async Task Migration_validation_failure_leaves_no_store()
    {
        var source = await SeedV10Async("{ not json");
        using var service = new PlayniteSettingsService(Store);

        var exception = await Assert.ThrowsAsync<SettingsException>(() =>
            service.MigrateFromV10Async(source, []));

        Assert.Equal(SettingsFailure.Corrupt, exception.Failure);
        Assert.False(File.Exists(Store));
    }

    [Fact]
    public async Task Pre_cancelled_operations_do_not_read_or_write()
    {
        var storage = new FakeStorage { Contents = await MigratedEnvelopeAsync() };
        using var service = new PlayniteSettingsService(storage);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UpdateAsync(
            [SettingsUpdate.Set(PlayniteSettingsKeys.Language, "german")], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.MigrateFromV10Async(Path.Combine(directory, "missing.json"), [], cancellation.Token));
        Assert.Equal(0, storage.Reads);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task Disposed_service_refuses_further_operations()
    {
        var service = new PlayniteSettingsService(new FakeStorage());
        service.Dispose();
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.LoadAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.UpdateAsync([SettingsUpdate.Set(PlayniteSettingsKeys.Language, "german")]));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<string> SeedGoldenConfigAsync()
    {
        var source = Path.Combine(directory, "source", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source,
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "golden", "settings", "config.json")));
        return source;
    }

    private async Task<string> SeedV10Async(string json)
    {
        var source = Path.Combine(directory, "source", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, json);
        return source;
    }

    private async Task<byte[]> MigratedEnvelopeAsync()
    {
        var source = await SeedV10Async("""{"Version":7,"Language":"english","Future":{"keep":true}}""");
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        await SettingsMigration.MigrateAsync(source, Store, []);
        return await File.ReadAllBytesAsync(Store);
    }

    private static string Envelope(string settings, string legacy, byte[] archived)
    {
        var report = """{"rewrittenPaths":[],"unmappedPaths":[],"preservedUnknownProperties":[]}""";
        return $$"""{"schemaVersion":1,"settings":{{settings}},"settingsUtf8":"{{Convert.ToBase64String(archived)}}","legacy":{{legacy}},"report":{{report}}}""";
    }

    private sealed class FakeStorage : ISettingsStorage
    {
        public string Location => "fake://settings";
        public byte[]? Contents { get; set; }
        public Exception? ReadError { get; init; }
        public Exception? WriteError { get; init; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }

        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (ReadError is not null)
            {
                throw ReadError;
            }

            return Task.FromResult(Contents is null ? null : Contents.ToArray());
        }

        public Task WriteAtomicAsync(byte[] contents, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            if (WriteError is not null)
            {
                throw WriteError;
            }

            Contents = contents.ToArray();
            return Task.CompletedTask;
        }
    }
}
