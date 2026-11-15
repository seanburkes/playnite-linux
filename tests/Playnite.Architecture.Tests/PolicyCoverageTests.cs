using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class PolicyCoverageTests
{
    // An independent floor for the product universe. The architecture document and src/ must
    // both match it, so deleting a product from either place cannot shrink coverage silently.
    private static readonly string[] ExpectedProducts =
    [
        "Playnite.Cli",
        "Playnite.Core",
        "Playnite.Desktop",
        "Playnite.LegacyImport",
        "Playnite.LegacyImport.Contracts",
        "Playnite.Platform",
        "Playnite.Platform.Linux",
        "Playnite.SDK",
        "Playnite.SDK.Abstractions",
        "Playnite.Steam",
    ];

    private static readonly string[] PortingConstructs = ParsePortingConstructs();

    private static readonly Lazy<MetadataReference[]> ProductReferences = new(() => ProductGraph.Products
        .SelectMany(product => product.Values("Reference"))
        .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)).ToArray());

    [Fact]
    public void Every_PORTING_banned_construct_has_exactly_one_owner()
    {
        Assert.NotEmpty(PortingConstructs);
        var problems = BannedConstructInventory.OwnershipProblems(PortingConstructs, BannedConstructInventory.Rows);
        Assert.True(problems.Length == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Ownership_validation_rejects_an_ownerless_inventory_row()
    {
        var ownerless = new BannedConstructRow("X", null, null, false, null, null, null, "condition", "risk");
        var problems = BannedConstructInventory.OwnershipProblems(["X"], [ownerless]);
        Assert.Contains(problems, problem => problem.Contains("no symbol file or source probe owner", StringComparison.Ordinal));
    }

    [Fact]
    public void Ownership_validation_detects_a_removed_PORTING_rule()
    {
        // A rule deleted from PORTING.md leaves its inventory row orphaned; a rule left in
        // PORTING.md without an inventory row also fails.
        var surviving = new BannedConstructRow("Kept", "BannedSymbols.txt", "N:System.Drawing", false,
            "Playnite.Core", "class C { }", "banned Windows", "condition", "risk");
        var problems = BannedConstructInventory.OwnershipProblems(["Kept", "Removed"], [surviving]);
        Assert.Contains(problems, problem => problem.Contains("PORTING row 'Removed' has no inventory owner", StringComparison.Ordinal));
    }

    [Fact]
    public void Ownership_validation_detects_an_orphaned_inventory_row()
    {
        var orphan = new BannedConstructRow("Removed", "BannedSymbols.txt", "N:System.Drawing", false,
            "Playnite.Core", "class C { }", "banned Windows", "condition", "risk");
        var problems = BannedConstructInventory.OwnershipProblems([], [orphan]);
        Assert.Contains(problems, problem => problem.Contains("inventory row 'Removed' has no PORTING entry", StringComparison.Ordinal));
    }

    [Fact]
    public void Ownership_validation_detects_a_duplicate_inventory_row()
    {
        var row = new BannedConstructRow("X", "BannedSymbols.txt", "N:System.Drawing", false,
            "Playnite.Core", "class C { }", "banned Windows", "condition", "risk");
        var problems = BannedConstructInventory.OwnershipProblems(["X"], [row, row]);
        Assert.Contains(problems, problem => problem.Contains("inventory row 'X' appears 2 times", StringComparison.Ordinal));
    }

    [Fact]
    public void Ownership_validation_detects_a_duplicate_PORTING_construct()
    {
        var row = new BannedConstructRow("X", "BannedSymbols.txt", "N:System.Drawing", false,
            "Playnite.Core", "class C { }", "banned Windows", "condition", "risk");
        var problems = BannedConstructInventory.OwnershipProblems(["X", "X"], [row]);
        Assert.Contains(problems, problem => problem.Contains("PORTING construct 'X' appears 2 times", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_inventory_row_states_its_condition_and_residual_risk()
    {
        foreach (var row in BannedConstructInventory.Rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Condition), $"{row.Construct}: missing allowed condition");
            Assert.False(string.IsNullOrWhiteSpace(row.Approximation), $"{row.Construct}: missing approximation/risk note");
        }
    }

    [Fact]
    public void Expected_product_set_is_complete_and_non_shrinkable()
    {
        // The document, the source tree and every policy scope must all still contain the full
        // expected set. Removing a product from two places at once cannot reduce coverage.
        Assert.Equal(ExpectedProducts.Order(), ArchitectureTable.ProjectNames.Order());
        Assert.Equal(ExpectedProducts.Order(), ArchitectureTable.ProductRows.Select(row => row.Project).Order());
        Assert.Equal(ExpectedProducts.Order(), ProductGraph.Products.Select(product => product.Name).Order());
        Assert.Equal(ExpectedProducts.Order(), SourcePolicy.KnownProjects.Order());
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
        // Removing or weakening a SourcePolicy rule leaves its inventory probe passing no
        // longer, so rule removal cannot hide behind the inventory.
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
        // A typo in a BannedSymbols row would silently ban nothing. The two package-only
        // symbols are acknowledged in the inventory.
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
    public void Product_inputs_refuse_an_empty_source_set()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ProductInputs.RequireSources([]));
        Assert.Contains("no source files", exception.Message);
    }

    [Fact]
    public void Product_inputs_refuse_an_executable_without_an_entry_point()
    {
        var compilation = CSharpCompilation.Create("NoMain",
            [CSharpSyntaxTree.ParseText("class C { }")], PolicyProbe.References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductInputs.RequireEntryPoint(compilation, OutputKind.ConsoleApplication));
        Assert.Contains("no entry point", exception.Message);
    }

    [Fact]
    public void Product_inputs_refuse_duplicate_names()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductInputs.RequireUniqueNames(["Playnite.Core", "Playnite.Core"]));
        Assert.Contains("Duplicate product project names", exception.Message);
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
        Assert.Contains("no reference allowance", exception.Message);
    }

    [Theory]
    [InlineData("Cor")]
    [InlineData("Dapper2")]
    [InlineData("System.Windwos")]
    public void Architecture_table_parser_rejects_an_unknown_may_reference_token(string token)
    {
        var markdown = $$"""
            ## 2. Projects and dependency rules

            | Project | TFM | May reference | Must not reference |
            | --- | --- | --- | --- |
            | `Playnite.Core` | `net11.0` | {{token}} | — |
            """;
        var exception = Assert.Throws<InvalidOperationException>(() => ArchitectureTable.Parse(markdown));
        Assert.Contains("not a known project, package or API", exception.Message);
    }

    [Fact]
    public void Architecture_table_parser_rejects_a_misspelled_must_not_token()
    {
        var markdown = """
            ## 2. Projects and dependency rules

            | Project | TFM | May reference | Must not reference |
            | --- | --- | --- | --- |
            | `Playnite.Core` | `net11.0` | `Playnite.SDK.Abstractions` | `Dappr` |
            """;
        var exception = Assert.Throws<InvalidOperationException>(() => ArchitectureTable.Parse(markdown));
        Assert.Contains("not a known project, package or API", exception.Message);
    }

    [Fact]
    public void Must_not_columns_are_enforced_for_the_cited_edges()
    {
        var core = ArchitectureTable.For("Playnite.Core");
        Assert.NotNull(ArchitectureTable.ProjectViolation(core, "Playnite.Platform"));
        Assert.NotNull(ArchitectureTable.PackageViolation(core, "Avalonia"));
        Assert.Contains("Avalonia", core.ForbiddenPackages);
        Assert.Null(ArchitectureTable.ProjectViolation(core, "Playnite.SDK.Abstractions"));
        Assert.Null(ArchitectureTable.PackageViolation(core, "Dapper"));

        var contracts = ArchitectureTable.For("Playnite.LegacyImport.Contracts");
        Assert.NotNull(ArchitectureTable.ProjectViolation(contracts, "Playnite.Core"));
        Assert.NotNull(ArchitectureTable.PackageViolation(contracts, "LiteDB"));
        Assert.Contains("LiteDB", contracts.ForbiddenPackages);
    }

    [Fact]
    public void Must_not_API_tokens_drive_source_gate_probes()
    {
        var probes = new Dictionary<string, (string Source, string Message)>(StringComparer.Ordinal)
        {
            ["System.Windows"] = ("namespace System.Windows.Other { class W { } } class C { System.Windows.Other.W? value; }", "banned Windows"),
            ["System.Drawing"] = ("class C { System.Drawing.Point value; }", "banned Windows"),
            ["Microsoft.Win32"] = ("class C { Microsoft.Win32.RegistryKey? value; }", "banned Windows"),
            ["Win32"] = ("class C { Microsoft.Win32.RegistryKey? value; }", "banned Windows"),
            ["DllImport"] = ("class C { [System.Runtime.InteropServices.DllImport(\"libc\")] static extern int getpid(); }", "native import"),
        };

        var checkedApis = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in ArchitectureTable.ProductRows)
        {
            foreach (var api in row.ForbiddenApis)
            {
                Assert.True(probes.ContainsKey(api), $"must-not API '{api}' has no source-gate probe");
                var (source, message) = probes[api];
                var violations = PolicyProbe.Check(row.Project, source);
                Assert.Contains(violations, v => v.Contains(message, StringComparison.Ordinal));
                checkedApis.Add(api);
            }
        }

        Assert.NotEmpty(checkedApis);
    }

    [Fact]
    public void Avalonia_allowance_respects_the_table_qualifiers()
    {
        // Desktop's row is unqualified and owns views/themes, so the family is intended there.
        Assert.Null(ArchitectureTable.PackageViolation(ArchitectureTable.For("Playnite.Desktop"), "Avalonia.Themes.Fluent"));
        // SDK says "UI contracts only"; Platform/Platform.Linux say base/control services.
        Assert.NotNull(ArchitectureTable.PackageViolation(ArchitectureTable.For("Playnite.SDK"), "Avalonia.Diagnostics"));
        Assert.Null(ArchitectureTable.PackageViolation(ArchitectureTable.For("Playnite.Platform"), "Avalonia.Base"));
        Assert.NotNull(ArchitectureTable.PackageViolation(ArchitectureTable.For("Playnite.Platform.Linux"), "Avalonia.Diagnostics"));
    }

    [Fact]
    public void Gc_symbol_scope_matches_the_policy_allowlist_and_is_not_self_declarable()
    {
        var allowlistFile = Path.Combine(ProductGraph.Root, "docs", "port", "BenchmarkHarnessAllowlist.props");
        var props = XDocument.Load(allowlistFile);
        var property = props.Descendants("PlayniteBenchmarkHarnessProjects").Single().Value;
        var msbuildAllowlist = property.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(SourcePolicy.BenchmarkHarnessProjects.Order(), msbuildAllowlist.Order());

        // The allowlist lives only in the protected policy file; the unprotected root props
        // imports it and must not define a second copy a product could override.
        var rootProps = File.ReadAllText(Path.Combine(ProductGraph.Root, "Directory.Build.props"));
        Assert.Contains("BenchmarkHarnessAllowlist.props", rootProps, StringComparison.Ordinal);
        Assert.DoesNotContain("<PlayniteBenchmarkHarnessProjects", rootProps, StringComparison.Ordinal);

        foreach (var product in ProductGraph.Products)
        {
            Assert.Equal(!SourcePolicy.BenchmarkHarnessProjects.Contains(product.Name),
                product.Values("AdditionalFile").Contains(Path.Combine(ProductGraph.Root, "BannedSymbols.GcCollect.txt")));
        }
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
