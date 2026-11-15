namespace Playnite.Architecture.Tests;

/// <summary>
/// Reads the allowed-reference table in docs/port/02-ARCHITECTURE.md section 2.
/// The document is the single source of truth; the tests never keep a second copy.
/// Tokens are validated against a closed vocabulary so a typo cannot turn a
/// prohibition into a silent allowance.
/// </summary>
internal sealed record ArchitectureRow(string Project, string[] MayReference, string[] MustNotReference)
{
    internal string[] AllowedProjects => MayReference.Select(ArchitectureTable.ResolveProject).OfType<string>().ToArray();
    internal string[] ForbiddenProjects => MustNotReference.Select(ArchitectureTable.ResolveProject).OfType<string>().ToArray();
    internal string[] AllowedPackages => MayReference.Where(ArchitectureTable.IsPackageToken).Select(ArchitectureTable.PackageName).ToArray();
    internal string[] ForbiddenPackages => MustNotReference.Where(ArchitectureTable.IsPackageToken).Select(ArchitectureTable.PackageName).ToArray();
    internal string[] ForbiddenApis => MustNotReference.Where(ArchitectureTable.IsApiToken).Select(ArchitectureTable.NormalizeToken).ToArray();
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

    // The table's closed package vocabulary. A new package allowance must be added here in the
    // same change, so a misspelling fails instead of silently parsing as an allowed package.
    internal static readonly string[] PackageNames =
    [
        "Avalonia",
        "Dapper",
        "LiteDB",
        "Microsoft.Data.Sqlite",
        "Newtonsoft.Json",
        "NLog",
        "YamlDotNet",
    ];

    // Banned API names that appear in the Must-not column; their enforcement is the source/IL
    // gate, but a typo here must still fail table validation. "Win32" is the table's shorthand
    // for Microsoft.Win32.
    internal static readonly string[] ApiNames = ["DllImport", "Microsoft.Win32", "System.Drawing", "System.Windows", "Win32"];

    // Avalonia qualifiers from the table and the clarification below it. A null entry means the
    // row's unqualified "Avalonia" admits the package family; otherwise only the named packages
    // are allowed (for example, Desktop owns views/themes, SDK only UI contracts).
    private static readonly Dictionary<string, string[]?> AvaloniaQualifiers = new(StringComparer.Ordinal)
    {
        ["Playnite.SDK"] = ["Avalonia"],
        ["Playnite.Platform"] = ["Avalonia", "Avalonia.Base"],
        ["Playnite.Platform.Linux"] = ["Avalonia", "Avalonia.Base"],
        ["Playnite.Desktop"] = null,
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // The table's shorthands for the two full project names it abbreviates.
        ["Abstractions"] = "Playnite.SDK.Abstractions",
        ["Contracts"] = "Playnite.LegacyImport.Contracts",
    };

    private static readonly Lazy<IReadOnlyList<ArchitectureRow>> Cached =
        new(() => Parse(File.ReadAllText(Path.Combine(ProductGraph.Root, "docs", "port", "02-ARCHITECTURE.md"))));

    internal static IReadOnlyList<ArchitectureRow> Rows => Cached.Value;

    /// <summary>Product rows only; the table's Tests row is not a product.</summary>
    internal static IReadOnlyList<ArchitectureRow> ProductRows => Rows.Where(row => ProjectNames.Contains(row.Project)).ToArray();

    internal static ArchitectureRow For(string project) => ProductRows.Single(row => row.Project == project);

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

    internal static bool IsPackageToken(string token) => PackageNames.Contains(NormalizeToken(token));

    internal static bool IsApiToken(string token) => ApiNames.Contains(NormalizeToken(token));

    internal static string PackageName(string token) => NormalizeToken(token);

    /// <summary>
    /// A direct project edge is valid only when the table allows it and does not forbid it.
    /// Exposed so a test can prove the cited Core -> Platform and Contracts -> Core edges fail.
    /// </summary>
    internal static string? ProjectViolation(ArchitectureRow row, string dependency)
    {
        if (row.ForbiddenProjects.Contains(dependency))
        {
            return $"{row.Project} -> {dependency} is in the table's Must-not-reference column";
        }

        return row.AllowedProjects.Contains(dependency)
            ? null
            : $"{row.Project} -> {dependency} is not in the table's May-reference column";
    }

    /// <summary>
    /// A direct package is valid only when the table allows it, does not forbid it, and the
    /// Avalonia qualifier for that project admits it.
    /// </summary>
    internal static string? PackageViolation(ArchitectureRow row, string package)
    {
        var forbidden = row.ForbiddenPackages.FirstOrDefault(name => IsFamily(package, name));
        if (forbidden != null)
        {
            return $"{row.Project} -> package {package} is in the table's Must-not-reference column ({forbidden})";
        }

        var qualifiers = row.AllowedPackages.Append(row.Project == "Playnite.Platform" ? "Avalonia" : string.Empty)
            .Where(name => name.Length > 0).ToArray();
        foreach (var allowed in qualifiers)
        {
            if (allowed == "Avalonia" && AvaloniaQualifiers.TryGetValue(row.Project, out var qualified))
            {
                if (qualified == null ? IsFamily(package, "Avalonia") : qualified.Contains(package, StringComparer.Ordinal))
                {
                    return null;
                }
            }
            else if (IsFamily(package, allowed))
            {
                return null;
            }
        }

        return $"{row.Project} -> package {package} is not admitted by the table";
    }

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
            // Classify every token up front; Classify throws on a token outside the closed
            // project/package/API/prose vocabulary, so a typo cannot widen or silently drop a rule.
            var allowances = mayReference.Select(Classify).ToArray();
            _ = mustNotReference.Select(Classify).ToArray();
            if (allowances.Length == 0)
            {
                throw new InvalidOperationException(
                    $"docs/port/02-ARCHITECTURE.md row '{project}' has no reference allowance");
            }

            if (!allowances.Any(kind => kind is TokenKind.Project or TokenKind.Package)
                && !allowances.Any(kind => kind is TokenKind.Nothing))
            {
                throw new InvalidOperationException(
                    $"docs/port/02-ARCHITECTURE.md row '{project}' has no resolvable project or package reference");
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

    internal enum TokenKind
    {
        Project,
        Package,
        Api,
        Nothing,
    }

    internal static TokenKind Classify(string token)
    {
        var name = NormalizeToken(token);
        if (name.StartsWith("nothing", StringComparison.OrdinalIgnoreCase))
        {
            return TokenKind.Nothing;
        }

        if (ResolveProject(name) != null)
        {
            return TokenKind.Project;
        }

        if (PackageNames.Contains(name))
        {
            return TokenKind.Package;
        }

        if (ApiNames.Contains(name))
        {
            return TokenKind.Api;
        }

        throw new InvalidOperationException(
            $"docs/port/02-ARCHITECTURE.md token '{token}' is not a known project, package or API; " +
            "a typo must not turn a prohibition into a silent pass");
    }

    private static string[] SplitTokens(string cell) => cell
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(token => token.Length > 0 && token != "—" && token != "-")
        .ToArray();

    private static bool IsFamily(string name, string prefix) => name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
}
