namespace Playnite.Core.Tests.Integration;

internal sealed class IntegrationTemporaryDirectory : IDisposable
{
    internal IntegrationTemporaryDirectory()
    {
        Path = Directory.CreateTempSubdirectory("p5-005a-").FullName;
    }

    internal string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
