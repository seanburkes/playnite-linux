using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class ProductSourceTests
{
    [Fact]
    public void Product_compiler_inputs_obey_the_banned_construct_table()
    {
        var failures = new List<string>();
        foreach (var project in ProductGraph.Products)
        {
            // A project with no sources would contribute no violations and silently switch
            // every source rule off for that product.
            Assert.NotEmpty(project.Values("Source"));
            var options = new CSharpParseOptions(LanguageVersion.Preview,
                preprocessorSymbols: project.Values("Define").Single().Split(','));
            var trees = project.Values("Source").Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path));
            var outputKind = project.Values("OutputType").Single() is "Exe" or "WinExe"
                ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary;
            var compilation = CSharpCompilation.Create(project.Name, trees,
                project.Values("Reference").Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(outputKind, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
            // Fail closed: semantic checks on an unresolved program could silently miss aliases/types.
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            // The Cli exception is keyed on the real entry point; without one it could never apply.
            if (outputKind == OutputKind.ConsoleApplication)
            {
                Assert.NotNull(compilation.GetEntryPoint(default));
            }

            failures.AddRange(SourcePolicy.Violations(project.Name, compilation, project.InBenchmarkHarness));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Distinct()));
    }
}
