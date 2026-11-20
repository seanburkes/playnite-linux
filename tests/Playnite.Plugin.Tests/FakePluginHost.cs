using System.Reflection;
using System.Runtime.Loader;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace Playnite.Plugin.Tests;

/// <summary>
/// Loads sample plugin assemblies by reflection and exposes the SDK extension points the tests
/// invoke. The discovery contract is <see cref="IPlayniteExtension"/> from <c>Playnite.SDK</c>;
/// the host knows nothing about any sample type.
/// </summary>
public sealed class FakePluginHost
{
    private readonly List<IPlayniteExtension> extensions = [];

    /// <summary>Gets the discovered extensions.</summary>
    public IReadOnlyList<IPlayniteExtension> Extensions => extensions;

    /// <summary>Loads assemblies from the supplied paths and discovers their extensions.</summary>
    public static FakePluginHost Load(params string[] assemblyPaths)
    {
        var host = new FakePluginHost();
        foreach (var path in assemblyPaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Sample assembly was not built.", fullPath);
            }

            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
            foreach (var type in assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && typeof(IPlayniteExtension).IsAssignableFrom(type)))
            {
                if (Activator.CreateInstance(type) is not IPlayniteExtension extension)
                {
                    throw new InvalidOperationException($"{type.FullName} could not be constructed.");
                }

                host.extensions.Add(extension);
            }
        }

        return host;
    }

    /// <summary>Gets the single discovered extension of the requested SDK type.</summary>
    public T Find<T>()
        where T : class, IPlayniteExtension
    {
        return extensions.OfType<T>().Single();
    }

    /// <summary>Invokes the start extension point on every discovered plugin.</summary>
    public IReadOnlyList<Playnite.SDK.Plugins.Plugin> Start()
    {
        var started = new List<Playnite.SDK.Plugins.Plugin>();
        foreach (var plugin in extensions.OfType<Playnite.SDK.Plugins.Plugin>())
        {
            plugin.OnApplicationStarted(new OnApplicationStartedEventArgs());
            started.Add(plugin);
        }

        return started;
    }
}
