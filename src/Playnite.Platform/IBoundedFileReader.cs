namespace Playnite.Platform;

public enum BoundedFileReadStatus { Ok, Missing, NotRegularFile, TooLarge }

public sealed record BoundedFileRead(BoundedFileReadStatus Status, byte[]? Bytes);

/// <summary>Read-only, handle-bound reads of canonical paths; no component may be a symlink.</summary>
public interface IBoundedFileReader
{
    BoundedFileRead Read(string path, int maxBytes);
}
