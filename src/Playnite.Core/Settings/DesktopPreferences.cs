namespace Playnite.Settings;

/// <summary>The two preferences editable in Desktop, separate from first-run setup.</summary>
public sealed record DesktopPreferences(string Language, bool ShowSidebar);

public sealed class DesktopPreferencesStore(string settingsPath, string welcomePath)
{
    private readonly SettingsFileStorage welcome = new(welcomePath);

    public async Task<DesktopPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var settings = new PlayniteSettingsService(settingsPath);
        return Read(await settings.LoadAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<DesktopPreferences> SaveAsync(DesktopPreferences original, DesktopPreferences edited,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<SettingsUpdate>();
        if (edited.Language != original.Language)
        {
            updates.Add(new(PlayniteSettingsKeys.Language, edited.Language));
        }
        if (edited.ShowSidebar != original.ShowSidebar)
        {
            updates.Add(new(PlayniteSettingsKeys.ShowSidebar, edited.ShowSidebar));
        }

        using var settings = new PlayniteSettingsService(settingsPath);
        return Read(await settings.UpdateAsync(updates, cancellationToken).ConfigureAwait(false));
    }

    public async Task<bool> IsWelcomeAcknowledgedAsync(CancellationToken cancellationToken = default) =>
        await welcome.ReadAsync(cancellationToken).ConfigureAwait(false) is not null;

    public Task AcknowledgeWelcomeAsync(CancellationToken cancellationToken = default) =>
        welcome.WriteAtomicAsync([], cancellationToken);

    private static DesktopPreferences Read(SettingsSnapshot snapshot) =>
        new(snapshot.Get(PlayniteSettingsKeys.Language) ?? PlayniteSettingsKeys.Language.DefaultValue!,
            snapshot.Get(PlayniteSettingsKeys.ShowSidebar));
}
