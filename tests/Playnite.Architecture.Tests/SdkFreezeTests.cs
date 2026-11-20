using System.Reflection;
using System.Text.RegularExpressions;
using Playnite.Core.Descriptions;
using Playnite.Core.Hosting;
using Playnite.Core.UriHandling;
using Playnite.SDK.Data;
using Xunit;

namespace Playnite.Architecture.Tests;

/// <summary>
/// Freeze evidence for p6-008: the committed Abstractions manifest, the complete v6-to-v7
/// ApiCompat classification, the fake-host interface map, and the Core/Avalonia boundary.
/// </summary>
public sealed class SdkFreezeTests
{
    private static readonly string[] AllowedClassifications = ["adapted", "deferred", "excluded"];

    private static string Docs(string name) => Path.Combine(ProductGraph.Root, "docs", "port", name);

    private static Assembly Abstractions => typeof(IDataSerializer).Assembly;

    [Fact]
    public void Frozen_manifest_is_the_committed_PublicAPI_surface()
    {
        var expected = Declarations("PublicAPI.Shipped.txt")
            .Concat(Declarations("PublicAPI.Unshipped.txt"))
            .ToArray();
        var manifest = File.ReadAllLines(Docs("p6-008-frozen-api.txt"))
            .Where(line => line.Length > 0 && line != "#nullable enable")
            .ToArray();

        Assert.Equal(expected, manifest);
    }

    [Fact]
    public void Frozen_assembly_exports_exactly_the_declared_types()
    {
        var manifest = File.ReadAllLines(Docs("p6-008-frozen-api.txt"));
        var declared = manifest
            .Where(line => line.Length > 0 && line != "#nullable enable")
            .Where(line => !line.Contains('(') && !line.Contains(" -> ") && !line.Contains(" = "))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var exported = Abstractions.GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(exported, declared);
        Assert.Equal(9, exported.Length);
        Assert.Equal(27, typeof(IDataSerializer).GetMethods().Length);
        Assert.Single(typeof(Playnite.SDK.Models.IIdentifiable).GetProperties());
    }

    [Fact]
    public void Frozen_type_members_have_the_declared_signatures()
    {
        var identifiable = typeof(Playnite.SDK.Models.IIdentifiable);
        var id = Assert.Single(identifiable.GetProperties());
        Assert.Equal("Id", id.Name);
        Assert.Equal(typeof(Guid), id.PropertyType);
        Assert.False(id.CanWrite);

        var args = typeof(Playnite.SDK.Plugins.LibraryGetGamesArgs);
        var constructors = args.GetConstructors();
        Assert.Equal(2, constructors.Length);
        Assert.Contains(constructors, constructor => constructor.GetParameters().Length == 0);
        Assert.Contains(constructors, constructor => constructor.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .SequenceEqual(new[] { typeof(CancellationToken) }));
        var token = Assert.Single(args.GetProperties());
        Assert.Equal("CancelToken", token.Name);
        Assert.Equal(typeof(CancellationToken), token.PropertyType);
        Assert.False(token.SetMethod!.IsPublic);

        var nameAttribute = typeof(Playnite.SDK.Data.SerializationPropertyNameAttribute);
        Assert.Equal(typeof(string), Assert.Single(nameAttribute.GetConstructors()).GetParameters().Single().ParameterType);
        var propertyName = nameAttribute.GetProperty("PropertyName")!;
        Assert.Equal("PropertyName", propertyName.Name);
        Assert.Equal(typeof(string), propertyName.PropertyType);
        Assert.True(propertyName.CanRead && propertyName.CanWrite);

        Assert.Single(typeof(Playnite.SDK.Data.DontSerializeAttribute).GetConstructors());
        Assert.Single(typeof(Playnite.SDK.Events.OnApplicationStartedEventArgs).GetConstructors());
        Assert.Equal(typeof(Version), typeof(Playnite.SDK.SdkVersions).GetProperty("SDKVersion")!.PropertyType);

        Assert.Equal(
            new[] { ("Installed", 0), ("Uninstalled", 1) },
            Enum.GetValues<Playnite.SDK.Models.InstallationStatus>()
                .Select(value => (value.ToString(), (int)value))
                .ToArray());
        Assert.Equal(19, Enum.GetValues<Playnite.SDK.Plugins.MetadataField>().Length);
        Assert.Equal(0, (int)Playnite.SDK.Plugins.MetadataField.Name);
        Assert.Equal(18, (int)Playnite.SDK.Plugins.MetadataField.InstallSize);
    }

