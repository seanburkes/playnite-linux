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
            var options = new CSharpParseOptions(LanguageVersion.Preview,
                preprocessorSymbols: project.Values("Define").Single().Split(','));
            var trees = project.Values("Source").Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path));
            var compilation = CSharpCompilation.Create(project.Name, trees,
                project.Values("Reference").Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(project.Values("OutputType").Single() is "Exe" or "WinExe"
                    ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
            // Fail closed: semantic checks on an unresolved program could silently miss aliases/types.
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            failures.AddRange(SourcePolicy.Violations(project.Name, compilation));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Distinct()));
    }
}
