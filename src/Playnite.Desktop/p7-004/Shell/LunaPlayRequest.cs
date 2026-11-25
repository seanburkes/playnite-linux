using System.Diagnostics;
using System.Runtime.InteropServices;
using Playnite.Core.Models;
using Playnite.Platform.Sessions;

namespace Playnite.Desktop.Shell;

internal sealed record LunaHandoff(bool Requested, string Status);

internal sealed class LunaPlayRequest(
    Func<string, string?>? toolResolver = null,
    Action<LaunchRequest>? startBrowser = null,
    string? configuredExecutable = null)
{
    internal const string FortniteUrl = "https://luna.amazon.com/game/fortnite?autoplay=1&g=web&ref=tmp_ext_epic";
    internal const string ChromeExecutableVariable = "PLAYNITE_LUNA_CHROME";
    internal const string InvalidUrlMessage = "Enter an HTTPS game link from luna.amazon.com.";

    internal static GameAction? FindPlayAction(Game game) =>
        game.PluginId == StoreCatalog.LunaPluginId
            ? game.GameActions?.FirstOrDefault(action => action is { Type: GameActionType.URL, IsPlayAction: true })
            : null;

    internal static Uri? ParseGameUrl(string? value)
    {
        if (value is null || value.Any(char.IsControl)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "luna.amazon.com", StringComparison.Ordinal)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || !uri.AbsolutePath.StartsWith("/game/", StringComparison.Ordinal)
            || !HasGameSlug(uri.AbsolutePath))
        {
            return null;
        }

        return uri;
    }

    private static bool HasGameSlug(string path)
    {
        var remainder = path.AsSpan("/game/".Length);
        var separator = remainder.IndexOf('/');
        var slug = Uri.UnescapeDataString((separator < 0 ? remainder : remainder[..separator]).ToString());
        return slug.Any(char.IsLetterOrDigit) && !slug.Any(character =>
            char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '\\');
    }

    internal Task<LunaHandoff> SendAsync(string url, string gameName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ParseGameUrl(url) is not { } uri)
        {
            return Task.FromResult(new LunaHandoff(false, InvalidUrlMessage));
        }

        if (configuredExecutable is not null
            && (!Path.IsPathFullyQualified(configuredExecutable) || !File.Exists(configuredExecutable)))
        {
            return Task.FromResult(new LunaHandoff(false, $"{ChromeExecutableVariable} must point to a Chrome executable using an absolute path."));
        }

        var executable = configuredExecutable ?? GameLaunchPlanner.ResolveToolPath("google-chrome-stable", toolResolver)
            ?? GameLaunchPlanner.ResolveToolPath("google-chrome", toolResolver);
        if (executable is null)
        {
            return Task.FromResult(new LunaHandoff(false, "Google Chrome was not found. Install Google Chrome to open Luna."));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (startBrowser ?? StartBrowser)(new LaunchRequest(executable, [uri.AbsoluteUri]));
            return Task.FromResult(new LunaHandoff(true, $"Requested Chrome to open {gameName} on Amazon Luna."));
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new LunaHandoff(false, $"Could not open Google Chrome for {gameName}. Check your Chrome installation."));
        }
    }

    internal static LunaPlayRequest CreateDefault()
    {
        var configured = Environment.GetEnvironmentVariable(ChromeExecutableVariable);
        return new LunaPlayRequest(configuredExecutable: string.IsNullOrWhiteSpace(configured) ? null : configured);
    }

    private static void StartBrowser(LaunchRequest request)
    {
        var info = new ProcessStartInfo(request.Executable) { UseShellExecute = false };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var browser = Process.Start(info) ?? throw new IOException("Chrome did not start.");
    }
}
