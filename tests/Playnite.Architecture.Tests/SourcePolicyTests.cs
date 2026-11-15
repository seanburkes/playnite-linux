using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class SourcePolicyTests
{
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

        Assert.Contains(PolicyProbe.Check(project, source), v => v.Contains(expected, StringComparison.Ordinal));
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
        Assert.Contains(PolicyProbe.Check("Playnite.Core", source), v => v.Contains(expected, StringComparison.Ordinal));
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
        Assert.Empty(PolicyProbe.Check(project, source));
    }

    [Theory]
    [InlineData("class C { static void Main() { System.Threading.Tasks.Task.CompletedTask.Wait(); } }")]
    [InlineData("class C { static void Main() { } void M() {\n// Not the entry point.\nSystem.Threading.Tasks.Task.CompletedTask.Wait();\n} }")]
    public void Cli_wait_exception_requires_an_entry_point_and_comment(string source)
    {
        Assert.Contains(PolicyProbe.Check("Playnite.Cli", source), v => v.Contains("blocking task wait", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Playnite.Benchmarks", "Playnite.Benchmarks", true, false)]
    [InlineData("Playnite.Core", "Playnite.Benchmarks", true, true)]
    [InlineData("Playnite.Benchmarks", "Playnite.Benchmarks", false, true)]
    [InlineData("Playnite.Core", "", true, true)]
    public void Gc_collect_requires_a_policy_allowlist_entry_and_a_comment(
        string project, string allowlist, bool comment, bool rejected)
    {
        var source = comment
            ? "class C { void M() {\n// Benchmark harness warm-up.\nSystem.GC.Collect();\n} }"
            : "class C { void M() { System.GC.Collect(); } }";
        var harnesses = allowlist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var violations = PolicyProbe.Check(project, source, harnesses);
        Assert.Equal(rejected, violations.Any(v => v.Contains("banned call System.GC.Collect", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_second_product_cannot_borrow_an_allowlisted_harness_name()
    {
        // The allowlist is per project: naming another project a harness never permits this one.
        var violations = PolicyProbe.Check("Playnite.Core",
            "class C { void M() {\n// Benchmark harness warm-up.\nSystem.GC.Collect();\n} }",
            ["Playnite.Benchmarks"]);
        Assert.Contains(violations, v => v.Contains("banned call System.GC.Collect", StringComparison.Ordinal));
    }

    [Fact]
    public void Symbol_catalog_covers_every_forbidden_framework_overload()
    {
        var compilation = CSharpCompilation.Create("SymbolCatalog", references: PolicyProbe.References);
        var common = BannedSymbolFiles.Rows(Path.Combine(ProductGraph.Root, "BannedSymbols.txt"));
        var gc = BannedSymbolFiles.Rows(Path.Combine(ProductGraph.Root, "BannedSymbols.GcCollect.txt"));
        var blocking = BannedSymbolFiles.Rows(Path.Combine(ProductGraph.Root, "BannedSymbols.Blocking.txt"));
        foreach (var (typeName, memberName) in new[]
        {
            ("System.AppDomain", "CreateDomain"), ("System.Threading.Thread", "Abort"),
            ("System.Diagnostics.Process", "Start"), ("System.Diagnostics.ProcessStartInfo", ".ctor")
        })
        {
            var type = compilation.GetTypeByMetadataName(typeName);
            Assert.True(type != null, $"catalog target type {typeName} does not resolve");
            var methods = type!.GetMembers().OfType<IMethodSymbol>().Where(m => m.Name == memberName);
            if (typeName == "System.Diagnostics.Process")
            {
                methods = methods.Where(m => m.Parameters.Length > 0 && m.Parameters[0].Type.SpecialType == SpecialType.System_String
                    && !(m.Parameters.Length == 2 && m.Parameters[1].Type.SpecialType != SpecialType.System_String));
            }
            else if (typeName == "System.Diagnostics.ProcessStartInfo")
            {
                methods = methods.Where(m => m.Parameters.Length == 2 && m.Parameters[1].Type.SpecialType == SpecialType.System_String);
            }

            AssertCovered(methods, common, typeName);
        }

        var gcType = compilation.GetTypeByMetadataName("System.GC");
        Assert.True(gcType != null, "System.GC does not resolve");
        AssertCovered(gcType!.GetMembers().OfType<IMethodSymbol>().Where(m => m.Name == "Collect"), gc, "System.GC");

        var task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        Assert.True(task != null, "System.Threading.Tasks.Task does not resolve");
        foreach (var name in new[] { "Wait", "WaitAll", "WaitAny" })
        {
            AssertCovered(task!.GetMembers().OfType<IMethodSymbol>().Where(m => m.Name == name), blocking, "System.Threading.Tasks.Task");
        }

        foreach (var typeName in new[] { "System.Threading.Tasks.Task`1", "System.Threading.Tasks.ValueTask`1" })
        {
            var type = compilation.GetTypeByMetadataName(typeName);
            Assert.True(type != null, $"catalog target type {typeName} does not resolve");
            var results = type!.GetMembers().OfType<IPropertySymbol>().Where(p => p.Name == "Result");
            Assert.NotEmpty(results);
            AssertCovered(results, blocking, typeName);
        }
    }

    private static void AssertCovered<T>(IEnumerable<T> symbols, string[] banned, string label) where T : ISymbol
    {
        var members = symbols.ToArray();
        Assert.NotEmpty(members);
        var ids = members.Select(member => member.GetDocumentationCommentId()).ToArray();
        var missing = ids.Where(id => id == null || !banned.Contains(id)).ToArray();
        Assert.True(missing.Length == 0,
            $"{label} has uncovered overloads: {string.Join(", ", missing)}; present ids: {string.Join(" | ", ids)}");
    }
}
