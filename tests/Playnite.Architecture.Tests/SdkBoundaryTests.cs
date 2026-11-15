using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class SdkBoundaryTests
{
    [Fact]
    public void Evaluated_Core_and_abstractions_obey_the_SDK_boundary()
    {
        var projects = ProductGraph.Products.Where(p => p.Name is "Playnite.Core" or "Playnite.SDK.Abstractions").ToArray();
        Assert.Equal(new[] { "Playnite.Core", "Playnite.SDK.Abstractions" }, projects.Select(p => p.Name).Order());
        foreach (var project in projects)
        {
            var violations = SdkBoundaryPolicy.GraphViolations(project.Name, project.Values("ProjectReference"), project.Dependencies()).ToArray();
            Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
            Assert.Contains("Microsoft.NETCore.App", project.Values("FrameworkReference"));
            var buildViolations = SdkBoundaryPolicy.BuildInputViolations(project.Name, project.Values("UseWPF").Single(),
                project.Values("UseWindowsForms").Single(), project.Values("FrameworkReference")).ToArray();
            Assert.True(buildViolations.Length == 0, string.Join(Environment.NewLine, buildViolations));
        }
    }

    [Fact]
    public void Missing_Core_abstractions_reference_is_rejected()
    {
        Assert.Contains(SdkBoundaryPolicy.GraphViolations("Playnite.Core", [], []), v => v.Contains("must directly reference", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Core_UI_SDK_reference_is_rejected_even_when_transitive(bool direct)
    {
        string[] references = direct ? ["Playnite.SDK.Abstractions", "Playnite.SDK"] : ["Playnite.SDK.Abstractions"];
        Assert.Contains(SdkBoundaryPolicy.GraphViolations("Playnite.Core", references, ["Playnite.SDK"]),
            v => v.Contains("UI SDK", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Playnite.Platform")]
    [InlineData("Playnite.Platform.Linux")]
    [InlineData("Playnite.Desktop")]
    [InlineData("Playnite.Steam")]
    public void Core_rejects_every_unlisted_Playnite_dependency_in_the_package_closure(string dependency)
    {
        Assert.Contains(SdkBoundaryPolicy.GraphViolations("Playnite.Core", ["Playnite.SDK.Abstractions"], [dependency]),
            violation => violation == $"Core resolved Playnite dependency {dependency} is not in the architecture table's allowed project list");
    }

    [Theory]
    [InlineData("Avalonia")]
    [InlineData("Avalonia.Controls")]
    [InlineData("Playnite.Platform")]
    [InlineData("Playnite.Platform.Linux")]
    [InlineData("Playnite.SDK")]
    [InlineData("Microsoft.Win32.Registry")]
    [InlineData("System.Drawing.Common")]
    [InlineData("System.Windows")]
    [InlineData("WindowsBase")]
    [InlineData("PresentationCore")]
    [InlineData("PresentationFramework")]
    [InlineData("System.Xaml")]
    [InlineData("System.IO.Packaging")]
    [InlineData("System.Windows.Forms")]
    [InlineData("Microsoft.UI")]
    [InlineData("Windows.UI")]
    [InlineData("WindowsAppSDK")]
    [InlineData("Microsoft.WindowsAppSDK")]
    public void Abstractions_forbidden_dependency_is_rejected_directly_and_transitively(string dependency)
    {
        Assert.NotEmpty(SdkBoundaryPolicy.GraphViolations("Playnite.SDK.Abstractions", [dependency], []));
        Assert.NotEmpty(SdkBoundaryPolicy.GraphViolations("Playnite.SDK.Abstractions", [], [dependency]));
    }

    [Theory]
    [InlineData("true", "", "Microsoft.NETCore.App")]
    [InlineData("", "true", "Microsoft.NETCore.App")]
    [InlineData("", "", "Microsoft.WindowsDesktop.App")]
    [InlineData("", "", "Microsoft.WindowsDesktop.App.WPF")]
    [InlineData("", "", "Microsoft.WindowsDesktop.App.WindowsForms")]
    public void Unused_Windows_UI_build_inputs_cannot_hide_behind_framework_reference_filtering(string wpf, string forms, string framework)
    {
        foreach (var project in new[] { "Playnite.Core", "Playnite.SDK.Abstractions" })
        {
            Assert.NotEmpty(SdkBoundaryPolicy.BuildInputViolations(project, wpf, forms, [framework]));
        }
    }

    [Theory]
    [InlineData("Avalonia.Controls", "Control")]
    [InlineData("Avalonia.Media", "IImage")]
    [InlineData("Playnite.SDK.Controls", "PluginUserControl")]
    [InlineData("Playnite.Platform", "IPaths")]
    [InlineData("System.IO.Packaging", "Package")]
    [InlineData("Windows.UI.Xaml", "Window")]
    public void Abstractions_rejects_UI_or_platform_types_even_behind_an_alias(string ns, string type)
    {
        var source = $$"""
            using LeakedType = {{ns}}.{{type}};
            namespace {{ns}} { public class {{type}} {} }
            public class Contract { public System.Collections.Generic.List<LeakedType[]> Leak() => new(); }
            """;
        Assert.Contains(PolicyProbe.Check("Playnite.SDK.Abstractions", source), v => v.Contains("UI/platform type", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Playnite.SDK")]
    [InlineData("Avalonia")]
    [InlineData("Avalonia.Controls")]
    [InlineData("Playnite.Platform")]
    [InlineData("Playnite.Platform.Linux")]
    [InlineData("Playnite.SDK.Controls")]
    [InlineData("PresentationFramework")]
    [InlineData("WindowsAppSDK")]
    public void Any_type_from_a_UI_or_platform_assembly_is_rejected_regardless_of_namespace(string assemblyName)
    {
        var ui = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText("namespace Innocent { public class Dialog {} }")],
            PolicyProbe.References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        Assert.True(ui.Emit(image).Success);
        var compilation = CSharpCompilation.Create("Playnite.SDK.Abstractions",
            [CSharpSyntaxTree.ParseText("public class Contract { public Innocent.Dialog? Dialog { get; set; } }")],
            PolicyProbe.References.Where(reference => Path.GetFileNameWithoutExtension(reference.Display) != assemblyName)
                .Append(MetadataReference.CreateFromImage(image.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(SourcePolicy.Violations("Playnite.SDK.Abstractions", compilation), v => v.Contains("UI/platform type", StringComparison.Ordinal));
    }

    [Fact]
    public void BCL_contracts_remain_allowed()
    {
        Assert.Empty(SdkBoundaryPolicy.GraphViolations("Playnite.Core", ["Playnite.SDK.Abstractions"], ["Playnite.SDK.Abstractions", "System.Runtime"]));
        Assert.Empty(SdkBoundaryPolicy.GraphViolations("Playnite.SDK.Abstractions", [], ["System.Runtime"]));
        Assert.Empty(SdkBoundaryPolicy.BuildInputViolations("Playnite.SDK.Abstractions", "", "", ["Microsoft.NETCore.App"]));
        Assert.Empty(PolicyProbe.Check("Playnite.SDK.Abstractions", "public interface Contract { System.Guid Id { get; } System.Windows.Input.ICommand Command { get; } }"));
    }

    [Theory]
    [InlineData("Microsoft.CodeAnalysis.PublicApiAnalyzers")]
    [InlineData("Microsoft.DotNet.ApiCompat.Task")]
    public void API_tooling_packages_use_the_closed_table_vocabulary_without_a_bypass(string package)
    {
        Assert.Equal(ArchitectureTable.TokenKind.Package, ArchitectureTable.Classify(package));
        Assert.Null(ArchitectureTable.PackageViolation(ArchitectureTable.For("Playnite.SDK.Abstractions"), package));
        Assert.NotNull(ArchitectureTable.PackageViolation(ArchitectureTable.For("Playnite.Core"), package));
    }

    [Fact]
    public void Library_discovery_receives_live_host_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var args = new LibraryGetGamesArgs(cancellation.Token);
        Assert.False(args.CancelToken.IsCancellationRequested);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => args.CancelToken.ThrowIfCancellationRequested());
        Assert.Equal(CancellationToken.None, new LibraryGetGamesArgs().CancelToken);
    }

    [Fact]
    public void Seed_contracts_are_public_in_the_Avalonia_free_versioned_assembly()
    {
        var assembly = typeof(IIdentifiable).Assembly;
        Type[] contracts = [typeof(IIdentifiable), typeof(InstallationStatus), typeof(IDataSerializer), typeof(LibraryGetGamesArgs),
            typeof(MetadataField), typeof(OnApplicationStartedEventArgs), typeof(SdkVersions)];
        Assert.All(contracts, type => { Assert.True(type.IsPublic); Assert.Same(assembly, type.Assembly); });
        Assert.Equal(new Version(7, 0, 0, 0), SdkVersions.SDKVersion);
        Assert.Equal(27, typeof(IDataSerializer).GetMethods().Length);
        Assert.Equal(Enumerable.Range(0, 19), Enum.GetValues<MetadataField>().Select(value => (int)value));
        Assert.Equal(typeof(Guid), typeof(IIdentifiable).GetProperty("Id")!.PropertyType);
    }

    [Theory]
    [InlineData(InstallationStatus.Installed, 0, "LOCGameIsInstalledTitle")]
    [InlineData(InstallationStatus.Uninstalled, 1, "LOCGameIsUnInstalledTitle")]
    public void Installation_status_preserves_wire_value_and_localization_key(InstallationStatus status, int value, string key)
    {
        Assert.Equal(value, (int)status);
        var field = typeof(InstallationStatus).GetField(status.ToString())!;
        var description = (System.ComponentModel.DescriptionAttribute)Attribute.GetCustomAttribute(field, typeof(System.ComponentModel.DescriptionAttribute))!;
        Assert.Equal(key, description.Description);
    }
}
