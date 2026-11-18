using Playnite.Platform.DesktopIntegration;

namespace Playnite.Platform.Linux.DesktopIntegration;

/// <summary>PATH lookup without a shell. Empty PATH entries are ignored rather than meaning cwd.</summary>
public sealed class LinuxExecutableLocator : IExecutableLocator
{
    private readonly string pathValue;

    public LinuxExecutableLocator()
        : this(Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
    {
    }

    public LinuxExecutableLocator(string pathValue) => this.pathValue = pathValue;

    public bool Exists(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.Contains('/', StringComparison.Ordinal))
        {
            return File.Exists(name);
        }

        foreach (var directory in pathValue.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, name)))
            {
                return true;
            }
        }

        return false;
    }
}
