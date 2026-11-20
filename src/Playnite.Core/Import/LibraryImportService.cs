namespace Playnite.Core.Import;

/// <summary>Where an import stopped or completed. A planned dry run never publishes.</summary>
public enum LibraryImportOutcome
{
    /// <summary>The source was validated and a report was produced; no destination was written.</summary>
    Planned,

    /// <summary>The migration returned after publishing a verified destination.</summary>
    Published,
}

/// <summary>Checkpoints reported while an import runs, in migration order.</summary>
public enum LibraryImportStage
{
    /// <summary>The legacy reader is producing the neutral JSON envelope.</summary>
    Reading,

    /// <summary>The reader envelope is complete.</summary>
    Read,

    /// <summary>Records have been validated and bound to port models.</summary>
    Transformed,

    /// <summary>Referenced assets have been copied and their fields rewritten.</summary>
    Copied,

    /// <summary>A partial asset blob has been written.</summary>
    AssetPartialCopy,

    /// <summary>An asset blob has been flushed to disk.</summary>
    AssetFlushed,

    /// <summary>One game has been written to the staged database.</summary>
    PartialWrite,

    /// <summary>All records have been written to the staged database.</summary>
    Written,

    /// <summary>The staged database passed value, integrity and foreign-key checks.</summary>
    Verified,

    /// <summary>The verified database is ready for the publication lease.</summary>
    BeforeSwap,

    /// <summary>The publication lease is held and the swap is starting.</summary>
    Publishing,

    /// <summary>The staged database has been renamed over the live path.</summary>
    Swapped,

    /// <summary>The published database carried the expected migration receipt.</summary>
    ReceiptConfirmed,

    /// <summary>The import is complete; publication has succeeded.</summary>
    Committed,

    /// <summary>The external audit report is about to be moved into place.</summary>
    BeforeReportMove,

    /// <summary>A checkpoint this service version does not name.</summary>
    Unknown,
}

/// <summary>One progress notification. <see cref="Checkpoint"/> is the underlying migration name.</summary>
public sealed record LibraryImportProgress(LibraryImportStage Stage, string Checkpoint);

/// <summary>Inputs for one import. Paths and containment are validated by the migration itself.</summary>
public sealed record LibraryImportRequest(string SourceDirectory, string Destination, bool DryRun = false,
    bool ReplaceExisting = false);

/// <summary>The structured outcome of an import. Published is set only after migration returns.</summary>
public sealed record LibraryImportResult(LibraryImportOutcome Outcome, MigrationReport Report);

/// <summary>Why an import failed before it could return a <see cref="LibraryImportResult"/>.</summary>
public enum LibraryImportFailure
{
    /// <summary>The request paths were invalid or the destination was inside the source.</summary>
    InvalidRequest,

    /// <summary>The legacy reader could not start, was cancelled or exited unsuccessfully.</summary>
    SourceReadFailed,

    /// <summary>The intermediate source failed validation or could not be transformed.</summary>
    SourceRejected,

    /// <summary>The destination or one of its migration artifacts could not be used.</summary>
    DestinationUnavailable,

    /// <summary>The migration returned success but the published destination is absent.</summary>
    PublicationFailed,
}

/// <summary>An import failure carrying a stable, machine-readable classification.</summary>
public sealed class LibraryImportException : Exception
{
    public LibraryImportException(LibraryImportFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Gets the failure category, independent of the exception message.</summary>
    public LibraryImportFailure Failure { get; }
}

/// <summary>Produces the neutral v10 JSON envelope consumed by <see cref="LibraryMigration"/>.</summary>
public interface ILibraryImportReader
{
    Task<string> ReadAsync(string sourceDirectory, CancellationToken cancellationToken = default);
}

/// <summary>Runs a v10 library through the existing reader and verified migration as one service.</summary>
public sealed class LibraryImportService
{
    private readonly ILibraryImportReader reader;
    private readonly ILibraryMigrator migrator;

    /// <summary>Creates the service over a reader. The verified <see cref="LibraryMigration"/> path is used.</summary>
    public LibraryImportService(ILibraryImportReader reader)
        : this(reader, new LibraryMigrator())
    {
    }

