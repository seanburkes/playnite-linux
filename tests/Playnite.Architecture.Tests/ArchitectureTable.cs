namespace Playnite.Architecture.Tests;

/// <summary>
/// Reads the allowed-reference table in docs/port/02-ARCHITECTURE.md section 2.
/// The document is the single source of truth; the tests never keep a second copy.
/// </summary>
internal sealed record ArchitectureRow(string Project, string[] MayReference, string[] MustNotReference)
{
    internal string[] AllowedProjects => MayReference.Select(ArchitectureTable.ResolveProject).OfType<string>().ToArray();
    internal string[] ForbiddenProjects => MustNotReference.Select(ArchitectureTable.ResolveProject).OfType<string>().ToArray();

    internal string[] AllowedPackages => MayReference
        .Where(token => ArchitectureTable.ResolveProject(token) == null && ArchitectureTable.IsPackageToken(token))
        .Select(ArchitectureTable.PackageName).ToArray();
}

internal static class ArchitectureTable
{
    internal static readonly string[] ProjectNames =
    [
        "Playnite.SDK.Abstractions",
        "Playnite.SDK",
        "Playnite.Core",
        "Playnite.Platform",
        "Playnite.Platform.Linux",
        "Playnite.Steam",
        "Playnite.LegacyImport.Contracts",
        "Playnite.LegacyImport",
        "Playnite.Desktop",
        "Playnite.Cli",
    ];

    private static readonly Lazy<IReadOnlyList<ArchitectureRow>> Cached =
        new(() => Parse(File.ReadAllText(Path.Combine(ProductGraph.Root, "docs", "port", "02-ARCHITECTURE.md"))));

    internal static IReadOnlyList<ArchitectureRow> Rows => Cached.Value;

    /// <summary>Product rows only; the table's Tests row is not a product.</summary>
    internal static IReadOnlyList<ArchitectureRow> ProductRows => Rows.Where(row => ProjectNames.Contains(row.Project)).ToArray();

    internal static ArchitectureRow For(string project) => ProductRows.Single(row => row.Project == project);

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // The table's shorthands for the two full project names it abbreviates.
        ["Abstractions"] = "Playnite.SDK.Abstractions",
        ["Contracts"] = "Playnite.LegacyImport.Contracts",
    };

    internal static string? ResolveProject(string token)
    {
        var name = NormalizeToken(token);
        if (name.Length == 0)
        {
            return null;
        }

        if (Aliases.TryGetValue(name, out var aliased))
        {
            return aliased;
        }

        return ProjectNames.FirstOrDefault(project =>
            name == project || name == project["Playnite.".Length..]);
    }

    internal static string NormalizeToken(string token)
    {
        var value = token.Replace("`", string.Empty, StringComparison.Ordinal).Trim();
        var parenthesis = value.IndexOf(" (", StringComparison.Ordinal);
        if (parenthesis >= 0)
        {
            value = value[..parenthesis];
        }

        var space = value.IndexOf(' ');
        if (space >= 0)
        {
            value = value[..space];
        }

        return value.Trim();
    }

    internal static bool IsPackageToken(string token)
    {
        var name = NormalizeToken(token);
        return name.Length > 0
            && !name.StartsWith("nothing", StringComparison.OrdinalIgnoreCase)
            && (char.IsUpper(name[0]) || name.Contains('.', StringComparison.Ordinal));
    }

    internal static string PackageName(string token) => NormalizeToken(token);

    internal static IReadOnlyList<ArchitectureRow> Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == "## 2. Projects and dependency rules");
        if (start < 0)
        {
            throw new InvalidOperationException(
                "docs/port/02-ARCHITECTURE.md section 2 'Projects and dependency rules' was not found");
        }

        var rows = new List<ArchitectureRow>();
        var headerSeen = false;
        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith('|'))
            {
                if (rows.Count > 0)
                {
                    break;
                }

                continue;
            }

            var cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length < 4)
            {
                throw new InvalidOperationException(
                    $"docs/port/02-ARCHITECTURE.md section 2 table row has {cells.Length} cells: {line}");
            }

            if (cells[0] == "Project")
            {
                headerSeen = true;
                continue;
            }

            if (cells.All(cell => cell.Length == 0 || cell.All(c => c is '-' or ':' or ' ')))
            {
                continue;
            }

            var project = cells[0].Replace("`", string.Empty, StringComparison.Ordinal).Trim();
            if (project == "Tests")
            {
                continue;
            }

            if (!ProjectNames.Contains(project))
            {
                throw new InvalidOperationException(
                    $"docs/port/02-ARCHITECTURE.md lists unknown project '{project}'; update ArchitectureTable.ProjectNames");
            }

            if (rows.Any(row => row.Project == project))
            {
                throw new InvalidOperationException(
                    $"docs/port/02-ARCHITECTURE.md lists project '{project}' more than once");
            }

            var mayReference = SplitTokens(cells[2]);
            var mustNotReference = SplitTokens(cells[3]);
            if (mayReference.Length == 0 || !mayReference.Any(token => token.StartsWith("nothing", StringComparison.OrdinalIgnoreCase))
                && mayReference.All(token => ResolveProject(token) == null))
            {
                throw new InvalidOperationException(
                    $"docs/port/02-ARCHITECTURE.md row '{project}' has no resolvable project reference and no explicit 'nothing' allowance");
            }

            rows.Add(new ArchitectureRow(project, mayReference, mustNotReference));
        }

        if (!headerSeen || rows.Count == 0)
        {
            throw new InvalidOperationException(
                "docs/port/02-ARCHITECTURE.md section 2 has no architecture rows; the architecture gate would pass vacuously");
        }

        return rows;
    }

    private static string[] SplitTokens(string cell) => cell
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(token => token.Length > 0 && token != "—" && token != "-")
        .ToArray();
}
