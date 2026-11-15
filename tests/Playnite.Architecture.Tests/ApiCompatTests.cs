using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class ApiCompatTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiCompat_accepts_the_baseline_and_rejects_a_changed_identity_type(bool breakIdentity)
    {
        var project = ProductGraph.Products.Single(p => p.Name == "Playnite.SDK.Abstractions");
        var scratch = Path.Combine(Path.GetTempPath(), "playnite-apicompat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var right = project.Values("Output").Single();
            if (breakIdentity)
            {
                var options = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: project.Values("Define").Single().Split(','));
                var trees = project.Values("Source").Select(path =>
                    CSharpSyntaxTree.ParseText(File.ReadAllText(path).Replace("Guid Id { get; }", "string Id { get; }", StringComparison.Ordinal), options, path));
                var compilation = CSharpCompilation.Create(project.Name, trees,
                    project.Values("Reference").Select(path => MetadataReference.CreateFromFile(path)),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
                right = Path.Combine(scratch, project.Name + ".dll");
                var emit = compilation.Emit(right);
                Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
            }

            // Exercise Microsoft's task directly: no public-API analyzer can cause this failure.
            var task = new XElement("Microsoft.DotNet.ApiCompat.Task.ValidateAssembliesTask",
                new XAttribute("LeftAssemblies", project.Values("ApiCompatBaseline").Single()),
                new XAttribute("RightAssemblies", right),
                new XAttribute("LeftAssembliesReferences", string.Join(',', project.Values("Reference"))),
                new XAttribute("RightAssembliesReferences", string.Join(',', project.Values("Reference"))),
                new XAttribute("RoslynAssembliesPath", project.Values("RoslynPath").Single()));
            var input = Path.Combine(scratch, "validate.proj");
            new XDocument(new XElement("Project",
                new XElement("UsingTask", new XAttribute("TaskName", task.Name),
                    new XAttribute("AssemblyFile", project.Values("ApiCompatTask").Single())),
                new XElement("Target", new XAttribute("Name", "Validate"), task))).Save(input);
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "msbuild", input, "-t:Validate", "-nologo", "-v:minimal" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var output = await stdout + await stderr;
            if (breakIdentity)
            {
                Assert.NotEqual(0, process.ExitCode);
                Assert.Contains("CP0002", output, StringComparison.Ordinal);
                Assert.Contains("System.Guid Playnite.SDK.Models.IIdentifiable.Id.get", output, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(process.ExitCode == 0, output);
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
