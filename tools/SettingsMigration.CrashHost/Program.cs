using System.Diagnostics;
using Playnite.Settings;

var step = Enum.Parse<SettingsWriteStep>(args[2]);
await SettingsMigration.MigrateAsync(args[0], args[1], [], checkpoint: current =>
{
    if (current == step)
    {
        // Prove the child reached this boundary before abrupt process death, which intentionally
        // bypasses every managed finally/Dispose.
        File.WriteAllText(args[3], current.ToString());
        Process.GetCurrentProcess().Kill();
    }
});
