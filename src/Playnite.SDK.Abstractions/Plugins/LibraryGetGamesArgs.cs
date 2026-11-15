namespace Playnite.SDK.Plugins;

/// <summary>Arguments for library game discovery.</summary>
public class LibraryGetGamesArgs
{
    public CancellationToken CancelToken { get; internal set; }

    public LibraryGetGamesArgs()
    {
    }

    // The host now lives in a separate assembly and cannot use the internal setter.
    public LibraryGetGamesArgs(CancellationToken cancelToken)
    {
        CancelToken = cancelToken;
    }
}
