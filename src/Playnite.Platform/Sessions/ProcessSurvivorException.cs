namespace Playnite.Platform.Sessions;

/// <summary>
/// A stop that killed the owned process group but found a tracked identity outside that group
/// still running, or a process-group member that could not be proven gone.
/// </summary>
public sealed class ProcessSurvivorException : Exception
{
    public ProcessSurvivorException(string message, IReadOnlyList<ProcessIdentity> survivors)
        : base(message)
    {
        Survivors = survivors;
    }

    public IReadOnlyList<ProcessIdentity> Survivors { get; }
}
