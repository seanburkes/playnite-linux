using Playnite.Core;
using Playnite.Core.Database;
using Playnite.Platform.Linux;
using Playnite.Steam;
using Playnite.Steam.Sync;

namespace Playnite.Desktop.Shell;

internal interface ILibrarySteamSync
{
    Task<SteamSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken);
}

/// <summary>Runs bounded read-only Steam discovery + SQLite reconciliation off the UI thread.</summary>
internal sealed class LibrarySteamSyncRunner(IEnvironment? environment = null) : ILibrarySteamSync
{
    private readonly IEnvironment environment = environment ?? new LinuxEnvironment();

    public Task<SteamSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(async () =>
        {
            var fileSystem = new LinuxFileSystem();
            var paths = new LinuxPaths(environment, fileSystem);
            var discovery = new SteamDiscoverySource(
                environment,
                paths,
                new SteamFileSystem(new LinuxBoundedFileReader()));

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var database = await LibraryDatabase.OpenAsync(databasePath, cancellationToken).ConfigureAwait(false);
            return await new SteamLibrarySyncService(discovery).SyncAsync(database, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public static string FormatSummary(SteamSyncResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Issues.Any(issue => issue.Reason == SteamSyncReasons.SteamRootMissing))
        {
            return "Steam library not found on this machine.";
        }

        if (!result.Reconciled && result.Issues.Count > 0)
        {
            var reasons = string.Join(", ", result.Issues.Select(issue => issue.Reason).Distinct(StringComparer.Ordinal));
            return $"Steam sync could not reconcile library ({reasons}).";
        }

        var summary = $"Steam sync complete: {result.Added} added, {result.Updated} updated, {result.Unchanged} unchanged";
        if (result.LinkedTitlesUpdated > 0)
        {
            summary += $", {result.LinkedTitlesUpdated} linked title(s) refreshed";
        }

        if (result.Removed > 0)
        {
            summary += $", {result.Removed} removed";
        }

        if (result.Skipped.Count > 0)
        {
            summary += $", {result.Skipped.Count} skipped";
        }

        if (result.Issues.Count > 0)
        {
            summary += $", {result.Issues.Count} issue(s)";
        }

        return summary + ".";
    }
}
