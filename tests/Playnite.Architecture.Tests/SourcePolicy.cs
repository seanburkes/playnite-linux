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

    // PORTING allows native imports only in Platform.Linux with a reviewed allow-list. The
    // list covers handle-bound libc file reads, PID-reuse-free signaling and SDL3 gamepads.
    // A declaration naming any other entry point, in another
    // project, or whose entry point cannot be read from source is rejected. Adding a symbol
    // is a reviewed policy change.
    internal static readonly string[] LinuxNativeImportAllowlist =
        ["close", "openat", "read", "statx", "pidfd_open", "pidfd_send_signal",
            "SDL_InitSubSystem", "SDL_QuitSubSystem", "SDL_PollEvent", "SDL_OpenGamepad", "SDL_CloseGamepad"];

    // pidfd_send_signal can deliver any signal, so the entry-point allow-list alone would
    // review the syscall but not its capability. The reviewed capability is exactly SIGTERM
    // against a pidfd the same code opened: every reference must be a direct call whose
    // signal argument is the compile-time constant 15, and a forwarded or runtime signal is
    // unprovable and rejected rather than assumed safe.
    internal const string PidfdSendSignalEntryPoint = "pidfd_send_signal";
    internal const long SigTerm = 15;

    // PORTING allows GC.Collect only in a benchmark/soak harness with a comment. The harness
    // projects come from the protected docs/port/BenchmarkHarnessAllowlist.props file, so
    // widening the exception needs the oracle-change label and the protected-path gate; review
    // is adversarial on the owner's behalf. No project can declare itself a harness, and the
    // comment is still required by the source rule.
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
            // The reviewed entry points are the only thing this permits; the declaration is
            // rejected when its entry point is not on the list or cannot be proven from source.
            if (project != "Playnite.Platform.Linux")
            {
                return "native import outside Playnite.Platform.Linux";
            }

            // A named argument (EntryPoint) or a bare type reference resolves inside the
            // attribute but declares no import; only the attribute constructor is a native
            // declaration to judge.
            if (symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor })
            {
                return null;
            }

            var entryPoint = NativeImportEntryPoint(node, model);
            return entryPoint != null && LinuxNativeImportAllowlist.Contains(entryPoint, StringComparer.Ordinal)
                ? null
                : $"native import needs a reviewed Linux allow-list entry; '{entryPoint ?? "unknown entry point"}' is not allowed";
        }

        if (symbol is IMethodSymbol method)
        {
            if (ImportedEntryPoint(method) == PidfdSendSignalEntryPoint)
            {
                return PidfdSendSignalViolation(node, method, model);
            }

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

    /// <summary>
    /// Returns the native entry point the declaration actually binds, or null when source
    /// cannot prove it. An explicit EntryPoint wins; otherwise the method name is the entry
    /// point, matching the interop marshaller. A non-constant EntryPoint is unprovable and
    /// therefore rejected by the caller.
    /// </summary>
    private static string? NativeImportEntryPoint(SyntaxNode node, SemanticModel model)
    {
        // The attribute name is itself an ExpressionSyntax whose symbol can resolve to the
        // constructor, so resolve every hit back to the declaration it belongs to.
        var attribute = node as AttributeSyntax
            ?? node.Ancestors().OfType<AttributeSyntax>().FirstOrDefault();
        if (attribute?.ArgumentList == null
            || attribute.Parent is not AttributeListSyntax { Parent: MethodDeclarationSyntax method })
        {
            return null;
        }

        foreach (var argument in attribute.ArgumentList.Arguments)
        {
            if (argument.NameEquals?.Name.Identifier.ValueText != "EntryPoint")
            {
                continue;
            }

            return model.GetConstantValue(argument.Expression) is { HasValue: true, Value: string value }
                ? value
                : null;
        }

        return method.Identifier.ValueText;
    }

    /// <summary>
    /// Returns the entry point an imported method binds, matching the declaration rule: an
    /// explicit EntryPoint wins, otherwise the method name. Null when the method is not a
    /// native import at all.
    /// </summary>
    private static string? ImportedEntryPoint(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            var attributeName = attribute.AttributeClass?.ToDisplayString();
            if (attributeName is not ("System.Runtime.InteropServices.DllImportAttribute"
                or "System.Runtime.InteropServices.LibraryImportAttribute"))
            {
                continue;
            }

            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key == "EntryPoint")
                {
                    return named.Value.Value as string;
                }
            }

            return method.Name;
        }

        return null;
    }

    /// <summary>
    /// Enforces the SIGTERM-only capability of the reviewed pidfd_send_signal import. The
    /// only accepted reference is a direct call whose signal argument is a compile-time
    /// SIGTERM; a method group, function pointer, forwarded or runtime signal is rejected
    /// because the delivered signal would no longer be provable from source.
    /// </summary>
    private static string? PidfdSendSignalViolation(SyntaxNode node, IMethodSymbol method, SemanticModel model)
    {
        if (node is not InvocationExpressionSyntax invocation)
        {
            var enclosing = node.Ancestors().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(candidate => candidate.Expression.Span.Contains(node.Span));
            return enclosing != null && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(enclosing).Symbol, method)
                ? null
                : "pidfd_send_signal may only be called directly with SIGTERM; a method group or function pointer could send any signal";
        }

        ArgumentSyntax? signalArgument = null;
        for (var index = 0; index < invocation.ArgumentList.Arguments.Count; index++)
        {
            var argument = invocation.ArgumentList.Arguments[index];
            var parameter = argument.NameColon is { } named
                ? method.Parameters.FirstOrDefault(candidate => candidate.Name == named.Name.Identifier.ValueText)
                : index < method.Parameters.Length ? method.Parameters[index] : null;
            if (parameter?.Ordinal == 1)
            {
                signalArgument = argument;
                break;
            }
        }

        if (signalArgument == null
            || model.GetConstantValue(signalArgument.Expression) is not { HasValue: true, Value: IConvertible constant })
        {
            return "pidfd_send_signal signal must be the compile-time constant SIGTERM (15); a forwarded or runtime signal is not provable";
        }

        var signal = constant.ToInt64(System.Globalization.CultureInfo.InvariantCulture);
        return signal == SigTerm
            ? null
            : $"pidfd_send_signal is restricted to SIGTERM (15); signal {signal} is not allowed";
    }

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
