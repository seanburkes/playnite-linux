namespace Playnite.Settings;

/// <summary>Byte storage for one settings document. Implementations must be safe to call concurrently.</summary>
public interface ISettingsStorage
{
    /// <summary>Gets a diagnostic location for this storage.</summary>
    string Location { get; }

    /// <summary>Reads the current document, or <c>null</c> when it does not exist.</summary>
    Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the document. A successful return means the complete new document is visible;
    /// an exception means the previous document is unchanged.
    /// </summary>
    Task WriteAtomicAsync(byte[] contents, CancellationToken cancellationToken = default);
}

/// <summary>
/// A settings file written beside its destination and renamed into place. If the config
/// directory cannot be created or written, the update fails with
/// <see cref="SettingsFailure.WriteUnavailable"/> and the previous file is unchanged. A stage
/// left by a failed write is removed; a missing file is not created by reads.
/// </summary>
public sealed class SettingsFileStorage : ISettingsStorage
{
    public SettingsFileStorage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A settings path must be absolute.", nameof(path));
        }

        Location = Path.GetFullPath(path);
    }

    public string Location { get; }

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await File.ReadAllBytesAsync(Location, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            throw new SettingsException(SettingsFailure.Unreadable,
                $"Settings file '{Location}' could not be read: {exception.Message}", exception);
        }
    }

    public async Task WriteAtomicAsync(byte[] contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var directory = Path.GetDirectoryName(Location)
            ?? throw new SettingsException(SettingsFailure.WriteUnavailable,
                $"Settings path '{Location}' has no directory.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(Location)}.{Path.GetRandomFileName()}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            RejectSymbolicLinks(Location);
            RejectSymbolicLinks(temporary);
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            }))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, Location, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            throw new SettingsException(SettingsFailure.WriteUnavailable,
                $"Settings file '{Location}' could not be written: {exception.Message}", exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort: the stage name is unique and the destination is already final.
                _ = exception.HResult;
            }
        }
    }

    private static void RejectSymbolicLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null)
            {
                throw new IOException("Settings storage paths must not traverse symbolic links.");
            }
        }
    }
}
