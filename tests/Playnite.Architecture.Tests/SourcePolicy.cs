using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Playnite.Architecture.Tests;

internal static class SourcePolicy
{
    internal static IEnumerable<string> Violations(string project, CSharpCompilation compilation)
    {
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

                    rule = SymbolViolation(project, symbol, node, model);
                }

                if (rule != null)
                {
                    yield return $"{project}: {tree.FilePath}:{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}: {rule}";
                }
            }
        }
    }

    private static string? SymbolViolation(string project, ISymbol? symbol, SyntaxNode node, SemanticModel model)
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

        if (name is "System.Runtime.InteropServices.DllImportAttribute" or "System.Runtime.InteropServices.LibraryImportAttribute")
        {
            // No native imports are approved yet. A reviewed Linux allow-list belongs to its porting unit.
            return project == "Playnite.Platform.Linux"
                ? "native import needs a reviewed Linux allow-list (currently empty)"
                : "native import outside Playnite.Platform.Linux";
        }

        if (symbol is IMethodSymbol method)
        {
            if (name == "System.GC" && method.Name == "Collect"
                || name == "System.Threading.Thread" && method.Name == "Abort"
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
    private static bool IsHost(string project) => project is "Playnite.Desktop" or "Playnite.Cli" or "Playnite.LegacyImport";
    private static bool IsLogic(string project) => project is "Playnite.Core" or "Playnite.SDK.Abstractions" or "Playnite.Steam";

    private static bool IsUiHandler(string project, IMethodSymbol method)
    {
        if (project is not ("Playnite.Desktop" or "Playnite.SDK") || method.Parameters.Length != 2
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

        var statement = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        return statement != null && statement.GetLeadingTrivia().Concat(statement.GetTrailingTrivia())
            .Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));
    }
}
