using Playnite.Core.Database;
using Playnite.Core.Import;
using Playnite.Platform;
using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;
using Playnite.Settings;
using Playnite.Steam;
using Playnite.Steam.Sync;

namespace Playnite.Desktop.Setup;

internal sealed class FirstTimeSetup
{
    private readonly LinuxEnvironment environment = new();
    private readonly XdgDirectories directories;
    private readonly LibraryImportService importer = new(new LegacyLibraryReaderExecutable());
    private string SettingsFile => Path.Combine(directories.Config, "settings.json");
    private string Database => Path.Combine(directories.Data, "library.sqlite");
    internal string LibraryBackup => Path.Combine(Database + ".import", "source.json");
    internal string LibraryReport => Database + ".import-report.json";
    internal string SettingsArchive => Database + ".import-settings.json";
    internal bool HasLibrary => File.Exists(Database) || Directory.Exists(Database) ||
        new FileInfo(Database).LinkTarget is not null;
    internal bool CanKeepLibrary => File.Exists(Database);
    internal bool HasImportReport => CanKeepLibrary && File.Exists(LibraryReport);
    internal bool HasNativeSettings => File.Exists(SettingsFile);

    internal FirstTimeSetup()
    {
        directories = XdgDirectories.Resolve(environment);
    }

    internal async Task<bool> IsCompleteAsync()
    {
        using var settings = new PlayniteSettingsService(SettingsFile);
        return (await settings.LoadAsync().ConfigureAwait(false)).Get(PlayniteSettingsKeys.FirstTimeWizardComplete);
    }

    internal async Task<ImportSummary> ImportAsync(string source, string windowsRoot,
        IProgress<LibraryImportProgress> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(source))
        {
            throw new ArgumentException("Choose an absolute path to the mounted Playnite folder.");
        }
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        if (!Directory.Exists(selected))
        {
            throw new DirectoryNotFoundException("The selected folder was not found. Check that the drive or share is mounted.");
        }
        RequireNoLinks(selected);
        EnsureOutsideSource(selected, directories.Data);
        EnsureOutsideSource(selected, directories.Config);
        if (HasLibrary)
        {
            throw new IOException("A Linux library already exists. Setup will not replace it.");
        }

        var child = Path.Combine(selected, "library");
        var library = !File.Exists(Path.Combine(selected, "database.json")) &&
            File.Exists(Path.Combine(child, "database.json")) ? child : selected;
        RequireNoLinks(library);
        var config = Path.Combine(selected, "config.json");
        SettingsPathMapping[] mappings = string.IsNullOrWhiteSpace(windowsRoot) ? [] : [new(windowsRoot.Trim(), selected)];
        Directory.CreateDirectory(directories.Data);
        var result = await importer.ImportAsync(new(library, Database), progress, token).ConfigureAwait(false);

