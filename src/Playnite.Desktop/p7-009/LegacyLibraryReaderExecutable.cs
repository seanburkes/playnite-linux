using System.ComponentModel;
using System.Diagnostics;
using Playnite.Core.Import;

namespace Playnite.Desktop.Setup;

// The CLI takes an explicit framework-dependent DLL; Desktop ships an apphost so
// a self-contained distribution does not need a dotnet command on PATH.
internal sealed class LegacyLibraryReaderExecutable : ILibraryImportReader
{
    public async Task<string> ReadAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = Path.Combine(AppContext.BaseDirectory, "legacy-import", "Playnite.LegacyImport");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { sourceDirectory },
            },
        };
        try
        {
            if (!process.Start())
            {
                throw new LibraryImportException(LibraryImportFailure.SourceReadFailed, "The legacy reader could not start.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new LibraryImportException(LibraryImportFailure.SourceReadFailed,
                $"The legacy reader could not start. Verify that the Playnite installation includes its legacy importer. {exception.Message}",
                exception);
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // Exit can win the race with cancellation.
            }
            catch (Win32Exception) when (process.HasExited)
            {
                // The OS may have already reaped the child.
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }

        var diagnostic = await error.ConfigureAwait(false);
        var envelope = await output.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new LibraryImportException(LibraryImportFailure.SourceReadFailed,
                $"The legacy reader exited with code {process.ExitCode}: {diagnostic.Trim()}");
        }
        return envelope;
    }
}
