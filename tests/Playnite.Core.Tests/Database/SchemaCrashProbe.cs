using System.Diagnostics;
using System.Runtime.CompilerServices;
using Playnite.Core.Database;

namespace Playnite.Core.Tests.Database;

internal static class SchemaCrashProbe
{
    // A child test executable runs only this probe and is SIGKILLed with an open SQLite transaction.
    // The ordinary test process never sets this environment variable on itself.
    [ModuleInitializer]
    internal static void Initialize()
    {
        var path = Environment.GetEnvironmentVariable("PLAYNITE_SCHEMA_CRASH_DATABASE");
        if (path == null) return;
        var version = int.Parse(Environment.GetEnvironmentVariable("PLAYNITE_SCHEMA_CRASH_VERSION")!);
        var stage = Enum.Parse<MigrationStage>(Environment.GetEnvironmentVariable("PLAYNITE_SCHEMA_CRASH_STAGE")!);
        using var connection = DatabaseTestDirectory.Connect(path);
        SchemaMigrator.ApplyAsync(connection, LibraryDatabase.SchemaVersion, CancellationToken.None, (v, s) =>
        {
            if (v != version || s != stage) return;
            File.WriteAllText(path + ".checkpoint", $"{v}:{s}");
            Process.GetCurrentProcess().Kill();
            // Do not let the migration advance while the OS delivers SIGKILL.
            Thread.Sleep(Timeout.Infinite);
        }).GetAwaiter().GetResult();
        Environment.Exit(94);
    }
}
