using Playnite.Settings;
using Xunit;

namespace Playnite.Core.Tests.Settings;

public sealed class DesktopPreferencesTests : IDisposable
{
    private readonly string config = Path.Combine(Path.GetTempPath(), "playnite-p7-006-" + Path.GetRandomFileName());
    private string SettingsPath => Path.Combine(config, "settings.json");
    private DesktopPreferencesStore Store => new(SettingsPath, Path.Combine(config, "welcome-acknowledged"));

    [Fact]
    public async Task Welcome_acknowledgment_does_not_create_settings_or_complete_the_import_wizard()
    {
        Assert.False(await Store.IsWelcomeAcknowledgedAsync(TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(config));
        await Store.AcknowledgeWelcomeAsync(TestContext.Current.CancellationToken);
        Assert.True(await Store.IsWelcomeAcknowledgedAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(SettingsPath));

        using var service = new PlayniteSettingsService(SettingsPath);
        Assert.False((await service.LoadAsync(TestContext.Current.CancellationToken))
            .Get(PlayniteSettingsKeys.FirstTimeWizardComplete));
    }

    [Fact]
    public async Task Saving_only_changed_preferences_preserves_other_settings_and_wizard_state()
    {
        var token = TestContext.Current.CancellationToken;
        var original = await Store.LoadAsync(token);
        Assert.Equal(new DesktopPreferences("english", true), original);
        using var service = new PlayniteSettingsService(SettingsPath);
        await service.UpdateAsync(
            [new(PlayniteSettingsKeys.Language, "fr"), new(PlayniteSettingsKeys.FirstTimeWizardComplete, true)], token);

        Assert.Equal(new DesktopPreferences("fr", false),
            await Store.SaveAsync(original, original with { ShowSidebar = false }, token));
        Assert.Equal(new DesktopPreferences("fr", false), await Store.LoadAsync(token));
        Assert.True((await service.LoadAsync(token)).Get(PlayniteSettingsKeys.FirstTimeWizardComplete));
        Assert.False(await Store.IsWelcomeAcknowledgedAsync(token));
    }

    [Fact]
    public async Task Canceled_operations_do_not_write_either_file()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.AcknowledgeWelcomeAsync(source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Store.SaveAsync(new("english", true), new("de", false), source.Token));
        Assert.False(Directory.Exists(config));
    }

    public void Dispose()
    {
        if (Directory.Exists(config))
        {
            Directory.Delete(config, recursive: true);
        }
    }
}
