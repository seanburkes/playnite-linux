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
        bool started;
        try
        {
            started = process.Start();
        }
        catch (Exception exception)
        {
            throw new LibraryImportException(LibraryImportFailure.SourceReadFailed,
                $"The legacy reader could not start. Verify that the Playnite installation includes its legacy importer. {exception.Message}",
                exception);
        }
        if (!started)
        {
            throw new LibraryImportException(LibraryImportFailure.SourceReadFailed, "The legacy reader could not start.");
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Exception? exitRace = null;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (process.HasExited)
            {
                exitRace = exception;
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (exitRace is not null)
            {
                throw new OperationCanceledException("The legacy reader exited during cancellation.", exitRace, cancellationToken);
            }
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
