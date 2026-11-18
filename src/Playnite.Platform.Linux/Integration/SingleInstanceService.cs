using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Playnite.Platform.Integration;

namespace Playnite.Platform.Linux.Integration;

/// <summary>
/// Linux single-instance guard. Ownership is an advisory lock on a file in the runtime
/// directory; the kernel drops it when the owning process exits or is killed, so a crash never
/// leaves a lock that must be broken manually. The lock file itself may remain and is harmless.
/// A Unix domain socket next to the lock forwards activation to the owner when the path fits
/// the platform limit; otherwise ownership still works and the diagnostic says forwarding is
/// unavailable.
/// </summary>
public sealed class SingleInstanceService : ISingleInstance
{
    private const int MaxInstanceIdLength = 64;
    private const int MaxActivationBytes = 4096;
    private const int MaxSocketPathBytes = 100;
    private const int ActivationTimeoutMilliseconds = 5000;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly FileStream? lockStream;
    private readonly Socket? listener;
    private readonly string? socketPath;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task? acceptLoop;
    private int disposed;

    private SingleInstanceService(
        SingleInstanceResult result,
        FileStream? lockStream,
        string? socketPath,
        Socket? listener)
    {
        Result = result;
        this.lockStream = lockStream;
        this.socketPath = socketPath;
        this.listener = listener;
        if (listener is not null)
        {
            acceptLoop = AcceptLoopAsync(listener, shutdown.Token);
        }
    }

    public SingleInstanceResult Result { get; }

    public event EventHandler<ActivationRequest>? Activated;

    /// <summary>
    /// Acquires ownership or reports that a live process already owns it. This method never
    /// throws for environmental failures; the caller gets an <see cref="SingleInstanceStatus.Unavailable"/>
    /// result and may continue without the guard.
    /// </summary>
    public static SingleInstanceService Start(string instanceId, XdgDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ValidateInstanceId(instanceId);

        var runtimeFailure = directories.EnsureRuntimeDirectory();
        if (runtimeFailure is not null)
        {
            return new SingleInstanceService(new SingleInstanceResult(SingleInstanceStatus.Unavailable, runtimeFailure), null, null, null);
        }

        var name = instanceId;
        var lockPath = Path.Combine(directories.Runtime, name + ".lock");
        var socketPath = Path.Combine(directories.Runtime, name + ".sock");

        FileStream lockStream;
        try
        {
            lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return new SingleInstanceService(
                new SingleInstanceResult(
                    SingleInstanceStatus.AlreadyRunning,
                    $"Another Playnite instance already owns {lockPath}. Activate it instead of starting a second instance."),
                null,
                socketPath,
                null);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new SingleInstanceService(
                new SingleInstanceResult(SingleInstanceStatus.Unavailable, $"Cannot open the single-instance lock {lockPath}: {exception.Message}"),
                null,
                null,
                null);
        }

        try
        {
            var owner = Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n");
            lockStream.SetLength(0);
            lockStream.Write(owner);
            lockStream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lockStream.Dispose();
            return new SingleInstanceService(
                new SingleInstanceResult(SingleInstanceStatus.Unavailable, $"Cannot record ownership in {lockPath}: {exception.Message}"),
                null,
                null,
                null);
        }

        var (socket, diagnostic) = StartListener(socketPath);
        var result = new SingleInstanceResult(SingleInstanceStatus.Owner, diagnostic);
        return new SingleInstanceService(result, lockStream, socket is null ? null : socketPath, socket);
    }