        // The library has already been published. Settings are a separate handoff;
        // do not describe a settings failure as a rolled-back library.
        SettingsMigrationResult? settings = null;
        string? settingsWarning = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var read = new LinuxBoundedFileReader().Read(config, 16 * 1024 * 1024);
            if (read.Status != BoundedFileReadStatus.Missing)
            {
                if (read.Status != BoundedFileReadStatus.Ok)
                {
                    throw new IOException($"The v10 config.json could not be read as a regular settings file ({read.Status}).");
                }
                // Migrate captured bytes so the settings service never reopens
                // a path on the mounted source after the regular-file check.
                var captured = Directory.CreateTempSubdirectory("playnite-v10-settings-");
                try
                {
                    var capturedFile = Path.Combine(captured.FullName, "config.json");
                    await File.WriteAllBytesAsync(capturedFile, read.Bytes!, token).ConfigureAwait(false);
                    try
                    {
                        settings = await SettingsMigration.MigrateAsync(capturedFile, SettingsArchive, mappings,
                            cancellationToken: token).ConfigureAwait(false);
                    }
                    catch (ArgumentException) when (mappings.Length != 0)
                    {
                        settings = await SettingsMigration.MigrateAsync(capturedFile, SettingsArchive, [],
                            cancellationToken: token).ConfigureAwait(false);
                        settingsWarning = "The original Windows folder was not a valid mapping. Windows paths were kept as legacy data.";
                    }
                }
                finally
                {
                    captured.Delete(recursive: true);
                }
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception) || exception is OperationCanceledException)
        {
            settingsWarning = exception is OperationCanceledException
                ? "Legacy settings were not copied because setup was cancelled."
                : $"Legacy settings were not copied: {exception.Message}";
        }
        return new(result.Report, settings?.Report, settingsWarning);
    }

    internal Task<SteamSyncResult> SyncSteamAsync(CancellationToken token) =>
        // p6-003 discovery is a synchronous, bounded read of Steam manifests; run
        // that existing seam off the UI thread rather than blocking the wizard.
        Task.Run(async () =>
        {
            var paths = new LinuxPaths(environment, new LinuxFileSystem());
            var discovery = new SteamDiscoverySource(environment, paths, new SteamFileSystem(new LinuxBoundedFileReader()));
            Directory.CreateDirectory(directories.Data);
            var database = await LibraryDatabase.OpenAsync(Database, token).ConfigureAwait(false);
            return await new SteamLibrarySyncService(discovery).SyncAsync(database, token).ConfigureAwait(false);
        }, token);

    internal async Task FinishAsync(bool applyImportedSettings, CancellationToken token)
    {
        var complete = new SettingsUpdate(PlayniteSettingsKeys.FirstTimeWizardComplete, true);
        if (applyImportedSettings && HasImportReport && File.Exists(SettingsArchive) && !HasNativeSettings)
        {
            // Prepare a complete native file before making it visible. In particular,
            // never let the legacy wizard's completion flag decide Linux startup.
            var temporary = SettingsFile + "." + Path.GetRandomFileName();
            try
            {
                var storage = new SettingsFileStorage(temporary);
                await storage.WriteAtomicAsync(await File.ReadAllBytesAsync(SettingsArchive, token).ConfigureAwait(false), token)
                    .ConfigureAwait(false);
                using var archived = new PlayniteSettingsService(storage);
                try
                {
                    await archived.UpdateAsync([complete], token).ConfigureAwait(false);
                }
                catch (SettingsException exception) when (exception.Failure is SettingsFailure.Corrupt or SettingsFailure.InvalidValue)
                {
                    throw new LegacySettingsInvalidException(exception);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, SettingsFile, overwrite: false);
                return;
            }
            catch (IOException) when (File.Exists(SettingsFile))
            {
                // A newly created native profile wins; preserve its preferences.
                await SaveCompletionAsync(complete, token).ConfigureAwait(false);
                return;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        await SaveCompletionAsync(complete, token).ConfigureAwait(false);
    }

    private async Task SaveCompletionAsync(SettingsUpdate complete, CancellationToken token)
    {
        using var settings = new PlayniteSettingsService(SettingsFile);
        await settings.UpdateAsync([complete], token).ConfigureAwait(false);
    }

    private static void EnsureOutsideSource(string source, string target)
    {
        if (source == Path.GetPathRoot(source) || target == source ||
            target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Choose a source outside the Linux Playnite profile.");
        }
        RequireNoLinks(target);
    }

    private static void RequireNoLinks(string location)
    {
        for (string? path = location; path is not null; path = Path.GetDirectoryName(path))
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new IOException("Import paths must not traverse symbolic links. Use the actual mounted folder and a direct Linux profile path.");
            }
        }
    }

    internal static bool IsExpectedFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException
            or LibraryImportException or SettingsException or Newtonsoft.Json.JsonException
            or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException;

    internal sealed record ImportSummary(MigrationReport Library, SettingsMigrationReport? Settings, string? SettingsWarning);
    internal sealed class LegacySettingsInvalidException(SettingsException error) : Exception(error.Message, error);
}
