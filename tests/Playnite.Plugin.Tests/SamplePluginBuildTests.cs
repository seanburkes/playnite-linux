using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace Playnite.Plugin.Tests;

/// <summary>
/// Compile and assembly-shape evidence for the p6-009 samples. The test project references each
/// sample, so a sample that does not compile against the frozen Abstractions plus the preview
/// assembly cannot reach this test.
/// </summary>
public sealed class SamplePluginBuildTests
{
    public static TheoryData<string> Samples => new(SampleAssemblies.Names);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_assembly_exists_and_targets_net11(string name)
    {
        var path = SampleAssemblies.PathFor(name);
        Assert.True(File.Exists(path), $"{name}.dll is missing from the test output: {path}");

        var identity = AssemblyName.GetAssemblyName(path);
        Assert.Equal(name, identity.Name);

        var assembly = SampleAssemblies.Load(name);
        var target = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
        Assert.NotNull(target);
        Assert.StartsWith(".NETCoreApp,Version=v11.0", target.FrameworkName, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_references_the_preview_sdk_and_no_product_or_ui_assembly(string name)
    {
        var references = SampleAssemblies.Load(name).GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(referenceName => referenceName is not null)
            .ToArray();

        Assert.Contains("Playnite.SDK", references);
        Assert.Contains("Playnite.SDK.Abstractions", references);
        Assert.DoesNotContain(references, reference =>
            reference is "Playnite.Core" or "Playnite.Desktop" or "Playnite.Cli"
                or "Playnite.Platform" or "Playnite.Platform.Linux");
        Assert.DoesNotContain(references, reference =>
            reference!.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_project_takes_the_sdk_as_its_only_playnite_reference(string name)
    {
        var projectPath = Path.Combine(ProductRoot(), "samples", name, name + ".csproj");
        Assert.True(File.Exists(projectPath), projectPath);

        var project = XDocument.Load(projectPath);
        var properties = project.Descendants("PropertyGroup").Elements().ToArray();
        Assert.Equal("net11.0", properties.Single(element => element.Name == "TargetFramework").Value);
        Assert.Equal("true", properties.Single(element => element.Name == "TreatWarningsAsErrors").Value);

        var references = project.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .ToArray();
        Assert.Equal(["../../src/Playnite.SDK/Playnite.SDK.csproj"], references);
    }

    private static string ProductRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Playnite.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Cannot locate Playnite.slnx from the test output.");
    }
}
