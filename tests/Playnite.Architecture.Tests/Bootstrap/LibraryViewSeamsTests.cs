using System.Diagnostics;
using System.Reflection;
using Playnite.Desktop.Shell;
using Xunit;

namespace Playnite.Desktop.Tests;

/// <summary>p7-003 library view-model seams over the real p6-001/p6-012 service.</summary>
public sealed class LibraryViewSeamsTests
{
    [Fact]
    public async Task A_new_app_owned_library_starts_empty_without_reading_the_fixture()
    {
        using var temp = new TemporaryLibrary();
        var model = new LibraryViewModel(Path.Combine(temp.Root, "playnite", "library.sqlite"));

        await model.LoadAsync();

        Assert.Equal("empty", model.Status);
        Assert.Equal(0, model.TotalCount);
        Assert.Empty(model.Rows);
        Assert.Null(model.Selected);
        Assert.True(File.Exists(Path.Combine(temp.Root, "playnite", "library.sqlite")));
    }

    [Fact]
    public async Task Fixture_imported_rows_and_details_flow_through_bounded_stable_pages()
    {
        using var temp = new TemporaryLibrary();
        var destination = Path.Combine(temp.Root, "playnite", "library.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await ImportFixtureAsync(destination);

        var model = new LibraryViewModel(destination);
        await model.LoadAsync();

        Assert.Equal("loaded", model.Status);
        Assert.Equal(3, model.TotalCount);
        Assert.Equal(3, model.Rows.Count);
        Assert.Equal("Synthetic Steam Installed", model.Rows[0].Name);
        Assert.Equal("220", model.Selected?.GameId);

        var firstIds = model.Rows.Select(row => row.Id).ToArray();
        await model.LoadPageAsync(1);
        Assert.Equal(2, model.Rows.Count);
        await model.LoadPageAsync(0);
        Assert.Equal(firstIds, model.Rows.Select(row => row.Id).ToArray());
        Assert.Equal("220", model.Selected?.GameId);
    }

    [Fact]
    public void The_view_model_is_instance_owned_without_a_service_locator()
    {
        var constructor = Assert.Single(typeof(LibraryViewModel).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Single(parameters);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IServiceProvider));
    }

    private static async Task ImportFixtureAsync(string destination)
    {
        var configuration = typeof(LibraryViewSeamsTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        string Product(string name) => RepositoryPath("src", name, "bin", configuration, "net11.0", name + ".dll");

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     Product("Playnite.Cli"), "import-v10", Product("Playnite.LegacyImport"),
                     RepositoryPath("tests", "fixtures", "v10", "library"), destination,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException("Could not start the library importer.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(deadline.Token);
        Assert.True(process.ExitCode == 0, await errors);
        Assert.Contains("\"ImportedSteam\":3", await output);
    }

    private static string RepositoryPath(params string[] segments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Playnite.slnx")))
        {
            root = root.Parent;
        }

        return Path.Combine([root?.FullName ?? throw new InvalidOperationException("Cannot locate Playnite.slnx"), .. segments]);
    }

    private sealed class TemporaryLibrary : IDisposable
    {
        internal TemporaryLibrary() => Root = Path.Combine(Path.GetTempPath(), "playnite-p7-003-" + Path.GetRandomFileName());

        internal string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
