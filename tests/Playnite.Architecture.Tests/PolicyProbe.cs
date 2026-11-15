using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Playnite.Architecture.Tests;

/// <summary>Builds a small in-memory compilation and runs the source policy over it.</summary>
internal static class PolicyProbe
{
    internal static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    internal static string[] Check(string project, string source, IReadOnlyCollection<string>? benchmarkHarnesses = null)
    {
        var compilation = CSharpCompilation.Create("PolicyProbe",
            [CSharpSyntaxTree.ParseText(source)], References,
            new CSharpCompilationOptions(project == "Playnite.Cli" ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary));
        // Obsolete-but-banned APIs can produce compiler errors in net11; allow those
        // diagnostics only when Roslyn still resolves the symbol for policy inspection.
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error && d.Id is not ("CS0619" or "SYSLIB0006" or "SYSLIB0024" or "SYSLIB0050"));
        return SourcePolicy.Violations(project, compilation, benchmarkHarnesses).ToArray();
    }
}
