using Microsoft.CodeAnalysis;

namespace Playnite.Architecture.Tests;

internal static class SdkBoundaryPolicy
{
    private static readonly string[] WindowsFamilies =
    [
        "Microsoft.Win32", "System.Drawing", "System.Management", "System.ServiceProcess",
        "System.Windows", "WindowsBase", "PresentationCore", "PresentationFramework",
        "System.Xaml", "System.IO.Packaging", "System.Windows.Forms", "Microsoft.UI",
        "Windows.UI", "WindowsAppSDK", "Microsoft.WindowsAppSDK", "Microsoft.WindowsDesktop.App"
    ];

    internal static IEnumerable<string> GraphViolations(string project, IEnumerable<string> direct, IEnumerable<string> closure)
    {
        if (project == "Playnite.Core")
        {
            if (!direct.Contains("Playnite.SDK.Abstractions"))
            {
                yield return "Core must directly reference Playnite.SDK.Abstractions";
            }

            if (direct.Concat(closure).Contains("Playnite.SDK", StringComparer.OrdinalIgnoreCase))
            {
                yield return "Core cannot reference the UI SDK, even transitively";
            }

            foreach (var dependency in direct.Concat(closure).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(dependency => dependency.StartsWith("Playnite.", StringComparison.OrdinalIgnoreCase)))
            {
                if (!ArchitectureTable.For(project).AllowedProjects.Contains(dependency, StringComparer.OrdinalIgnoreCase))
                {
                    yield return $"Core resolved Playnite dependency {dependency} is not in the architecture table's allowed project list";
                }
            }
        }

        if (project is "Playnite.Core" or "Playnite.SDK.Abstractions")
        {
            foreach (var dependency in direct.Concat(closure).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (IsFamily(dependency, "Avalonia") || WindowsFamilies.Any(family => IsFamily(dependency, family))
                    || project == "Playnite.SDK.Abstractions" && dependency.StartsWith("Playnite.", StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"{project} cannot reference {dependency}, even transitively";
                }
            }
        }
    }

    internal static IEnumerable<string> BuildInputViolations(string project, string useWpf, string useWindowsForms, IEnumerable<string> frameworks)
    {
        if (string.Equals(useWpf, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(useWindowsForms, "true", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{project} cannot enable UseWPF or UseWindowsForms";
        }

        foreach (var framework in frameworks.Where(framework => framework != "Microsoft.NETCore.App"))
        {
            yield return $"{project} framework reference {framework} is not allowed in the UI-free boundary";
        }
    }

    internal static string? UiSymbolViolation(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace.ToDisplayString();
        var assembly = type.ContainingAssembly.Name;
        return IsFamily(ns, "Avalonia") || IsFamily(ns, "Playnite.Platform")
            || IsFamily(ns, "Playnite.SDK.Controls") || assembly.Equals("Playnite.SDK", StringComparison.OrdinalIgnoreCase)
            || IsFamily(assembly, "Avalonia") || IsFamily(assembly, "Playnite.Platform") || IsFamily(assembly, "Playnite.SDK.Controls")
            || type.ToDisplayString() != "System.Windows.Input.ICommand" && WindowsFamilies.Any(family => IsFamily(ns, family) || IsFamily(assembly, family))
            ? $"UI/platform type in SDK Abstractions: {type}"
            : null;
    }

    private static bool IsFamily(string name, string prefix) => name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
}
