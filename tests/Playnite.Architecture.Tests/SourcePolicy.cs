using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Playnite.Architecture.Tests;

internal static class SourcePolicy
{
    // A renamed or added product must be classified here explicitly. Otherwise a rule that
    // matches by project name would silently stop covering it and the suite would pass while
    // the gate was narrower than PORTING.md.
    internal static readonly string[] KnownProjects =
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

    internal static readonly string[] HostProjects = ["Playnite.Desktop", "Playnite.Cli", "Playnite.LegacyImport"];
    internal static readonly string[] LogicProjects = ["Playnite.Core", "Playnite.SDK.Abstractions", "Playnite.Steam"];
    internal static readonly string[] UiProjects = ["Playnite.Desktop", "Playnite.SDK"];

    // PORTING allows GC.Collect only in a benchmark/soak harness with a comment. The harness
    // projects come from the protected docs/port/BenchmarkHarnessAllowlist.props file, so
    // widening the exception needs the oracle-change label and a second reviewer. No project
    // can declare itself a harness, and the comment is still required by the source rule.
    internal static readonly string[] BenchmarkHarnessProjects = LoadBenchmarkHarnesses();

    internal static IEnumerable<string> Violations(string project, CSharpCompilation compilation,
        IReadOnlyCollection<string>? benchmarkHarnessProjects = null)
    {
        var benchmarkHarnesses = benchmarkHarnessProjects ?? BenchmarkHarnessProjects;
        if (!KnownProjects.Contains(project) && !benchmarkHarnesses.Contains(project))
        {
            throw new InvalidOperationException(
                $"Project '{project}' has no source-policy classification; add it to SourcePolicy.KnownProjects and the PORTING.md owner inventory");
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                string? rule = null;
                if (node is CatchClauseSyntax clause && IsEmpty(clause.Block))
                {
                    rule = "empty catch hides failures";
                }
                else if (node is MethodDeclarationSyntax method && model.GetDeclaredSymbol(method) is { IsAsync: true, ReturnsVoid: true } declared
                    && !IsUiHandler(project, declared))
                {
                    rule = "async void is only allowed for UI event handler signatures";
                }
                else if (node is LocalFunctionStatementSyntax local && model.GetDeclaredSymbol(local) is { IsAsync: true, ReturnsVoid: true })
                {
                    rule = "async void local function is not a UI handler";
                }
                else if (node is AnonymousFunctionExpressionSyntax lambda && model.GetSymbolInfo(lambda).Symbol is IMethodSymbol { IsAsync: true, ReturnsVoid: true } anonymous
                    && !IsUiHandler(project, anonymous))
                {
                    rule = "async void lambda is only allowed for UI event handler signatures";
                }
                else if (node is ExpressionSyntax or AttributeSyntax)
                {
                    var symbol = model.GetSymbolInfo(node).Symbol;
                    if (symbol is IAliasSymbol alias)
                    {
                        symbol = alias.Target;
                    }

                    rule = SymbolViolation(project, symbol, node, model, benchmarkHarnesses.Contains(project));
                }

                if (rule != null)
                {
                    yield return $"{project}: {tree.FilePath}:{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}: {rule}";
                }
            }
        }
    }

    private static string? SymbolViolation(string project, ISymbol? symbol, SyntaxNode node, SemanticModel model, bool inBenchmarkHarness)
    {
        var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
        if (type == null)
        {
            return null;
        }

        var name = type.OriginalDefinition.ToDisplayString();
        var ns = type.ContainingNamespace.ToDisplayString();
        if (InNamespace(ns, "System.Windows") && name != "System.Windows.Input.ICommand"
            || InNamespace(ns, "System.Drawing") || InNamespace(ns, "Microsoft.Win32") || InNamespace(ns, "System.Management")
            || name is "System.ServiceProcess.ServiceController" or "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter")
        {
            return $"banned Windows/serialization API {type}";
        }

        if (project == "Playnite.SDK.Abstractions" && SdkBoundaryPolicy.UiSymbolViolation(type) is { } boundaryViolation)
        {
            return boundaryViolation;
        }

        if (name is "System.Runtime.InteropServices.DllImportAttribute" or "System.Runtime.InteropServices.LibraryImportAttribute")
        {
            // PORTING allows native imports only in Platform.Linux with a reviewed allow-list.
            // No entry has been reviewed yet, so even Linux rejects them until its unit adds one.
            return project == "Playnite.Platform.Linux"
                ? "native import needs a reviewed Linux allow-list (currently empty)"
                : "native import outside Playnite.Platform.Linux";
        }

        if (symbol is IMethodSymbol method)
        {
            if (name == "System.GC" && method.Name == "Collect")
            {
                // The table's exception is a benchmark/soak harness with a comment. The harness
                // must be named in the reviewed policy allowlist (no project can self-declare);
                // the comment is still required here, so an allowlist entry alone permits nothing.
                return inBenchmarkHarness && IsCommented(node)
                    ? null
                    : "banned call System.GC.Collect outside a commented policy-allowlisted benchmark/soak harness";
            }

            if (name == "System.Threading.Thread" && method.Name == "Abort"
                || name == "System.AppDomain" && method.Name == "CreateDomain")
            {
                return $"banned call {type}.{method.Name}";
            }

            if (name == "System.Environment" && method.Name == "Exit" && !IsHost(project))
            {
                return "Environment.Exit in library code";
            }

            if (name == "System.Diagnostics.Process" && method.Name == "Start" && method.Parameters.Length > 0
                && !(method.Parameters.Length == 2 && method.Parameters[1].Type.SpecialType != SpecialType.System_String)
                && method.Parameters[0].Type.SpecialType == SpecialType.System_String
                || name == "System.Diagnostics.ProcessStartInfo" && method.MethodKind == MethodKind.Constructor
                && method.Parameters.Length == 2 && method.Parameters[1].Type.SpecialType == SpecialType.System_String)
            {
                return "shell-string process launch; use ProcessStartInfo.ArgumentList";
            }

            if (IsLogic(project) && (name == "System.Guid" && method.Name == "NewGuid"
                || name == "System.Random" && method.MethodKind == MethodKind.Constructor && method.Parameters.Length == 0))
            {
                return "ambient randomness in logic; inject an ID or seeded Random";
            }

            if (InNamespace(ns, "System.Threading.Tasks") && method.Name is "Wait" or "WaitAll" or "WaitAny" && !IsCommentedCliEntry(project, node, model))
            {
                return "blocking task wait outside commented top-level CLI";
            }
        }

        if (symbol is IPropertySymbol property)
        {
            if (project == "Playnite.Core" && name == "System.DateTime" && property.Name == "Now")
            {
                return "DateTime.Now in Core; inject IClock/TimeProvider";
            }

            if (IsLogic(project) && name == "System.Random" && property.Name == "Shared")
            {
                return "unseeded shared Random in logic";
            }

            if (InNamespace(ns, "System.Threading.Tasks") && property.Name == "Result" && !IsCommentedCliEntry(project, node, model))
            {
                return "blocking task Result outside commented top-level CLI";
            }

            if (name == "System.Diagnostics.ProcessStartInfo" && property.Name == "Arguments")
            {
                return "process argument string; use ArgumentList";
            }

            if (name == "System.Diagnostics.ProcessStartInfo" && property.Name == "UseShellExecute"
                && node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node
                && model.GetConstantValue(assignment.Right) is not { HasValue: true, Value: false })
            {
                return "UseShellExecute must be explicitly false";
            }
        }

        return null;
    }

    private static bool IsEmpty(BlockSyntax block) => block.Statements.All(s => s is EmptyStatementSyntax || s is BlockSyntax nested && IsEmpty(nested));

    private static bool InNamespace(string name, string prefix) => name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal);
    private static bool IsHost(string project) => HostProjects.Contains(project);
    private static bool IsLogic(string project) => LogicProjects.Contains(project);

    private static bool IsUiHandler(string project, IMethodSymbol method)
    {
        // Signature-level approximation: PORTING says "UI event handlers only", but Roslyn
        // source analysis cannot prove that a method is registered with an event. The
        // architectural project scope is the only boundary available, so Desktop/SDK methods
        // and lambdas with the conventional (object sender, EventArgs-derived) shape are
        // permitted; registration and failure logging stay a review obligation.
        if (!UiProjects.Contains(project) || method.Parameters.Length != 2
            || method.Parameters[0].Type.SpecialType != SpecialType.System_Object)
        {
            return false;
        }

        for (var type = method.Parameters[1].Type as INamedTypeSymbol; type != null; type = type.BaseType)
        {
            if (type.ToDisplayString() == "System.EventArgs")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCommentedCliEntry(string project, SyntaxNode node, SemanticModel model)
    {
        if (project != "Playnite.Cli" || model.GetEnclosingSymbol(node.SpanStart) is not IMethodSymbol method
            || !SymbolEqualityComparer.Default.Equals(method, model.Compilation.GetEntryPoint(default)))
        {
            return false;
        }

        return IsCommented(node);
    }

    private static bool IsCommented(SyntaxNode node)
    {
        var statement = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        return statement != null && statement.GetLeadingTrivia().Concat(statement.GetTrailingTrivia())
            .Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));
    }

    private static string[] LoadBenchmarkHarnesses()
    {
        var path = Path.Combine(ProductGraph.Root, "docs", "port", "BenchmarkHarnessAllowlist.props");
        var document = XDocument.Load(path);
        var property = document.Descendants("PlayniteBenchmarkHarnessProjects").SingleOrDefault()
            ?? throw new InvalidOperationException($"{path} has no PlayniteBenchmarkHarnessProjects property");
        return property.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
    }
}
