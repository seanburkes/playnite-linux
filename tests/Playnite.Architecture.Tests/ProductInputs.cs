using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Playnite.Architecture.Tests;

/// <summary>
/// Guards that keep the source gate from passing on an empty or incomplete compilation.
/// Each guard is a separate method so a test can prove the failure path.
/// </summary>
internal static class ProductInputs
{
    internal static void RequireSources(IReadOnlyCollection<string> sources)
    {
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                "Product project has no source files; source rules would pass vacuously");
        }
    }

    internal static IMethodSymbol RequireEntryPoint(CSharpCompilation compilation, OutputKind outputKind)
    {
        var entryPoint = compilation.GetEntryPoint(default);
        if (outputKind == OutputKind.ConsoleApplication && entryPoint == null)
        {
            throw new InvalidOperationException(
                $"{compilation.AssemblyName} is an executable but has no entry point; the Cli comment exception could never apply");
        }

        return entryPoint!;
    }

    internal static void RequireUniqueNames(IEnumerable<string> names)
    {
        var duplicated = names.GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicated.Length > 0)
        {
            throw new InvalidOperationException($"Duplicate product project names: {string.Join(", ", duplicated)}");
        }
    }
}