    [Fact]
    public void ApiCompat_report_classifies_every_v6_difference()
    {
        var differences = File.ReadAllLines(Docs("p6-008-apicompat-v6-v7.txt"))
            .Where(line => line.StartsWith("CP", StringComparison.Ordinal))
            .Select(line =>
            {
                var match = Regex.Match(line, @"^CP(?<code>\d{4}): Type '(?<type>[^']+)' exists on v6-baseline but not on v7-candidate$");
                Assert.True(match.Success, "Unrecognised ApiCompat line: " + line);
                return (Code: match.Groups["code"].Value, Type: match.Groups["type"].Value);
            })
            .ToArray();

        Assert.Equal(218, differences.Length);
        Assert.All(differences, difference => Assert.Equal("0001", difference.Code));

        var rows = File.ReadAllLines(Docs("p6-008-apicompat-classification.tsv"));
        Assert.Equal("v6_difference\tgroup\tclassification\tdecision", rows[0]);
        var classified = rows.Skip(1)
            .Select(row => row.Split('\t'))
            .Select(columns =>
            {
                Assert.Equal(4, columns.Length);
                return (Type: columns[0], Group: columns[1], Classification: columns[2], Decision: columns[3]);
            })
            .ToArray();

        Assert.Equal(differences.Select(d => d.Type).Order(StringComparer.Ordinal),
            classified.Select(c => c.Type).Order(StringComparer.Ordinal));
        Assert.Equal(classified.Length, classified.Select(c => c.Type).Distinct(StringComparer.Ordinal).Count());
        Assert.All(classified, row =>
        {
            Assert.Contains(row.Classification, AllowedClassifications);
            Assert.False(string.IsNullOrWhiteSpace(row.Group));
            Assert.False(string.IsNullOrWhiteSpace(row.Decision));
        });
        Assert.DoesNotContain(classified, row => row.Classification == "breaking");
    }

    [Fact]
    public void ApiCompat_report_cites_the_v6_baseline_and_the_tool()
    {
        var report = File.ReadAllText(Docs("p6-008-sdk-freeze.md"));

        Assert.Contains("PlayniteSDK 6.18.0", report, StringComparison.Ordinal);
        Assert.Contains("63ae943bf226a782738d3d6b9977dcb77861e5d2ea1aadcf620332618c93e236", report, StringComparison.Ordinal);
        Assert.Contains("ce7511941aba4d4b17dd7b226fadfd0ecefdf51ab73775b25631089fd7542fc7", report, StringComparison.Ordinal);
        Assert.Contains("Microsoft.DotNet.ApiCompat 11.0.100", report, StringComparison.Ordinal);
        Assert.Contains("CP0001", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_SDK_interface_has_a_fake_host_contract_test()
    {
        var expected = Abstractions.GetExportedTypes()
            .Where(type => type.IsInterface)
            .Concat(typeof(INotificationsAPI).Assembly.GetExportedTypes()
                .Where(type => type.IsInterface && type.Namespace is
                    "Playnite.Core.Hosting" or "Playnite.Core.UriHandling" or "Playnite.Core.Descriptions"))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var rows = File.ReadAllLines(Docs("p6-008-interface-coverage.tsv"));
        Assert.Equal("interface\tfake\ttest\tfile", rows[0]);
        var coverage = rows.Skip(1)
            .Select(row => row.Split('\t'))
            .Select(columns =>
            {
                Assert.Equal(4, columns.Length);
                return (Interface: columns[0], Fake: columns[1], Test: columns[2], File: columns[3]);
            })
            .ToArray();

        Assert.Equal(expected, coverage.Select(row => row.Interface).Order(StringComparer.Ordinal));
        Assert.Equal(coverage.Length, coverage.Select(row => row.Interface).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(12, coverage.Length);

        foreach (var row in coverage)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Fake));
            Assert.False(string.IsNullOrWhiteSpace(row.Test));
            var path = Path.Combine(ProductGraph.Root, row.File);
            Assert.True(File.Exists(path), "Missing contract test file: " + row.File);
            var source = File.ReadAllText(path);
            Assert.Contains("class " + row.Fake, source, StringComparison.Ordinal);
            Assert.Contains(row.Test + "(", source, StringComparison.Ordinal);
            var interfaceName = row.Interface[(row.Interface.LastIndexOf('.') + 1)..];
            Assert.Contains(interfaceName, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Core_and_abstractions_have_no_UI_dependency()
    {
        var projects = ProductGraph.Products
            .Where(project => project.Name is "Playnite.Core" or "Playnite.SDK.Abstractions")
            .ToArray();
        Assert.Equal(2, projects.Length);

        foreach (var project in projects)
        {
            var violations = SdkBoundaryPolicy.GraphViolations(
                project.Name,
                project.Values("ProjectReference"),
                project.Dependencies()).ToArray();
            Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
            Assert.DoesNotContain(project.Dependencies(),
                dependency => dependency.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(project.Dependencies(), dependency => dependency is
                "Playnite.SDK" or "Playnite.Desktop" or "Playnite.Platform" or "Playnite.Platform.Linux");
        }

        var core = projects.Single(project => project.Name == "Playnite.Core");
        var abstractions = projects.Single(project => project.Name == "Playnite.SDK.Abstractions");
        Assert.Equal(["Playnite.SDK.Abstractions"],
            core.Dependencies()
                .Where(dependency => dependency.StartsWith("Playnite.", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(abstractions.Dependencies(),
            dependency => dependency.StartsWith("Playnite.", StringComparison.Ordinal));
    }

    private static string[] Declarations(string fileName)
    {
        return File.ReadAllLines(Path.Combine(ProductGraph.Root, "src", "Playnite.SDK.Abstractions", fileName))
            .Where(line => line.Length > 0 && line != "#nullable enable")
            .ToArray();
    }
}
