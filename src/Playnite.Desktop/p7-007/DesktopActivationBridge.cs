using Avalonia.Threading;
using Playnite.Core.UriHandling;
using Playnite.Desktop.Bootstrap;
using Playnite.Platform.Integration;
using Playnite.SDK;

namespace Playnite.Desktop.Integration;

/// <summary>
/// Connects <see cref="ISingleInstance.Activated"/> and startup <c>--uridata</c> arguments to
/// <see cref="UriHandlerApi"/> on the UI thread.
/// </summary>
public sealed class DesktopActivationBridge : IDisposable
{
    private readonly ISingleInstance? singleInstance;
    private readonly UriHandlerApi uriHandler;
    private readonly Func<Func<Task<UriDispatchResult>>, Task<UriDispatchResult>> dispatchOnUi;
    private readonly IAppLogger? logger;
    private int disposed;

    public DesktopActivationBridge(
        ISingleInstance? singleInstance,
        UriHandlerApi uriHandler,
        IAppLogger? logger = null,
        Func<Func<Task<UriDispatchResult>>, Task<UriDispatchResult>>? dispatchOnUi = null)
    {
        this.singleInstance = singleInstance;
        this.uriHandler = uriHandler ?? throw new ArgumentNullException(nameof(uriHandler));
        this.logger = logger;
        this.dispatchOnUi = dispatchOnUi ?? (work =>
            Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work));
    }

    public static bool TryExtractUriArgument(IReadOnlyList<string> args, out string? uri)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--uridata", StringComparison.Ordinal))
            {
                if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    uri = args[i + 1];
                    return true;
                }

                uri = null;
                return false;
            }

            if (arg.StartsWith("--uridata=", StringComparison.Ordinal))
            {
                var value = arg["--uridata=".Length..];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    uri = value;
                    return true;
                }

                uri = null;
                return false;
            }

            if (arg.StartsWith("playnite://", StringComparison.OrdinalIgnoreCase))
            {
                uri = arg;
                return true;
            }
        }

        uri = null;
        return false;
    }

    public void Start()
    {
        if (singleInstance is not null)
        {
            singleInstance.Activated += OnActivated;
        }
    }

    public async Task<UriDispatchResult> DispatchUriAsync(string uri, CancellationToken cancellationToken = default)
    {
        var result = await dispatchOnUi(() => uriHandler.DispatchAsync(uri, cancellationToken)).ConfigureAwait(false);
        logger?.Info($"uri-dispatch: status={result.Status}; target={result.Route?.Target}; diagnostic={result.Diagnostic ?? "none"}");
        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (singleInstance is not null)
        {
            singleInstance.Activated -= OnActivated;
        }
    }

    private void OnActivated(object? sender, ActivationRequest request)
    {
        _ = DispatchActivationSafeAsync(request.Uri);
    }

    private async Task DispatchActivationSafeAsync(string uri)
    {
        try
        {
            await DispatchUriAsync(uri).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger?.Error($"uri-dispatch: unhandled failure ({exception.Message})");
        }
    }
}
