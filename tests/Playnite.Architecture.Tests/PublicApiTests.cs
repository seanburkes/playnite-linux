using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class PublicApiTests
{
    private static ProductGraph Abstractions => ProductGraph.Products.Single(p => p.Name == "Playnite.SDK.Abstractions");

    [Fact]
    public void Shipped_API_matches_the_review_protected_checksum()
    {
        var shipped = Path.Combine(ProductGraph.Root, "src", "Playnite.SDK.Abstractions", "PublicAPI.Shipped.txt");
        var violation = ShippedSurfaceViolation(File.ReadAllBytes(shipped));
        Assert.True(violation == null, violation);
    }

    [Theory]
    [InlineData("AreObjectsEqual(object? object1, object? object2)", "AreObjectsEqual(string? object1, string? object2)")]
    [InlineData("MetadataField.Name = 0", "MetadataField.Title = 0")]
    public void Same_arity_signature_or_enum_rename_cannot_silently_rebaseline(string original, string replacement)
    {
        var shipped = File.ReadAllText(Path.Combine(ProductGraph.Root, "src", "Playnite.SDK.Abstractions", "PublicAPI.Shipped.txt"));
        Assert.Contains(original, shipped, StringComparison.Ordinal);
        var changed = Encoding.UTF8.GetBytes(shipped.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Contains("Shipped SDK API checksum mismatch", ShippedSurfaceViolation(changed)!, StringComparison.Ordinal);
    }

    [Fact]
    public void API_surface_and_baseline_are_review_protected_and_code_owned()
    {
        var workflow = File.ReadAllText(Path.Combine(ProductGraph.Root, ".github", "workflows", "plan-guards.yml"));
        var protectedPaths = workflow.Split("PROTECTED_PATHS: >-", StringSplitOptions.None)[1].Split("\njobs:", StringSplitOptions.None)[0]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ownership = File.ReadAllLines(Path.Combine(ProductGraph.Root, ".github", "CODEOWNERS"))
            .Where(line => !line.StartsWith('#')).Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        foreach (var path in new[] { "src/Playnite.SDK.Abstractions/PublicAPI.Shipped.txt",
            "src/Playnite.SDK.Abstractions/PublicAPI.Unshipped.txt", "src/Playnite.SDK.Abstractions/api-baseline" })
        {
            Assert.Contains(path, protectedPaths);
            Assert.Contains(ownership, row => row.Length >= 2 && row[0].TrimEnd('/') == path && row[1] == "@seanburkes");
        }
    }

    private static string? ShippedSurfaceViolation(byte[] surface)
    {
        var approved = File.ReadAllText(Path.Combine(ProductGraph.Root, "src", "Playnite.SDK.Abstractions", "api-baseline", "PublicAPI.Shipped.sha256")).Trim();
        var actual = Convert.ToHexStringLower(SHA256.HashData(surface));
        return actual == approved ? null : $"Shipped SDK API checksum mismatch: expected {approved}, got {actual}; rebaselining requires oracle-change and second-review approval";
    }

    [Fact]
    public void Evaluated_abstractions_build_enables_both_API_guards()
    {
        var project = Abstractions;
        Assert.Equal("true", project.Values("WarningsAsErrors").Single());
        Assert.Equal("true", project.Values("ApiCompatEnabled").Single());
        var baseline = project.Values("ApiCompatBaseline").Single();
        Assert.Equal(Path.Combine(ProductGraph.Root, "src", project.Name, "api-baseline", project.Name + ".dll"), baseline);
        Assert.True(File.Exists(baseline));
        Assert.Contains("Microsoft.CodeAnalysis.PublicApiAnalyzers", project.Values("Analyzer"));
        Assert.Contains("Microsoft.DotNet.ApiCompat.Task", project.DirectPackages());
        foreach (var name in new[] { "PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt" })
        {
            Assert.Single(project.Values("AdditionalFile").Where(file => Path.GetFileName(file) == name));
        }
    }

    [Fact]
    public async Task Public_API_analyzer_accepts_the_committed_surface()
    {
        Assert.Empty(await Analyze());
    }

    [Fact]
    public async Task Public_API_analyzer_rejects_an_untracked_addition()
    {
        var diagnostics = await Analyze(addition: "namespace Playnite.SDK { public class UntrackedContract {} }");
        Assert.Contains(diagnostics, d => d.Id == "RS0016" && d.GetMessage().Contains("UntrackedContract", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Public_API_analyzer_rejects_a_removed_shipped_member()
    {
        var diagnostics = await Analyze(removeIdentity: true);
        Assert.Contains(diagnostics, d => d.Id == "RS0017" && d.GetMessage().Contains("IIdentifiable.Id", StringComparison.Ordinal));
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string? addition = null, bool removeIdentity = false)
    {
        var project = Abstractions;
        var options = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: project.Values("Define").Single().Split(','));
        var trees = project.Values("Source").Select(path =>
        {
            var source = File.ReadAllText(path);
            if (removeIdentity && Path.GetFileName(path) == "IIdentifiable.cs")
            {
                source = source.Replace("Guid Id { get; }", string.Empty, StringComparison.Ordinal);
            }

            return CSharpSyntaxTree.ParseText(source, options, path);
        }).ToList();
        if (addition != null)
        {
            trees.Add(CSharpSyntaxTree.ParseText(addition, options));
        }

        var compilation = CSharpCompilation.Create(project.Name, trees,
            project.Values("Reference").Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var analyzerPath = project.Values("AnalyzerPath").Single(path => Path.GetFileNameWithoutExtension(path) == "Microsoft.CodeAnalysis.PublicApiAnalyzers");
        var analyzerType = Assembly.LoadFrom(analyzerPath).GetType("Microsoft.CodeAnalysis.PublicApiAnalyzers.DeclarePublicApiAnalyzer", throwOnError: true)!;
        ImmutableArray<DiagnosticAnalyzer> analyzers = [(DiagnosticAnalyzer)Activator.CreateInstance(analyzerType)!];
        var files = project.Values("AdditionalFile").Where(path => Path.GetFileName(path).StartsWith("PublicAPI.", StringComparison.Ordinal))
            .Select(path => (AdditionalText)new ApiFile(path)).ToImmutableArray();
        return await compilation.WithAnalyzers(analyzers, new AnalyzerOptions(files)).GetAnalyzerDiagnosticsAsync();
    }

    private sealed class ApiFile(string path) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(File.ReadAllText(path));
    }
}
