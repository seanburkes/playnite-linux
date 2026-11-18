namespace Playnite.Platform.DesktopIntegration;

public sealed record ExternalCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout)
{
    public ExternalCommand(string fileName, IReadOnlyList<string> arguments)
        : this(fileName, arguments, TimeSpan.FromSeconds(10))
    {
    }
}

public sealed record ExternalCommandResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? Failure)
{
    public bool Succeeded => Started && ExitCode == 0;

    public static ExternalCommandResult NotStarted(string failure) => new(false, -1, "", "", failure);
}

/// <summary>Runs an executable with an argument list; no shell string ever reaches the kernel.</summary>
public interface IExternalCommandRunner
{
    Task<ExternalCommandResult> RunAsync(ExternalCommand command, CancellationToken cancellationToken);
}

public interface IExecutableLocator
{
    bool Exists(string name);
}
