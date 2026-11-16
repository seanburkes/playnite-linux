namespace Playnite.Core.Database;

/// <summary>Retained lock inode coordinating database connections with file publication.</summary>
internal static class DatabasePublicationLease
{
    // Per-async-flow rendezvous lets tests observe actual OS-lock contention, not elapsed time.
    internal static readonly AsyncLocal<Action<string, bool>?> Contention = new();

    internal static async Task<FileStream> AcquireAsync(string database, bool exclusive, CancellationToken cancellationToken)
    {
        var path = database + ".publication.lock";
        if (new FileInfo(path).LinkTarget != null)
            throw new IOException("Database publication lock must not be a symbolic link.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // On Linux FileStream uses shared/exclusive flock locks. Never unlink this inode.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    exclusive ? FileShare.None : FileShare.ReadWrite);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
            {
                Contention.Value?.Invoke(database, exclusive);
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
