using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Playnite.Plugin.Tests;

/// <summary>Loads each sample assembly once from the test output directory.</summary>
internal static class SampleAssemblies
{
    private static readonly ConcurrentDictionary<string, Assembly> Loaded = new(StringComparer.Ordinal);

    internal static IReadOnlyList<string> Names { get; } =
    [
        "Playnite.Sample.HelloWorld",
        "Playnite.Sample.LibraryProvider",
        "Playnite.Sample.MetadataProvider",
    ];

    internal static string PathFor(string name) =>
        Path.Combine(AppContext.BaseDirectory, name + ".dll");

    internal static Assembly Load(string name) =>
        Loaded.GetOrAdd(name, sample => AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(PathFor(sample))));
}