    internal LibraryImportService(ILibraryImportReader reader, ILibraryMigrator migrator)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(migrator);
        this.reader = reader;
        this.migrator = migrator;
    }

    /// <summary>
    /// Reads the v10 source, runs the verified migration and returns its structured report.
    /// Re-run refusal, publication safety and cancellation outcomes are the migration's contract.
    /// </summary>
    public async Task<LibraryImportResult> ImportAsync(LibraryImportRequest request,
        IProgress<LibraryImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new(LibraryImportStage.Reading, "Reading"));
        string intermediate;
        try
        {
            intermediate = await reader.ReadAsync(request.SourceDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LibraryImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            throw new LibraryImportException(LibraryImportFailure.SourceReadFailed,
                $"The v10 library could not be read: {exception.Message}", exception);
        }

        if (string.IsNullOrEmpty(intermediate))
        {
            throw new LibraryImportException(LibraryImportFailure.SourceReadFailed,
                "The legacy reader returned an empty intermediate envelope.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        MigrationReport report;
        try
        {
            report = await migrator.RunAsync(intermediate, request.SourceDirectory, request.Destination,
                request.DryRun,
                checkpoint => progress?.Report(new(StageFor(checkpoint), checkpoint)),
                cancellationToken, request.ReplaceExisting).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LibraryImportException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new LibraryImportException(LibraryImportFailure.SourceRejected,
                $"The v10 library failed migration validation: {exception.Message}", exception);
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            throw new LibraryImportException(LibraryImportFailure.SourceRejected,
                $"The v10 intermediate envelope is not valid JSON: {exception.Message}", exception);
        }
        catch (ArgumentException exception)
        {
            throw new LibraryImportException(LibraryImportFailure.InvalidRequest, exception.Message, exception);
        }
        catch (IOException exception)
        {
            throw new LibraryImportException(LibraryImportFailure.DestinationUnavailable, exception.Message, exception);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new LibraryImportException(LibraryImportFailure.DestinationUnavailable, exception.Message, exception);
        }

        if (!request.DryRun && !File.Exists(request.Destination))
        {
            throw new LibraryImportException(LibraryImportFailure.PublicationFailed,
                "The migration reported success but the destination database is missing.");
        }

        return new(request.DryRun ? LibraryImportOutcome.Planned : LibraryImportOutcome.Published, report);
    }

    private static LibraryImportStage StageFor(string checkpoint) => checkpoint switch
    {
        "Read" => LibraryImportStage.Read,
        "Transformed" => LibraryImportStage.Transformed,
        "Copied" => LibraryImportStage.Copied,
        "AssetPartialCopy" => LibraryImportStage.AssetPartialCopy,
        "AssetFlushed" => LibraryImportStage.AssetFlushed,
        "PartialWrite" => LibraryImportStage.PartialWrite,
        "Written" => LibraryImportStage.Written,
        "Verified" => LibraryImportStage.Verified,
        "BeforeSwap" => LibraryImportStage.BeforeSwap,
        "Publishing" => LibraryImportStage.Publishing,
        "Swapped" => LibraryImportStage.Swapped,
        "ReceiptConfirmed" => LibraryImportStage.ReceiptConfirmed,
        "Committed" => LibraryImportStage.Committed,
        "BeforeReportMove" => LibraryImportStage.BeforeReportMove,
        _ => LibraryImportStage.Unknown,
    };
}

internal interface ILibraryMigrator
{
    Task<MigrationReport> RunAsync(string intermediate, string sourceDirectory, string destination, bool dryRun,
        Action<string>? checkpoint, CancellationToken cancellationToken, bool replaceExisting);
}

internal sealed class LibraryMigrator : ILibraryMigrator
{
    public Task<MigrationReport> RunAsync(string intermediate, string sourceDirectory, string destination, bool dryRun,
        Action<string>? checkpoint, CancellationToken cancellationToken, bool replaceExisting)
        => LibraryMigration.RunAsync(intermediate, sourceDirectory, destination, dryRun, checkpoint, cancellationToken,
            replaceExisting);
}
