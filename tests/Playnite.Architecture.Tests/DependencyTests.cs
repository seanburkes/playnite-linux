using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class DependencyTests
{
    [Fact]
    public void Evaluated_product_references_follow_the_allowed_reference_table()
    {
        var products = ProductGraph.Products.OrderBy(p => p.Name).ToArray();
        var rows = ArchitectureTable.ProductRows;
        // The file tree and the architecture document must describe the same project set.
        Assert.Equal(rows.Select(r => r.Project).Order(), products.Select(p => p.Name).Order());
        foreach (var project in products)
        {
            var row = ArchitectureTable.For(project.Name);
            foreach (var dependency in project.Values("ProjectReference"))
            {
                var violation = ArchitectureTable.ProjectViolation(row, dependency);
                Assert.True(violation == null, violation);
            }

            foreach (var banned in row.ForbiddenProjects)
            {
                Assert.DoesNotContain(banned, project.Values("ProjectReference"));
            }

            foreach (var package in project.DirectPackages().Where(p => p != "Microsoft.CodeAnalysis.BannedApiAnalyzers"))
            {
                var violation = ArchitectureTable.PackageViolation(row, package);
                if (project.Name == "Playnite.LegacyImport" && IsFamily(package, "LiteDB"))
                {
                    // The migration reader must stay on the frozen v10 file-format generation.
                    Assert.True(LegacyLiteDbVersion(project), $"{project.Name} -> package {package} must be LiteDB 4.1.4");
                    continue;
                }

                Assert.True(violation == null, violation);
            }

            // A "Must not reference" package applies to the resolved closure too, not only to a
            // direct reference. Avalonia in Core and LiteDB in Contracts/Desktop/Cli are the
            // rows the table names.
            foreach (var forbidden in row.ForbiddenPackages)
            {
                Assert.DoesNotContain(project.Dependencies(), d => IsFamily(d, forbidden));
            }
        }
    }

    [Fact]
    public void Core_and_abstractions_have_no_direct_or_transitive_Avalonia_dependency()
    {
        foreach (var project in ProductGraph.Products.Where(p => p.Name is "Playnite.Core" or "Playnite.SDK.Abstractions"))
        {
            Assert.DoesNotContain(project.Dependencies(), d => IsFamily(d, "Avalonia"));
        }
    }

    [Fact]
    public void Product_dependency_closures_exclude_Windows_and_importer_leaks()
    {
        foreach (var project in ProductGraph.Products)
        {
            var dependencies = project.Dependencies();
            Assert.DoesNotContain(dependencies, d => IsFamily(d, "Microsoft.Win32") || IsFamily(d, "System.Drawing.Common") || IsFamily(d, "System.Management") || IsFamily(d, "System.ServiceProcess.ServiceController"));
            if (project.Name is "Playnite.Desktop" or "Playnite.Cli")
            {
                Assert.DoesNotContain(dependencies, d => IsFamily(d, "LiteDB") || d == "Playnite.LegacyImport");
            }

            if (project.Name is "Playnite.LegacyImport" or "Playnite.LegacyImport.Contracts")
            {
                Assert.DoesNotContain(dependencies, d => d is "Playnite.Core" or "Playnite.Desktop" or "Playnite.SDK" or "Playnite.SDK.Abstractions");
            }
        }
    }

    [Fact]
    public void Compiled_product_types_have_no_banned_namespace_dependencies()
    {
        foreach (var project in ProductGraph.Products)
        {
            var assembly = Assembly.LoadFrom(project.Values("Output").Single());
            // NetArchTest passes any rule over an empty type set; an empty product
            // assembly would silently switch this gate off.
            Assert.NotEmpty(assembly.GetTypes());
            foreach (var dependency in new[] { "System.Drawing", "Microsoft.Win32", "System.Management", "System.ServiceProcess.ServiceController" })
            {
                var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(dependency).GetResult();
                Assert.True(result.IsSuccessful, $"{project.Name}: compiled dependency on {dependency}");
            }
        }
    }

    [Fact]
    public void Banned_API_scans_cover_products_and_exclude_the_architecture_runner()
    {
        foreach (var product in ProductGraph.Products)
        {
            Assert.Equal("true", product.Values("Product").Single());
            Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", product.DirectPackages());
            Assert.Contains(product.Values("Analyzer"), a => a.Contains("BannedApiAnalyzers", StringComparison.Ordinal));
            var files = product.Values("AdditionalFile");
            Assert.Contains(Path.Combine(ProductGraph.Root, "BannedSymbols.txt"), files);
            Assert.Equal(product.Name != "Playnite.Cli", files.Contains(Path.Combine(ProductGraph.Root, "BannedSymbols.Blocking.txt")));
            Assert.Equal(!SourcePolicy.BenchmarkHarnessProjects.Contains(product.Name), files.Contains(Path.Combine(ProductGraph.Root, "BannedSymbols.GcCollect.txt")));
            Assert.Equal(product.Name != "Playnite.Platform.Linux", files.Contains(Path.Combine(ProductGraph.Root, "BannedSymbols.Interop.txt")));
            Assert.Equal(product.Name is "Playnite.Core" or "Playnite.SDK.Abstractions" or "Playnite.Steam",
                files.Contains(Path.Combine(ProductGraph.Root, "BannedSymbols.Logic.txt")));
            Assert.Equal(product.Name == "Playnite.Core", files.Contains(Path.Combine(ProductGraph.Root, "BannedSymbols.Core.txt")));
            Assert.Equal(product.Name is not ("Playnite.Cli" or "Playnite.Desktop" or "Playnite.LegacyImport"),
                files.Contains(Path.Combine(ProductGraph.Root, "BannedSymbols.Library.txt")));
        }

        var runner = new ProductGraph(Path.Combine(ProductGraph.Root, "tests", "Playnite.Architecture.Tests", "Playnite.Architecture.Tests.csproj"));
        Assert.DoesNotContain("true", runner.Values("Product"));
        Assert.DoesNotContain("Microsoft.CodeAnalysis.BannedApiAnalyzers", runner.DirectPackages());
        Assert.DoesNotContain(runner.Values("Analyzer"), a => a.Contains("BannedApiAnalyzers", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Values("AdditionalFile"), file => Path.GetFileName(file).StartsWith("BannedSymbols", StringComparison.Ordinal));
        Assert.Contains("Microsoft.Win32.Registry", runner.Dependencies());
        Assert.Contains("System.Security.AccessControl", runner.Dependencies());
        Assert.DoesNotContain(ProductGraph.Products, p => p.Name == runner.Name);
    }

    private static bool IsFamily(string name, string prefix) => name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);

    private static bool LegacyLiteDbVersion(ProductGraph project)
    {
        // The migration reader must stay on the frozen v10 file-format generation.
        using var assets = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(project.ProjectFile)!, "obj", "project.assets.json")));
        return assets.RootElement.GetProperty("libraries").TryGetProperty("LiteDB/4.1.4", out _);
    }
}
