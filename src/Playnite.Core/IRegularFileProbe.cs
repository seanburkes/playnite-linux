namespace Playnite.Core;

/// <summary>
/// Classifies one filesystem entry by its own type. The answer must not follow a final symbolic
/// link and must not block when the entry is a FIFO, socket or device; the host implementation
/// owns the native call that reads the entry's file type. On Unix
/// <see cref="System.IO.DirectoryInfo.EnumerateFileSystemInfos"/> reports every entry that is
/// not a directory as a <see cref="System.IO.FileInfo"/>, so only the type probe can tell a
/// regular package file from a FIFO that would block a copy forever.
/// </summary>
public interface IRegularFileProbe
{
    /// <summary>Gets whether <paramref name="path"/> names a regular file.</summary>
    /// <param name="path">Absolute path of the entry to classify.</param>
    bool IsRegularFile(string path);
}
