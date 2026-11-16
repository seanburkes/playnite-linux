using System.Diagnostics;
using Playnite.Settings;

var step = Enum.Parse<SettingsWriteStep>(args[2]);
await SettingsMigration.MigrateAsync(args[0], args[1], [], checkpoint: current =>
{
    if (current == step)
    {
        // Abrupt process death intentionally bypasses every managed finally/Dispose.
        Process.GetCurrentProcess().Kill();
    }
});
