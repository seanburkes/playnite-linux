using Playnite.Platform.Linux;
using Playnite.Platform.Sessions;
using Playnite.Steam;

namespace Playnite.Desktop.Integration;

/// <summary>Asks Steam to launch a game without owning or tracking its process.</summary>
internal sealed class SteamPlayRequest(ISteamUriOpener opener)
{
    private readonly FlatpakSteamLauncher launcher = new(opener);

    public async Task<string> SendAsync(uint appId, string gameName, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = await launcher.LaunchAsync(appId, deadline.Token).ConfigureAwait(false);
            return result.Status switch
            {
                FlatpakLaunchStatus.RequestAccepted => $"Launch requested for {gameName}. Check Steam to see if it started.",
                FlatpakLaunchStatus.Rejected => $"The desktop did not accept the Steam launch request for {gameName}.",
                _ => $"Could not open Steam for {gameName}. Check that Steam is installed.",
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"The launch request for {gameName} was not confirmed. Check Steam before trying again.";
        }
    }

    internal sealed class DesktopOpener : ISteamUriOpener
    {
        public async Task<int> OpenAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            try
            {
                return await new LinuxExternalProcessHost().RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                throw new IOException("The desktop URI opener could not be started.", exception);
            }
        }
    }
}
