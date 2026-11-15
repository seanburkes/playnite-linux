using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class SourcePolicyTests
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    [Theory]
    [InlineData("Playnite.Core", "System.DateTime.Now;", "DateTime.Now")]
    [InlineData("Playnite.Core", "System.Guid.NewGuid();", "ambient randomness")]
    [InlineData("Playnite.Steam", "new System.Random();", "ambient randomness")]
    [InlineData("Playnite.Core", "System.Random.Shared;", "unseeded shared")]
    [InlineData("Playnite.Platform", "System.Environment.Exit(0);", "library code")]
    [InlineData("Playnite.Core", "System.GC.Collect(1, System.GCCollectionMode.Forced);", "banned call")]
    [InlineData("Playnite.Core", "System.AppDomain.CreateDomain(\"x\");", "banned call")]
    [InlineData("Playnite.Core", "System.Threading.Thread.CurrentThread.Abort();", "banned call")]
    [InlineData("Playnite.Core", "System.Diagnostics.Process.Start(\"sh\", \"-c command\");", "shell-string")]
    [InlineData("Playnite.Core", "new System.Diagnostics.ProcessStartInfo(\"sh\", \"-c command\");", "shell-string")]
    [InlineData("Playnite.Core", "new System.Diagnostics.ProcessStartInfo { Arguments = \"-c command\" };", "argument string")]
    [InlineData("Playnite.Core", "new System.Diagnostics.ProcessStartInfo { UseShellExecute = true };", "UseShellExecute")]
    [InlineData("Playnite.Core", "System.Threading.Tasks.Task.CompletedTask.Wait();", "blocking task wait")]
    [InlineData("Playnite.Core", "System.Threading.Tasks.Task.FromResult(1).Result;", "blocking task Result")]
    [InlineData("Playnite.Core", "new System.Threading.Tasks.ValueTask<int>(1).Result;", "blocking task Result")]
    public void Rejects_banned_semantic_calls(string project, string expression, string expected)
    {
        var source = "class C { void M() { _ = " + expression.TrimEnd(';') + "; } }";
        // Void expressions are valid statements rather than assignments.
        if (expression.Contains(".Wait(") || expression.Contains(".Collect(") || expression.Contains(".Exit(") || expression.Contains(".Abort("))
        {
            source = "class C { void M() { " + expression + " } }";
        }

        Assert.Contains(Check(project, source), v => v.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("class C { void M() { try { } catch { } } }", "empty catch")]
    [InlineData("class C { void M() { try { } catch (System.Exception) when (true) { ; { } } } }", "empty catch")]
    [InlineData("class C { async void M() { await System.Threading.Tasks.Task.Yield(); } }", "async void")]
    [InlineData("class C { void M() { async void Local() { await System.Threading.Tasks.Task.Yield(); } } }", "async void")]
    [InlineData("class C { void M() { System.Action a = async () => { await System.Threading.Tasks.Task.Yield(); }; } }", "async void")]
    [InlineData("using Clock = System.DateTime; class C { object M() => Clock.Now; }", "DateTime.Now")]
    [InlineData("class C { [System.Runtime.InteropServices.DllImport(\"libc\")] static extern int getpid(); }", "native import")]
    [InlineData("namespace System.Windows.Other { class Widget { } } class C { System.Windows.Other.Widget? value; }", "banned Windows")]
    [InlineData("namespace Microsoft.Win32.Descendant { class Widget { } } class C { Microsoft.Win32.Descendant.Widget? value; }", "banned Windows")]
    public void Rejects_banned_syntax_and_aliases(string source, string expected)
    {
        Assert.Contains(Check("Playnite.Core", source), v => v.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Playnite.Core", "class C { System.Windows.Input.ICommand? command; }")]
    [InlineData("Playnite.Core", "class C { object M() => new System.Random(42); }")]
    [InlineData("Playnite.Core", "class C { int M() { try { return 1; } catch (System.Exception) { throw; } } }")]
    [InlineData("Playnite.Platform.Linux", "class C { object M() => System.DateTime.Now; }")]
    [InlineData("Playnite.Cli", "class C { static void Main() { System.Environment.Exit(1); } }")]
    [InlineData("Playnite.Desktop", "class C { async void Click(object? sender, System.EventArgs e) { await System.Threading.Tasks.Task.Yield(); } }")]
    [InlineData("Playnite.Core", "class C { void M() { var p = new System.Diagnostics.ProcessStartInfo(\"tool\") { UseShellExecute = false }; p.ArgumentList.Add(\"value with spaces\"); System.Diagnostics.Process.Start(p); } }")]
    [InlineData("Playnite.Cli", "class C { static void Main() {\n// CLI startup boundary requires synchronous completion.\nSystem.Threading.Tasks.Task.CompletedTask.Wait();\n} }")]
    [InlineData("Playnite.Core", "class C { object M() => new System.Diagnostics.ProcessStartInfo(\"tool\", new[] { \"value with spaces\" }); }")]
    [InlineData("Playnite.Core", "class C { object? M() => System.Diagnostics.Process.Start(\"tool\", new[] { \"value with spaces\" }); }")]
    public void Permits_documented_exceptions(string project, string source)
    {
        Assert.Empty(Check(project, source));
    }

    [Theory]
    [InlineData("class C { static void Main() { System.Threading.Tasks.Task.CompletedTask.Wait(); } }")]
    [InlineData("class C { static void Main() { } void M() {\n// Not the entry point.\nSystem.Threading.Tasks.Task.CompletedTask.Wait();\n} }")]
    public void Cli_wait_exception_requires_an_entry_point_and_comment(string source)
    {
        Assert.Contains(Check("Playnite.Cli", source), v => v.Contains("blocking task wait", StringComparison.Ordinal));
    }

    [Fact]
    public void Symbol_catalog_covers_every_forbidden_framework_overload()
    {
        var compilation = CSharpCompilation.Create("SymbolCatalog", references: References);
        var banned = File.ReadAllLines(Path.Combine(ProductGraph.Root, "BannedSymbols.txt"))
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal)).Select(line => line.Split(';')[0]).ToHashSet();
        foreach (var (typeName, memberName) in new[]
        {
            ("System.GC", "Collect"), ("System.AppDomain", "CreateDomain"),
            ("System.Threading.Thread", "Abort"), ("System.Diagnostics.Process", "Start"),
            ("System.Diagnostics.ProcessStartInfo", ".ctor")
        })
        {
            var type = compilation.GetTypeByMetadataName(typeName)!;
            var methods = type.GetMembers().OfType<IMethodSymbol>().Where(m => m.Name == memberName);
            if (typeName == "System.Diagnostics.Process")
            {
                methods = methods.Where(m => m.Parameters.Length > 0 && m.Parameters[0].Type.SpecialType == SpecialType.System_String
                    && !(m.Parameters.Length == 2 && m.Parameters[1].Type.SpecialType != SpecialType.System_String));
            }
            else if (typeName == "System.Diagnostics.ProcessStartInfo")
            {
                methods = methods.Where(m => m.Parameters.Length == 2 && m.Parameters[1].Type.SpecialType == SpecialType.System_String);
            }

            Assert.NotEmpty(methods);
            foreach (var method in methods)
            {
                var id = method.GetDocumentationCommentId();
                Assert.NotNull(id);
                Assert.Contains(id, banned);
            }
        }
    }

    private static string[] Check(string project, string source)
    {
        var compilation = CSharpCompilation.Create("PolicyProbe",
            [CSharpSyntaxTree.ParseText(source)], References,
            new CSharpCompilationOptions(project == "Playnite.Cli" ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary));
        // Obsolete-but-banned APIs can produce compiler errors in net11; allow those
        // diagnostics only when Roslyn still resolves the symbol for policy inspection.
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error && d.Id is not ("CS0619" or "SYSLIB0006" or "SYSLIB0024"));
        return SourcePolicy.Violations(project, compilation).ToArray();
    }
}
