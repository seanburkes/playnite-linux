using Playnite.Platform.Sessions;
using Playnite.Steam;

namespace Playnite.Desktop.Shell;

/// <summary>Asks Heroic Games Launcher to launch an installed Epic/GOG/Amazon game via xdg-open argv.</summary>
internal sealed class HeroicPlayRequest(ISteamUriOpener opener, TimeSpan? launchTimeout = null)
{
    private const string DesktopOpenerExecutable = "/usr/bin/xdg-open";
    private readonly TimeSpan timeout = launchTimeout ?? TimeSpan.FromSeconds(10);

    public async Task<string> SendAsync(
        Guid pluginId,
        string gameId,
        string gameName,
        CancellationToken cancellationToken)
    {
        if (!StoreCatalog.TryBuildHeroicLaunchUri(pluginId, gameId, out var uri))
        {
            return $"Cannot launch {gameName}: invalid or unsupported Heroic store identifier.";
        }

        var request = new LaunchRequest(DesktopOpenerExecutable, [uri]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var exitCode = await opener.OpenAsync(request, deadline.Token).ConfigureAwait(false);
            return exitCode == 0
                ? $"Launch requested for {gameName} via Heroic. Check Heroic to see if it started."
                : $"The desktop did not accept the Heroic launch request for {gameName}.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"The Heroic launch request for {gameName} was not confirmed. Check Heroic before trying again.";
        }
        catch (IOException)
        {
            return $"Could not open Heroic for {gameName}. Check that Heroic Games Launcher is installed.";
        }
    }
}
