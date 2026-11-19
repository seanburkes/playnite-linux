using System.Text;
using Tmds.DBus.Protocol;

namespace Playnite.Steam.Import;

/// <summary>
/// <see cref="ISteamLibraryPortal"/> over the session's <c>org.freedesktop.portal.FileChooser</c>
/// and <c>org.freedesktop.portal.Documents</c> interfaces. The FileChooser runs in the host; the
/// returned document URI is the only path the sandbox can open. The host path is read through
/// <c>Documents.Info</c> and is used only to tie the grant to a declared Steam library.
/// </summary>
internal sealed class DesktopPortalLibraryPicker : ISteamLibraryPortal, IAsyncDisposable
{
    private const string Destination = "org.freedesktop.portal.Desktop";
    private const string DesktopPath = "/org/freedesktop/portal/desktop";
    private const string FileChooser = "org.freedesktop.portal.FileChooser";
    private const string DocumentsDestination = "org.freedesktop.portal.Documents";
    private const string DocumentsPath = "/org/freedesktop/portal/documents";
    private const string Documents = "org.freedesktop.portal.Documents";
    private const string Request = "org.freedesktop.portal.Request";

    private readonly SemaphoreSlim pick = new(1, 1);
    private DBusConnection? connection;

    public async Task<SteamPortalSelection> SelectSteamAppsDirectoryAsync(string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        await pick.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
            var token = "playnite_" + Guid.NewGuid().ToString("N");
            var sender = (current.UniqueName ?? throw new InvalidOperationException("The session bus connection has no unique name."))
                .TrimStart(':').Replace('.', '_');
            var expectedPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
            var debug = Environment.GetEnvironmentVariable("PLAYNITE_PORTAL_DEBUG") == "1";
            if (debug)
            {
                Console.Error.WriteLine($"portal: unique={current.UniqueName} expected={expectedPath}");
            }
            var completion = new TaskCompletionSource<PortalResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = await WatchAsync(current, expectedPath, token, completion, pathFilter: true).ConfigureAwait(false);

            var writer = current.GetMessageWriter();
            writer.WriteMethodCallHeader(Destination, DesktopPath, FileChooser, "OpenFile", "ssa{sv}");
            writer.WriteString(string.Empty);
            writer.WriteString(title);
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["directory"] = true,
                ["multiple"] = false,
                ["modal"] = true,
                ["handle_token"] = token,
            });

            var handle = await current.CallMethodAsync(writer.CreateMessage(),
                static (Message message, object? _) => message.GetBodyReader().ReadObjectPathAsString(),
                null).ConfigureAwait(false);
            if (debug)
            {
                Console.Error.WriteLine($"portal: handle={handle}");
            }

            if (!string.Equals(handle, expectedPath, StringComparison.Ordinal))
            {
                _ = await WatchAsync(current, handle, token, completion, pathFilter: true).ConfigureAwait(false);
            }

            // The sandboxed session bus is a proxy; a path-scoped match can be rewritten or
            // dropped there. A second match on interface+member only is cheap and still filters
            // by the exact token in the reader. The matches are left registered until the
            // connection is disposed: removing a match through the sandbox proxy closes the
            // connection before the document portal calls that follow the response.
            _ = await WatchAsync(current, null, token, completion, pathFilter: false).ConfigureAwait(false);

            PortalResponse? response;
            try
            {
                response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CloseRequestAsync(current, handle).ConfigureAwait(false);
                throw;
            }

            if (debug)
            {
                Console.Error.WriteLine("portal: response awaited");
            }

            if (response is null)
            {
                return SteamPortalSelection.Unavailable("The desktop portal did not answer the file chooser.");
            }

            if (response.Code != 0)
            {
                return response.Code == 1
                    ? SteamPortalSelection.Cancelled()
                    : SteamPortalSelection.Rejected($"The desktop portal returned response {response.Code}.");
            }

            if (!response.Results.TryGetValue("uris", out var uris) || uris.GetArray<string>() is not { Length: > 0 } paths)
            {
                return SteamPortalSelection.Rejected("The desktop portal returned no directory URI.");
            }

            var selected = new Uri(paths[0]);
            if (!selected.IsFile || !selected.IsAbsoluteUri)
            {
                return SteamPortalSelection.Rejected("The desktop portal returned a non-file URI.");
            }

            var portalPath = Path.TrimEndingDirectorySeparator(selected.LocalPath);
            if (debug)
            {
                Console.Error.WriteLine($"portal: selected={selected} local={portalPath}");
            }

            var (documentId, hostPath) = await ResolveDocumentAsync(current, portalPath, cancellationToken)
                .ConfigureAwait(false);
            return SteamPortalSelection.Selected(portalPath, hostPath, documentId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DBusConnectionClosedException or DBusConnectionException
                                               or DBusErrorReplyException or TimeoutException or InvalidOperationException
                                               or UriFormatException)
        {
            return SteamPortalSelection.Unavailable(exception.Message);
        }
        finally
        {
            pick.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        pick.Dispose();
        if (connection is not null)
        {
            connection.Dispose();
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private static ValueTask<IDisposable> WatchAsync(DBusConnection connection, string? path, string token,
        TaskCompletionSource<PortalResponse?> completion, bool pathFilter)
        => connection.AddMatchAsync(
            pathFilter
                ? new MatchRule
                {
                    Type = MessageType.Signal,
                    Path = path,
                    Interface = Request,
                    Member = "Response",
                }
                : new MatchRule
                {
                    Type = MessageType.Signal,
                    Interface = Request,
                    Member = "Response",
                },
            static (Message message, object? state) => ReadResponse(message, (string)state!),
            (Notification<PortalResponse?> notification) =>
            {
                if (Environment.GetEnvironmentVariable("PLAYNITE_PORTAL_DEBUG") == "1")
                {
                    Console.Error.WriteLine(
                        $"portal: handler type={notification.Type} hasValue={notification.HasValue} completion={notification.IsCompletion}");
                }

                if (notification.IsCompletion)
                {
                    // Notification<T>.Exception is only valid (and only non-null) on a completion.
                    completion.TrySetException(notification.Exception);
                }
                else if (notification.HasValue && notification.Value is not null)
                {
                    completion.TrySetResult(notification.Value);
                }
            },
            false, ObserverFlags.None, token);

    private static async Task CloseRequestAsync(DBusConnection connection, string handle)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, handle, Request, "Close");
        await connection.CallMethodAsync(writer.CreateMessage()).ConfigureAwait(false);
    }

    private async Task<DBusConnection> ConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connection is not null)
        {
            return connection;
        }

        var address = DBusAddress.Session ?? throw new InvalidOperationException("No session bus address is available.");
        var created = new DBusConnection(address);
        await created.ConnectAsync().ConfigureAwait(false);
        connection = created;
        return created;
    }

    /// <summary>
    /// Map a portal-visible path to its document id and host path. A path outside the document
    /// portal mount is not sandboxed: it is both the portal path and the host path.
    /// </summary>
    private static async Task<(string? DocumentId, string? HostPath)> ResolveDocumentAsync(DBusConnection connection,
        string portalPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var debug = Environment.GetEnvironmentVariable("PLAYNITE_PORTAL_DEBUG") == "1";
        if (debug)
        {
            Console.Error.WriteLine($"portal: resolve mount for {portalPath}");
        }

        var mount = await CallForPathAsync(connection, DocumentsDestination, DocumentsPath, Documents,
            "GetMountPoint", string.Empty).ConfigureAwait(false);
        if (debug)
        {
            Console.Error.WriteLine($"portal: mount={mount}");
        }

        if (mount is null || !portalPath.StartsWith(mount + "/", StringComparison.Ordinal))
        {
            return (null, portalPath);
        }

        var relative = portalPath[(mount.Length + 1)..];
        var separator = relative.IndexOf('/', StringComparison.Ordinal);
        var documentId = separator < 0 ? relative : relative[..separator];
        if (documentId.Length == 0)
        {
            return (null, null);
        }

        if (debug)
        {
            Console.Error.WriteLine($"portal: document={documentId}");
        }

        var host = await CallForPathAsync(connection, DocumentsDestination, DocumentsPath, Documents,
            "Info", "s", documentId).ConfigureAwait(false);
        if (debug)
        {
            Console.Error.WriteLine($"portal: host={host}");
        }

        return (documentId, host);
    }

    private static async Task<string?> CallForPathAsync(DBusConnection connection, string destination,
        string objectPath, string @interface, string member, string signature, string? argument = null)
    {
        var writer = connection.GetMessageWriter();
        if (signature.Length == 0)
        {
            // An empty signature header is invalid D-Bus; omit the header entirely.
            writer.WriteMethodCallHeader(destination, objectPath, @interface, member);
        }
        else
        {
            writer.WriteMethodCallHeader(destination, objectPath, @interface, member, signature);
            writer.WriteString(argument ?? throw new ArgumentException("A string argument is required.", nameof(argument)));
        }

        var bytes = await connection.CallMethodAsync(writer.CreateMessage(),
            static (Message message, object? _) => message.GetBodyReader().ReadArrayOfByte(),
            null).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return null;
        }

        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0)
        {
            end--;
        }

        return Encoding.UTF8.GetString(bytes, 0, end);
    }

    private static PortalResponse? ReadResponse(Message message, string token)
    {
        var debug = Environment.GetEnvironmentVariable("PLAYNITE_PORTAL_DEBUG") == "1";
        var path = message.PathAsString ?? string.Empty;
        if (debug)
        {
            Console.Error.WriteLine($"portal: response path={path} token={token}");
        }

        if (!path.EndsWith("/" + token, StringComparison.Ordinal))
        {
            return null;
        }

        var reader = message.GetBodyReader();
        var code = reader.ReadUInt32();
        var results = reader.ReadDictionaryOfStringToVariantValue();
        if (debug)
        {
            Console.Error.WriteLine($"portal: response code={code} results={results.Count}");
        }

        return new PortalResponse(code, results);
    }

    private sealed record PortalResponse(uint Code, Dictionary<string, VariantValue> Results);
}
