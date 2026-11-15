using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class PolicyCoverageTests
{
    private static readonly string[] PortingConstructs = ParsePortingConstructs();

    private static readonly Lazy<MetadataReference[]> ProductReferences = new(() => ProductGraph.Products
        .SelectMany(product => product.Values("Reference"))
        .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)).ToArray());

    [Fact]
    public void Every_PORTING_banned_construct_has_exactly_one_inventory_owner()
    {
        Assert.NotEmpty(PortingConstructs);
        Assert.Equal(PortingConstructs.Order(), BannedConstructInventory.Rows.Select(r => r.Construct).Order());
    }

    [Fact]
    public void Every_inventory_row_states_its_condition_and_residual_risk()
    {
        foreach (var row in BannedConstructInventory.Rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Condition), $"{row.Construct}: missing allowed condition");
            Assert.False(string.IsNullOrWhiteSpace(row.Approximation), $"{row.Construct}: missing approximation/risk note");
            if (row.SymbolFile != null)
            {
                Assert.False(string.IsNullOrWhiteSpace(row.SymbolId), $"{row.Construct}: symbol owner needs a representative row");
            }

            if (row.ProbeSource != null)
            {
                Assert.False(string.IsNullOrWhiteSpace(row.ProbeProject), $"{row.Construct}: source probe needs a project");
                Assert.False(string.IsNullOrWhiteSpace(row.ProbeMessage), $"{row.Construct}: source probe needs an expected message");
            }
        }
    }

    [Fact]
    public void Every_symbol_owner_row_exists_and_resolves_or_is_an_acknowledged_absence()
    {
        var compilation = CSharpCompilation.Create("BannedSymbols", references: ProductReferences.Value);
        foreach (var row in BannedConstructInventory.Rows.Where(r => r.SymbolFile != null))
        {
            var file = Path.Combine(ProductGraph.Root, row.SymbolFile!);
            var ids = BannedSymbolFiles.Rows(file);
            Assert.Contains(row.SymbolId, ids);

            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(row.SymbolId!, compilation);
            if (row.SymbolExpectedUnresolved)
            {
                Assert.Null(symbol);
            }
            else
            {
                Assert.True(symbol != null, $"{row.Construct}: {row.SymbolId} does not resolve; the symbol row bans nothing");
            }
        }
    }

    [Fact]
    public void Every_source_owner_row_has_a_working_failing_probe()
    {
        foreach (var row in BannedConstructInventory.Rows.Where(r => r.ProbeSource != null))
        {
            var violations = PolicyProbe.Check(row.ProbeProject!, row.ProbeSource!);
            Assert.True(violations.Any(v => v.Contains(row.ProbeMessage!, StringComparison.Ordinal)),
                $"{row.Construct}: probe did not produce '{row.ProbeMessage}'. Got: {string.Join(" | ", violations)}");
        }
    }

    [Fact]
    public void Every_Porting_symbol_row_resolves_against_the_reference_set()
    {
        // A typo in a BannedSymbols row would silently ban nothing. The one package-only
        // namespace is acknowledged in the inventory.
        var compilation = CSharpCompilation.Create("BannedSymbols", references: ProductReferences.Value);
        var unresolved = BannedSymbolFiles.All(ProductGraph.Root)
            .SelectMany(file => BannedSymbolFiles.Rows(file))
            .Where(id => DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) == null)
            .Order()
            .ToArray();
        Assert.True(unresolved.SequenceEqual(new[] { "N:System.Management", "T:System.ServiceProcess.ServiceController" }),
            $"unresolved banned rows: {string.Join(", ", unresolved)}");
    }

    [Fact]
    public void Source_policy_refuses_an_unclassified_project_instead_of_silently_narrowing()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SourcePolicy.Violations("Playnite.Renamed", CSharpCompilation.Create("Probe")).ToArray());
        Assert.Contains("no source-policy classification", exception.Message);
    }

    [Fact]
    public void Product_graph_refuses_an_empty_product_set_instead_of_passing_vacuously()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ProductGraph.RequireProducts([]));
        Assert.Contains("pass vacuously", exception.Message);
    }

    [Fact]
    public void Architecture_table_cross_checks_the_real_project_set()
    {
        var rows = ArchitectureTable.ProductRows.Select(r => r.Project).Order().ToArray();
        Assert.Equal(ProductGraph.Products.Select(p => p.Name).Order(), rows);
    }

    [Fact]
    public void Architecture_table_parser_rejects_an_empty_table()
    {
        Assert.Throws<InvalidOperationException>(() => ArchitectureTable.Parse("# nothing here"));
    }

    [Fact]
    public void Architecture_table_parser_rejects_an_unknown_project_row()
    {
        var markdown = """
            ## 2. Projects and dependency rules

            | Project | TFM | May reference | Must not reference |
            | --- | --- | --- | --- |
            | `Playnite.Unknown` | `net11.0` | nothing | — |
            """;
        var exception = Assert.Throws<InvalidOperationException>(() => ArchitectureTable.Parse(markdown));
        Assert.Contains("unknown project", exception.Message);
    }

    [Fact]
    public void Architecture_table_parser_rejects_a_row_without_a_reference_allowance()
    {
        var markdown = """
            ## 2. Projects and dependency rules

            | Project | TFM | May reference | Must not reference |
            | --- | --- | --- | --- |
            | `Playnite.Core` | `net11.0` |  | — |
            """;
        var exception = Assert.Throws<InvalidOperationException>(() => ArchitectureTable.Parse(markdown));
        Assert.Contains("no resolvable project reference", exception.Message);
    }

    private static string[] ParsePortingConstructs()
    {
        var lines = File.ReadAllLines(Path.Combine(ProductGraph.Root, "docs", "port", "PORTING.md"));
        var start = Array.FindIndex(lines, line => line.Trim() == "## Banned constructs");
        if (start < 0)
        {
            throw new InvalidOperationException("docs/port/PORTING.md 'Banned constructs' section was not found");
        }

        var constructs = new List<string>();
        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (!line.StartsWith('|'))
            {
                continue;
            }

            var cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length < 2 || cells[0] == "Banned" || cells[0].All(c => c is '-' or ':' or ' '))
            {
                continue;
            }

            constructs.Add(cells[0].Replace("`", string.Empty, StringComparison.Ordinal));
        }

        return constructs.ToArray();
    }
}

internal static class BannedSymbolFiles
{
    internal static IEnumerable<string> All(string root) => Directory.EnumerateFiles(root, "BannedSymbols*.txt", SearchOption.TopDirectoryOnly);

    internal static string[] Rows(string path) => File.ReadAllLines(path)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
        .Select(line => line.Split(';')[0].Trim())
        .ToArray();
}
