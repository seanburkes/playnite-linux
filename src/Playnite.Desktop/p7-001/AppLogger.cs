using System.Globalization;
using System.Text;
using Playnite.Core;

namespace Playnite.Desktop.Bootstrap;

/// <summary>Startup log sink. The composition root supplies it to every service that reports.</summary>
public interface IAppLogger : IDisposable
{
    void Info(string message);

    void Error(string message);
}

/// <summary>One formatted log line, kept pure so formatting is testable without a file.</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message)
{
    public string Format() => string.Format(
        CultureInfo.InvariantCulture,
        "{0:yyyy-MM-ddTHH:mm:ssZ} {1} {2}",
        Timestamp,
        Level,
        Message);
}

/// <summary>Append-only UTF-8 startup log. Disposal releases the file handle.</summary>
public sealed class FileAppLogger : IAppLogger
{
    private readonly StreamWriter writer;
    private readonly object gate = new();
    private bool disposed;

    public FileAppLogger(string path, IRegularFileProbe probe)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = BootstrapFiles.IsRegular(path, probe);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The log path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message) => Write("ERROR", message);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            writer.Dispose();
        }
    }

    private void Write(string level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var line = new LogEntry(DateTimeOffset.UtcNow, level, message).Format();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            writer.WriteLine(line);
        }
    }
}
