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
        Assert.True(snapshot.Get(PlayniteSettingsKeys.CloseToTray));
        Assert.True(snapshot.Get(PlayniteSettingsKeys.EnableTray));
        Assert.Equal("english", snapshot.Get(PlayniteSettingsKeys.Language));
        Assert.Equal("Playnite_builtin_DefaultDesktop", snapshot.Get(PlayniteSettingsKeys.Theme));
        Assert.Null(snapshot.Get(PlayniteSettingsKeys.DatabasePath));
        Assert.Null(snapshot.Get(PlayniteSettingsKeys.InstallInstanceId));
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
