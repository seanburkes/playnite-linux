namespace Playnite.Cli;

/// <summary>
/// Rejects an option that appears more than once. The documented CLI contract treats a
/// duplicate option as an invalid argument (exit code 2).
/// </summary>
internal static class OptionGuard
{
    internal static void Once(HashSet<string> seen, string name, string usage)
    {
        if (!seen.Add(name))
        {
            throw new UsageException($"{name} was specified more than once.{Environment.NewLine}{usage}");
        }
    }
}
