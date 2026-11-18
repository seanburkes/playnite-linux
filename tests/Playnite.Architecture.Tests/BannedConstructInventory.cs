namespace Playnite.Architecture.Tests;

/// <summary>
/// One row per banned construct in docs/port/PORTING.md. The construct text is the table's
/// first cell with backticks removed. A row is owned by a symbol file, a source-policy probe,
/// or both; the test fails when a PORTING row has no owner or an owner no longer works.
/// </summary>
internal sealed record BannedConstructRow(
    string Construct,
    string? SymbolFile,
    string? SymbolId,
    bool SymbolExpectedUnresolved,
    string? ProbeProject,
    string? ProbeSource,
    string? ProbeMessage,
    string Condition,
    string Approximation);

internal static class BannedConstructInventory
{
    internal static readonly BannedConstructRow[] Rows =
    [        new("System.Windows.*", null, null, false, "Playnite.Core",
            "namespace System.Windows.Other { class Widget { } } class C { System.Windows.Other.Widget? value; }",
            "banned Windows", "Allowed only for System.Windows.Input.ICommand; every other System.Windows type and descendant is banned.",
            "Exact for source resolution. Dynamic, reflection or generated code is outside source analysis."),
        new("System.Drawing.*", "BannedSymbols.txt", "N:System.Drawing", false, "Playnite.Core",
            "class C { System.Drawing.Point value; }", "banned Windows", "No exception.", "Exact."),
        new("Microsoft.Win32.*", "BannedSymbols.txt", "N:Microsoft.Win32", false, "Playnite.Core",
            "class C { Microsoft.Win32.RegistryKey? value; }", "banned Windows", "No exception.", "Exact."),
        new("DllImport/LibraryImport", "BannedSymbols.Interop.txt", "T:System.Runtime.InteropServices.DllImportAttribute", false,
            "Playnite.Core", "class C { [System.Runtime.InteropServices.DllImport(\"libc\")] static extern int getpid(); }",
            "native import", "Only Playnite.Platform.Linux with a reviewed allow-list.",
            "The reviewed allow-list permits exactly the libc entry points close, openat, read and statx for a handle-bound file read, plus pidfd_open and pidfd_send_signal for a PID-reuse-free termination. Every other entry point, every other project and an entry point that source cannot resolve are rejected, and pidfd_send_signal must be a direct call whose signal argument is the compile-time constant SIGTERM (15), so no other signal can be delivered."),
        new("System.Management", "BannedSymbols.txt", "N:System.Management", true, null, null, null, "No exception.",
            "The namespace is absent from the net11 reference set; only the symbol row can catch it if a package adds it."),
        new("BinaryFormatter", "BannedSymbols.txt", "T:System.Runtime.Serialization.Formatters.Binary.BinaryFormatter", false,
            "Playnite.Core", "class C { System.Runtime.Serialization.Formatters.Binary.BinaryFormatter? value; }",
            "banned Windows", "No exception.", "Exact."),
        new("Thread.Abort", "BannedSymbols.txt", "M:System.Threading.Thread.Abort", false, "Playnite.Core",
            "class C { void M() { System.Threading.Thread.CurrentThread.Abort(); } }",
            "banned call", "No exception.", "Exact."),
        new("AppDomain.CreateDomain", "BannedSymbols.txt", "M:System.AppDomain.CreateDomain(System.String)", false, "Playnite.Core",
            "class C { void M() { System.AppDomain.CreateDomain(\"x\"); } }",
            "banned call", "AssemblyLoadContext may replace it, but no wrapper is approved yet.", "Exact."),
        new("Process.Start(string) with a shell string", "BannedSymbols.txt", "M:System.Diagnostics.Process.Start(System.String,System.String)", false,
            "Playnite.Core", "class C { object? M() => System.Diagnostics.Process.Start(\"sh\", \"-c command\"); }",
            "shell-string", "Use ProcessStartInfo with ArgumentList, or an IEnumerable<string> overload.",
            "Exact for the listed overloads; UseShellExecute and dynamic argument values are separately checked."),
        new("Environment.Exit in library code", "BannedSymbols.Library.txt", "M:System.Environment.Exit(System.Int32)", false, "Playnite.Core",
            "class C { void M() { System.Environment.Exit(0); } }",
            "library code", "Only Playnite.Desktop, Playnite.Cli and Playnite.LegacyImport are hosts.", "Exact."),
        new("GC.Collect", "BannedSymbols.GcCollect.txt", "M:System.GC.Collect", false, "Playnite.Core",
            "class C { void M() { System.GC.Collect(); } }",
            "banned call", "Only a policy-allowlisted benchmark/soak harness with a comment on the call.",
            "No harness exists yet (p8-003). Until one is named in SourcePolicy.BenchmarkHarnessProjects and Directory.Build.props, every product call is rejected; a project cannot self-declare, and the list alone still requires the comment."),
        new("catch { }", null, null, false, "Playnite.Core",
            "class C { void M() { try { } catch { } } }", "empty catch",
            "Empty catch bodies are banned; catch specific, log, or rethrow.",
            "Only emptiness is checked. A typed catch that swallows without logging still passes and stays a review obligation."),
        new("async void", null, null, false, "Playnite.Core",
            "class C { async void M() { await System.Threading.Tasks.Task.Yield(); } }", "async void",
            "UI event handlers only.",
            "Registration cannot be proven by source analysis; the approximation is a Desktop/SDK (object sender, EventArgs-derived) signature, with local functions rejected everywhere."),
        new(".Result/.Wait()", "BannedSymbols.Blocking.txt", "P:System.Threading.Tasks.Task`1.Result", false, "Playnite.Core",
            "class C { object? M() => System.Threading.Tasks.Task.FromResult(1).Result; }",
            "blocking task Result", "Only a commented call inside the Playnite.Cli entry-point method.",
            "The entry-point method is treated as the whole top-level boundary; a commented call anywhere inside it is accepted."),
        new("DateTime.Now in Core", "BannedSymbols.Core.txt", "P:System.DateTime.Now", false, "Playnite.Core",
            "class C { object M() => System.DateTime.Now; }", "DateTime.Now",
            "Not allowed in Playnite.Core; other projects are outside this rule.", "Exact."),
        new("Guid.NewGuid() in pure logic", "BannedSymbols.Logic.txt", "M:System.Guid.NewGuid", false, "Playnite.Core",
            "class C { object M() => System.Guid.NewGuid(); }", "ambient randomness",
            "Not allowed in pure logic; inject an ID or pass one in.",
            "Purity is approximated by the Core, SDK.Abstractions and Steam project scope; impure members in those projects are also blocked."),
        new("Random without a seed in logic", "BannedSymbols.Logic.txt", "M:System.Random.#ctor", false, "Playnite.Core",
            "class C { object M() => new System.Random(); }", "ambient randomness",
            "Not allowed in pure logic; inject a seeded Random.", "Same project-scope approximation as Guid.NewGuid."),
    ];

