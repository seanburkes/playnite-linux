using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Playnite.SDK;
using Playnite.SDK.Events;
using Xunit;

namespace Playnite.Architecture.Tests;

/// <summary>
/// p6-007 URI/HTML move evidence: the plugin-facing URI and description contracts live in the
/// abstraction assembly under their v10 namespaces, and a plugin can compile against that
/// assembly alone.
/// </summary>
public sealed class SdkUriHtmlSurfaceTests
{
    private static Assembly Abstractions => typeof(IUriHandlerAPI).Assembly;

    [Fact]
    public void Sample_plugin_compiles_against_the_abstractions_assembly_alone()
    {
        // A plugin consumer sees only Playnite.SDK.Abstractions: no Core and no SDK
        // implementation assembly. The sample uses IUriHandlerAPI with PlayniteUriEventArgs and
        // implements both description contracts, so a Core type in either signature fails this
        // compile rather than only reflection.
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Playnite.SDK;
            using Playnite.SDK.Descriptions;
            using Playnite.SDK.Events;

            public sealed class SamplePlugin : IHtmlDescriptionProvider, IHtmlSanitizer
            {
                private readonly IUriHandlerAPI uriHandler;

                public SamplePlugin(IUriHandlerAPI uriHandler)
                {
                    this.uriHandler = uriHandler;
                }

                public void Listen()
                {
                    uriHandler.RegisterSource("sample", OnUri);
                    uriHandler.RemoveSource("sample");
                }

                private static void OnUri(PlayniteUriEventArgs args)
                {
                    _ = string.Join(",", args.Arguments);
                }

                public ValueTask<HtmlDescriptionSourceResult> GetDescriptionAsync(
                    string gameId,
                    CancellationToken cancellationToken = default)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(HtmlDescriptionSourceResult.Found($"<p>{gameId}</p>"));
                }

                public HtmlSanitizationResult Sanitize(string? html) => new(html ?? string.Empty);
            }
            """;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => !Path.GetFileNameWithoutExtension(path).StartsWith("Playnite", StringComparison.Ordinal))
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(Abstractions.Location))
            .ToArray();
        Assert.DoesNotContain(references, reference =>
            Path.GetFileNameWithoutExtension(reference.Display) == "Playnite.Core");

        var compilation = CSharpCompilation.Create(
            "SamplePlugin",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
        Assert.Equal("Playnite.SDK.Abstractions", Abstractions.GetName().Name);
    }
}
