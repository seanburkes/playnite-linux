using Playnite.Core.Addons;
using Playnite.Core.Manifests;
using Xunit;

namespace Playnite.Core.Tests.Addons;

public sealed class AddonInstallationTests
{
    [Fact]
    public void Install_copies_an_add_on_into_a_stable_id_and_version_directory()
    {
        using var temp = new TempDirectory();
        var source = CreatePackage(temp, "Test_Plugin", "1.0");
        var root = Path.Combine(temp.FullPath, "Extensions");
        var installer = new AddonInstaller(new AddonInstallLayout(root));

        var result = installer.InstallExtension(source);

        Assert.True(result.Succeeded, Describe(result.Diagnostics));
        Assert.Equal(Path.Combine(root, "Test_Plugin", "1.0"), result.Directory);
        Assert.True(File.Exists(Path.Combine(result.Directory!, "extension.yaml")));
        Assert.True(File.Exists(Path.Combine(result.Directory!, "Plugin.dll")));
        Assert.True(File.Exists(Path.Combine(result.Directory!, "data", "config.json")));
        var installed = Assert.Single(installer.Layout.GetInstalled());
        Assert.Equal("Test_Plugin", installed.AddonId);
        Assert.Equal(new Version(1, 0), installed.Version);
        Assert.Equal(Path.Combine(result.Directory!, "extension.yaml"), installed.ManifestPath);
    }

