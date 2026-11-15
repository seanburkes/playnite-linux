using System.Reflection;
using System.Text.Json;

namespace Playnite.Architecture.Tests;

internal sealed class ProductGraph
{
    internal static readonly string Root = FindRoot();
    internal static readonly string Configuration = typeof(ProductGraph).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "BuildConfiguration").Value!;

    internal static readonly IReadOnlyDictionary<string, string[]> AllowedProjects =
        new Dictionary<string, string[]>
        {
            ["Playnite.SDK.Abstractions"] = [],
            ["Playnite.SDK"] = ["Playnite.SDK.Abstractions"],
            ["Playnite.Core"] = ["Playnite.SDK.Abstractions"],
            ["Playnite.Platform"] = ["Playnite.Core", "Playnite.SDK.Abstractions"],
            ["Playnite.Platform.Linux"] = ["Playnite.Platform", "Playnite.Core"],
            ["Playnite.Steam"] = ["Playnite.Core", "Playnite.Platform"],
            ["Playnite.LegacyImport.Contracts"] = [],
            ["Playnite.LegacyImport"] = ["Playnite.LegacyImport.Contracts"],
            ["Playnite.Desktop"] = ["Playnite.Core", "Playnite.SDK", "Playnite.Platform", "Playnite.Steam", "Playnite.LegacyImport.Contracts"],
            ["Playnite.Cli"] = ["Playnite.Core", "Playnite.Platform", "Playnite.Steam", "Playnite.LegacyImport.Contracts"]
        };

    internal static IEnumerable<ProductGraph> Products => Directory.EnumerateFiles(
        Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories).Select(p => new ProductGraph(p));

    internal string ProjectFile { get; }
    internal string Name => Path.GetFileNameWithoutExtension(ProjectFile);
    private readonly Dictionary<string, string[]> inputs;

    internal ProductGraph(string projectFile)
    {
        ProjectFile = projectFile;
        var snapshot = Path.Combine(Path.GetDirectoryName(projectFile)!, "obj", "architecture", Configuration, "inputs.txt");
        if (!File.Exists(snapshot))
        {
            throw new InvalidOperationException($"Build {projectFile} ({Configuration}) before architecture tests; missing {snapshot}");
        }

        inputs = File.ReadAllLines(snapshot).Select(line => line.Split('|', 2))
            .GroupBy(parts => parts[0]).ToDictionary(g => g.Key, g => g.Select(p => p[1]).ToArray());
    }

    internal string[] Values(string kind) => inputs.GetValueOrDefault(kind, []);

    internal string[] Dependencies()
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(ProjectFile)!, "obj", "project.assets.json")));
        return Values("Dependency")
            .Concat(Assembly.LoadFrom(Values("Output").Single()).GetReferencedAssemblies().Select(a => a.Name!))
            .Concat(assets.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name.Split('/')[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal string[] DirectPackages()
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(ProjectFile)!, "obj", "project.assets.json")));
        return assets.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject()
            .SelectMany(f => f.Value.TryGetProperty("dependencies", out var deps)
                ? deps.EnumerateObject().Select(p => p.Name) : []).Distinct().ToArray();
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Playnite.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Cannot locate Playnite.slnx from architecture test output.");
    }
}
