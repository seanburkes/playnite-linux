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

    public async Task<string> OpenExternalUriAsync(
        string rawUri,
        bool httpsOnly,
        string gameName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawUri)
            || rawUri.Any(char.IsControl)
            || !Uri.TryCreate(rawUri.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0)
        {
            return $"Cannot launch {gameName}: invalid launch URI.";
        }

        var scheme = uri.Scheme;
        var allowed = httpsOnly
            ? string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            : !string.Equals(scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
              && !string.Equals(scheme, "javascript", StringComparison.OrdinalIgnoreCase)
              && !string.Equals(scheme, "data", StringComparison.OrdinalIgnoreCase)
              && !string.Equals(scheme, "vbscript", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
        {
            return $"Cannot launch {gameName}: URI scheme '{scheme}' is not permitted.";
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var exitCode = await opener.OpenAsync(
                new LaunchRequest(FlatpakSteamLauncher.DefaultOpenerPath, [uri.AbsoluteUri]),
                deadline.Token).ConfigureAwait(false);
            return exitCode == 0
                ? $"Launch requested for {gameName}."
                : $"The desktop did not accept the launch request for {gameName}.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"The launch request for {gameName} was not confirmed.";
        }
        catch (IOException)
        {
            return $"Could not open the launch URI for {gameName}.";
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new IOException("The desktop URI opener could not be started.", exception);
            }
        }
    }
}