    /// <summary>
    /// Returns every ownership defect, including an ownerless row. Split from the xUnit
    /// assertions so a test can prove the failure path with a synthetic row and a mutated
    /// PORTING construct list.
    /// </summary>
    internal static string[] OwnershipProblems(string[] portingConstructs, IEnumerable<BannedConstructRow> rows)
    {
        var problems = new List<string>();
        foreach (var row in rows)
        {
            if (row.SymbolFile == null && row.ProbeSource == null)
            {
                problems.Add($"{row.Construct}: row has no symbol file or source probe owner");
            }

            if (row.SymbolFile != null && string.IsNullOrWhiteSpace(row.SymbolId))
            {
                problems.Add($"{row.Construct}: symbol owner has no representative row");
            }

            if (row.ProbeSource != null
                && (string.IsNullOrWhiteSpace(row.ProbeProject) || string.IsNullOrWhiteSpace(row.ProbeMessage)))
            {
                problems.Add($"{row.Construct}: source probe needs a project and an expected message");
            }
        }

        var table = portingConstructs.Order(StringComparer.Ordinal).ToArray();
        var inventory = rows.Select(row => row.Construct).Order(StringComparer.Ordinal).ToArray();

        // Multiset check: the previous sequence comparison caught duplicates; Except alone
        // would accept two identical rows as one owner and hide a rule duplicated in the table.
        foreach (var duplicate in inventory.GroupBy(value => value, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            problems.Add($"inventory row '{duplicate.Key}' appears {duplicate.Count()} times");
        }

        foreach (var duplicate in table.GroupBy(value => value, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            problems.Add($"PORTING construct '{duplicate.Key}' appears {duplicate.Count()} times");
        }

        foreach (var missing in table.Except(inventory, StringComparer.Ordinal))
        {
            problems.Add($"PORTING row '{missing}' has no inventory owner");
        }

        foreach (var extra in inventory.Except(table, StringComparer.Ordinal))
        {
            problems.Add($"inventory row '{extra}' has no PORTING entry (removed rule?)");
        }

        return problems.ToArray();
    }
}
