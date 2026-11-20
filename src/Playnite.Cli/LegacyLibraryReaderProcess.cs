using System.Diagnostics;
using Playnite.Core.Import;

namespace Playnite.Cli;

/// <summary>
/// Runs the separate LiteDB reader executable and returns its neutral JSON envelope.
/// Argument lists are used, so no shell parses or expands the paths.
/// </summary>
internal sealed class LegacyLibraryReaderProcess : ILibraryImportReader
{
    private readonly string dotnetExecutable;

    /// <summary>Creates a reader for the compiled <c>Playnite.LegacyImport</c> assembly.</summary>
    public LegacyLibraryReaderProcess(string readerAssemblyPath, string dotnetExecutable = "dotnet")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readerAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetExecutable);
        ReaderAssemblyPath = Path.GetFullPath(readerAssemblyPath);
        this.dotnetExecutable = dotnetExecutable;
    }

    /// <summary>Gets the absolute path of the reader assembly passed to the dotnet host.</summary>
    public string ReaderAssemblyPath { get; }

    /// <inheritdoc />
    public async Task<string> ReadAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ReaderAssemblyPath))
        {
            throw new LibraryImportException(LibraryImportFailure.SourceReadFailed,
                $"The legacy reader assembly was not found: {ReaderAssemblyPath}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(dotnetExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(ReaderAssemblyPath);
        start.ArgumentList.Add(sourceDirectory);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new LibraryImportException(LibraryImportFailure.SourceReadFailed,
                    $"The legacy reader process could not be started: {ReaderAssemblyPath}");
            }
        }
        catch (Exception exception)
        {
            throw new LibraryImportException(LibraryImportFailure.SourceReadFailed,
                $"The legacy reader process could not be started: {exception.Message}", exception);
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            throw;
        }

        var intermediate = await output.ConfigureAwait(false);
        var diagnostic = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new LibraryImportException(LibraryImportFailure.SourceReadFailed,
                $"The legacy reader failed with exit code {process.ExitCode}: {diagnostic.Trim()}");
        }

        return intermediate;
    }
}
