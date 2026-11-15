using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class ApiCompatTests
{
    [Theory]
    [InlineData("NoWarn", "RS0016")]
    [InlineData("NoWarn", "RS0017")]
    [InlineData("WarningsNotAsErrors", "RS0016")]
    [InlineData("WarningsNotAsErrors", "RS0017")]
    [InlineData("TreatWarningsAsErrors", "false")]
    public async Task Public_API_warning_policy_cannot_be_bypassed(string property, string value)
    {
        var project = ProductGraph.Products.Single(p => p.Name == "Playnite.SDK.Abstractions");
        var result = await RunMsBuild(project.ProjectFile, "ValidateSdkPublicApiPolicy", $"-p:{property}={value}");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SDK public API", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiCompat_accepts_the_baseline_and_rejects_a_removed_member(bool breakIdentity)
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
                    CSharpSyntaxTree.ParseText(File.ReadAllText(path).Replace("Guid Id { get; }", string.Empty, StringComparison.Ordinal), options, path));
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
            var result = await RunMsBuild(input, "Validate");
            if (breakIdentity)
            {
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("CP0002", result.Output, StringComparison.Ordinal);
                Assert.Contains("System.Guid Playnite.SDK.Models.IIdentifiable.Id.get", result.Output, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(result.ExitCode == 0, result.Output);
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunMsBuild(string input, string target, params string[] properties)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "msbuild", input, $"-t:{target}", "-nologo", "-v:minimal" }.Concat(properties))
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

        return (process.ExitCode, await stdout + await stderr);
    }
}