    public async Task<SingleInstanceResult> ActivateAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return new SingleInstanceResult(SingleInstanceStatus.Unavailable, "The single-instance service is disposed.");
        }

        if (Result.IsOwner)
        {
            return new SingleInstanceResult(SingleInstanceStatus.Owner, "This process owns the instance; dispatch the activation locally.");
        }

        if (socketPath is null)
        {
            return new SingleInstanceResult(
                SingleInstanceStatus.Unavailable,
                Result.Diagnostic ?? "Activation forwarding is unavailable for the running instance.");
        }

        if (Encoding.UTF8.GetByteCount(socketPath) > MaxSocketPathBytes)
        {
            return new SingleInstanceResult(
                SingleInstanceStatus.Unavailable,
                $"Activation forwarding unavailable: the socket path {socketPath} exceeds the Unix socket limit.");
        }

        if (!PlayniteUri.TryParse(uri, out _, out var error) || uri.Contains('\n') || uri.Contains('\r'))
        {
            return new SingleInstanceResult(
                SingleInstanceStatus.Unavailable,
                $"Refusing to forward a malformed activation URI: {error ?? "the URI contains a line break"}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ActivationTimeoutMilliseconds);
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token).ConfigureAwait(false);
            await client.SendAsync(Encoding.UTF8.GetBytes(uri + "\n"), SocketFlags.None, timeout.Token).ConfigureAwait(false);
            client.Shutdown(SocketShutdown.Send);

            var acknowledgement = new byte[16];
            var received = await client.ReceiveAsync(acknowledgement, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            var reply = Encoding.UTF8.GetString(acknowledgement, 0, received);
            if (!reply.StartsWith("OK", StringComparison.Ordinal))
            {
                return new SingleInstanceResult(SingleInstanceStatus.Unavailable, $"The owner rejected the activation: {reply.Trim()}");
            }

            return new SingleInstanceResult(SingleInstanceStatus.Forwarded, null);
        }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return new SingleInstanceResult(
                SingleInstanceStatus.Unavailable,
                $"Cannot forward activation to the running instance: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        shutdown.Cancel();
        listener?.Dispose();
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                Debug.WriteLine(exception);
            }
        }

        if (socketPath is not null)
        {
            try
            {
                File.Delete(socketPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(exception);
            }
        }

        lockStream?.Dispose();
        shutdown.Dispose();
    }

    private static (Socket? Socket, string? Diagnostic) StartListener(string socketPath)
    {
        if (Encoding.UTF8.GetByteCount(socketPath) > MaxSocketPathBytes)
        {
            return (null, $"Activation forwarding unavailable: the socket path {socketPath} exceeds the Unix socket limit; ownership still applies.");
        }

        try
        {
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Bind(new UnixDomainSocketEndPoint(socketPath));
                socket.Listen(4);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                return (socket, null);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
        {
            return (null, $"Activation forwarding unavailable: {exception.Message}");
        }
    }

    private async Task AcceptLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Debug.WriteLine(exception);
                }

                return;
            }

            using (client)
            {
                await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxActivationBytes + 1];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var received = await client.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (received == 0)
                {
                    break;
                }

                length += received;
                if (buffer.AsSpan(0, length).IndexOf((byte)'\n') >= 0)
                {
                    break;
                }
            }

            var newline = buffer.AsSpan(0, length).IndexOf((byte)'\n');
            if (newline < 0 || newline == 0 || newline > MaxActivationBytes)
            {
                await ReplyAsync(client, "ERR malformed activation\n", cancellationToken).ConfigureAwait(false);
                return;
            }

            var uri = StrictUtf8.GetString(buffer, 0, newline);
            if (!PlayniteUri.TryParse(uri, out _, out var error))
            {
                await ReplyAsync(client, $"ERR {error}\n", cancellationToken).ConfigureAwait(false);
                return;
            }

            await ReplyAsync(client, "OK\n", cancellationToken).ConfigureAwait(false);
            try
            {
                Activated?.Invoke(this, new ActivationRequest(uri));
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }
        }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException)
        {
            Debug.WriteLine(exception);
        }
    }

    private static async Task ReplyAsync(Socket client, string reply, CancellationToken cancellationToken)
    {
        await client.SendAsync(Encoding.UTF8.GetBytes(reply), SocketFlags.None, cancellationToken).ConfigureAwait(false);
        client.Shutdown(SocketShutdown.Send);
    }

    private static void ValidateInstanceId(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId) || instanceId.Length > MaxInstanceIdLength
            || !instanceId.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-')
            || instanceId is "." or "..")
        {
            throw new ArgumentException(
                $"Instance ids must be 1-{MaxInstanceIdLength} lowercase letters, digits, '.', '_' or '-' and must not be a traversal segment.",
                nameof(instanceId));
        }
    }
}
