using System.Collections.ObjectModel;

namespace Playnite.Platform.Sessions;

/// <summary>
/// An executable and an argv vector. There is deliberately no command-line string: the vector
/// is passed to <c>ProcessStartInfo.ArgumentList</c>, so no shell parses, expands or splits it.
/// Shell interpreters are rejected because launching one would reintroduce shell semantics.
/// </summary>
public sealed class LaunchRequest
{
    private static readonly HashSet<string> ShellExecutables = new(StringComparer.Ordinal)
    {
        "sh", "bash", "dash", "zsh", "ksh", "mksh", "pdksh", "csh", "tcsh", "fish", "yash", "rbash", "busybox",
    };

    public LaunchRequest(string executable, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("The executable must be an absolute path; PATH lookup and shells are not used.", nameof(executable));
        }

        if (executable.Contains('\0'))
        {
            throw new ArgumentException("The executable cannot contain a NUL character.", nameof(executable));
        }

        if (ShellExecutables.Contains(Path.GetFileName(executable)))
        {
            throw new ArgumentException("A shell interpreter cannot be launched; pass the real executable and an argv vector.", nameof(executable));
        }

        ArgumentNullException.ThrowIfNull(arguments);
        var copy = arguments.ToArray();
        if (copy.Any(argument => argument is null))
        {
            throw new ArgumentException("Arguments cannot be null.", nameof(arguments));
        }

        if (copy.Any(argument => argument.Contains('\0')))
        {
            throw new ArgumentException("Arguments cannot contain a NUL character.", nameof(arguments));
        }

        if (workingDirectory is not null)
        {
            if (!Path.IsPathFullyQualified(workingDirectory))
            {
                throw new ArgumentException("The working directory must be an absolute path.", nameof(workingDirectory));
            }

            if (workingDirectory.Contains('\0'))
            {
                throw new ArgumentException("The working directory cannot contain a NUL character.", nameof(workingDirectory));
            }
        }

        Executable = executable;
        Arguments = new ReadOnlyCollection<string>(copy);
        WorkingDirectory = workingDirectory;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }
}