    [Fact]
    public void Install_is_deterministic_across_extensions_roots()
    {
        using var temp = new TempDirectory();
        var source = CreatePackage(temp, "Test_Plugin", "1.0");
        var firstRoot = Path.Combine(temp.FullPath, "root-a");
        var secondRoot = Path.Combine(temp.FullPath, "root-b");

        var first = new AddonInstaller(new AddonInstallLayout(firstRoot)).InstallExtension(source);
        var second = new AddonInstaller(new AddonInstallLayout(secondRoot)).InstallExtension(source);

        Assert.True(first.Succeeded, Describe(first.Diagnostics));
        Assert.True(second.Succeeded, Describe(second.Diagnostics));
        Assert.Equal(
            Path.GetRelativePath(firstRoot, first.Directory!),
            Path.GetRelativePath(secondRoot, second.Directory!));
        Assert.Equal(RelativeFiles(firstRoot), RelativeFiles(secondRoot));
        foreach (var relative in RelativeFiles(firstRoot))
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(firstRoot, relative)),
                File.ReadAllBytes(Path.Combine(secondRoot, relative)));
        }
    }

    [Fact]
    public void Uninstall_removes_every_installed_file_and_leaves_no_residue()
    {
        using var temp = new TempDirectory();
        var source = CreatePackage(temp, "Test_Plugin", "1.0");
        var root = Path.Combine(temp.FullPath, "Extensions");
        var installer = new AddonInstaller(new AddonInstallLayout(root));
        var install = installer.InstallExtension(source);
        Assert.True(install.Succeeded, Describe(install.Diagnostics));

        var uninstall = installer.Uninstall("Test_Plugin");

        Assert.True(uninstall.Succeeded, Describe(uninstall.Diagnostics));
        Assert.Equal(Path.Combine(root, "Test_Plugin"), uninstall.Directory);
        Assert.False(Directory.Exists(uninstall.Directory!));
        AssertNoResidue(root);
        Assert.Empty(installer.Layout.GetInstalled());
    }

    [Fact]
    public void Install_rejects_a_duplicate_add_on_id()
    {
        using var temp = new TempDirectory();
        var source = CreatePackage(temp, "Test_Plugin", "1.0");
        var root = Path.Combine(temp.FullPath, "Extensions");
        var installer = new AddonInstaller(new AddonInstallLayout(root));
        var first = installer.InstallExtension(source);
        Assert.True(first.Succeeded, Describe(first.Diagnostics));

        var duplicate = installer.InstallExtension(source);

        Assert.False(duplicate.Succeeded);
        Assert.Equal("duplicate-addon", duplicate.Diagnostic!.Code);
        Assert.Equal("$.Id", duplicate.Diagnostic.Path);
        Assert.True(File.Exists(Path.Combine(first.Directory!, "Plugin.dll")));
        Assert.Single(installer.Layout.GetInstalled());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/tmp/escape")]
    [InlineData("..")]
    [InlineData("a\\b")]
    public void Install_rejects_path_traversal_in_the_add_on_id(string id)
    {
        using var temp = new TempDirectory();
        var source = CreatePackage(temp, id, "1.0");
        var root = Path.Combine(temp.FullPath, "Extensions");
        var installer = new AddonInstaller(new AddonInstallLayout(root));

        var result = installer.InstallExtension(source);

        Assert.False(result.Succeeded);
        Assert.Equal(AddonManifestValidator.UnsafeIdCode, result.Diagnostic!.Code);
        AssertNoResidue(root);
        Assert.False(Directory.Exists(Path.Combine(temp.FullPath, "escape")));
    }

    [Theory]
    [InlineData("../../evil.dll")]
    [InlineData("/usr/lib/evil.dll")]
    [InlineData("lib/../../evil.dll")]
    public void Install_rejects_a_module_that_escapes_the_install_directory(string module)
    {
        using var temp = new TempDirectory();
        var source = CreatePackage(temp, "Test_Plugin", "1.0", module);
        var root = Path.Combine(temp.FullPath, "Extensions");
        var installer = new AddonInstaller(new AddonInstallLayout(root));

        var result = installer.InstallExtension(source);

        Assert.False(result.Succeeded);
        Assert.Equal(AddonManifestValidator.UnsafeModuleCode, result.Diagnostic!.Code);
        Assert.Equal("$.Module", result.Diagnostic.Path);
        AssertNoResidue(root);
    }

    [Fact]
    public void Install_rejects_a_manifest_version_that_differs_from_the_requested_version()
    {
        using var temp = new TempDirectory();
        var source = CreatePackage(temp, "Test_Plugin", "1.0");
        var root = Path.Combine(temp.FullPath, "Extensions");
        var installer = new AddonInstaller(new AddonInstallLayout(root));

        var result = installer.InstallExtension(source, new Version(2, 0));

        Assert.False(result.Succeeded);
        Assert.Equal("version-mismatch", result.Diagnostic!.Code);
        Assert.Equal("$.Version", result.Diagnostic.Path);
        AssertNoResidue(root);
    }

    [Fact]
    public void Install_rejects_a_symbolic_link_in_the_package()
    {
        using var temp = new TempDirectory();
        var source = CreatePackage(temp, "Test_Plugin", "1.0");
        var outside = Path.Combine(temp.FullPath, "outside.dll");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(source, "linked.dll"), outside);
        var root = Path.Combine(temp.FullPath, "Extensions");
        var installer = new AddonInstaller(new AddonInstallLayout(root));

        var result = installer.InstallExtension(source);

        Assert.False(result.Succeeded);
        Assert.Equal("symlink", result.Diagnostic!.Code);
        AssertNoResidue(root);
    }

    [Fact]
    public void Uninstall_refuses_an_id_that_is_not_one_safe_segment()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.FullPath, "Extensions");
        var installer = new AddonInstaller(new AddonInstallLayout(root));

        var result = installer.Uninstall("../escape");

        Assert.False(result.Succeeded);
        Assert.Equal(AddonManifestValidator.UnsafeIdCode, result.Diagnostic!.Code);
        AssertNoResidue(root);
    }

    [Fact]
    public void Themes_use_the_same_id_and_version_layout()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.FullPath, "theme");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "theme.yaml"),
            "Id: 'Test_Theme'\nName: Test\nAuthor: Test\nVersion: 1.0\nMode: Fullscreen\nThemeApiVersion: 2.0.0\n");
        File.WriteAllText(Path.Combine(source, "index.xaml"), "<Theme />");
        var root = Path.Combine(temp.FullPath, "Extensions");
        var installer = new AddonInstaller(new AddonInstallLayout(root));

        var result = installer.InstallTheme(source);

        Assert.True(result.Succeeded, Describe(result.Diagnostics));
        Assert.Equal(Path.Combine(root, "Test_Theme", "1.0"), result.Directory);
        var installed = Assert.Single(installer.Layout.GetInstalled());
        Assert.Equal(Path.Combine(result.Directory!, "theme.yaml"), installed.ManifestPath);
        Assert.True(File.Exists(Path.Combine(result.Directory!, "index.xaml")));
    }

    [Fact]
    public void Installed_add_on_resolves_its_manifest_and_module_under_the_discovery_root()
    {
        using var temp = new TempDirectory();
        var source = CreatePackage(temp, "Test_Plugin", "1.0");
        var root = Path.Combine(temp.FullPath, "Extensions");
        var installer = new AddonInstaller(new AddonInstallLayout(root));
        var install = installer.InstallExtension(source);
        Assert.True(install.Succeeded, Describe(install.Diagnostics));

        // v10 ExtensionFactory and the p6-009 preview host discover extension.yaml under the
        // extensions root and then load the module's Playnite.SDK.Plugins.IPlayniteExtension
        // implementation. This unit stops at the resolved directory; it never loads or
        // executes package code.
        var installed = Assert.Single(installer.Layout.GetInstalled());
        var manifest = ManifestSerialization.FromYaml<ExtensionManifest>(
            File.ReadAllText(installed.ManifestPath));
        var module = Path.GetFullPath(Path.Combine(installed.Directory, manifest.Module!));

        Assert.True(File.Exists(module));
        Assert.StartsWith(installed.Directory + Path.DirectorySeparatorChar, module, StringComparison.Ordinal);
    }

    private static string CreatePackage(
        TempDirectory temp,
        string id,
        string version,
        string module = "Plugin.dll",
        string type = "GenericPlugin")
    {
        var source = Path.Combine(temp.FullPath, "package-" + Path.GetRandomFileName());
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "extension.yaml"),
            $"Id: '{id}'\nName: Test\nAuthor: Test\nVersion: {version}\nModule: '{module}'\nType: {type}\n");
        File.WriteAllText(Path.Combine(source, "Plugin.dll"), "plugin");
        Directory.CreateDirectory(Path.Combine(source, "data"));
        File.WriteAllText(Path.Combine(source, "data", "config.json"), "{}");
        return source;
    }

    private static string[] RelativeFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    private static void AssertNoResidue(string root)
    {
        if (Directory.Exists(root))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
    }

    private static string Describe(IEnumerable<AddonDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            FullPath = Path.Combine(Path.GetTempPath(), "playnite-p6-010", Path.GetRandomFileName());
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(FullPath))
            {
                Directory.Delete(FullPath, recursive: true);
            }
        }
    }
}
