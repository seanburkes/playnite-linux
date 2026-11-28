using System.IO.Compression;
using Playnite.Core.Addons;
using Playnite.Core.Plugins;
using Playnite.Platform.Linux;
using Xunit;

namespace Playnite.Core.Tests.Addons;

public sealed class AddonPackageLifecycleTests
{
    [Fact]
    public async Task Folder_and_archive_versions_are_published_one_at_a_time_and_can_be_rolled_back_and_removed()
    {
        var token = TestContext.Current.CancellationToken;
        using var files = new PackageFiles();
        using var host = new PluginHost(PluginSurface.Desktop);
        var original = files.CreateFolder("1.0");
        var archive = Path.Combine(files.Root, "update.pext");
        ZipFile.CreateFromDirectory(files.CreateFolder("2.0"), archive);

        await files.Installer.InstallPackageAsync(original, host, updateId: null, token);
        Assert.Empty(host.GetRecords());
        Assert.Equal(new Version(1, 0), files.ActiveVersion());

        var update = await files.Installer.InstallPackageAsync(archive, host, "test.addon", token);
        Assert.Equal(new Version(1, 0), update.PreviousVersion);
        Assert.Equal(new Version(2, 0), files.ActiveVersion());
        var oldUpdatePath = Assert.Single(files.Installer.GetActivePackageDirectories()).Directory;

        Assert.Null((await files.Installer.RollBackPackageAsync("test.addon", host, token)).PreviousVersion);
        Assert.Equal(new Version(1, 0), files.ActiveVersion());

        await files.Installer.InstallPackageAsync(archive, host, "test.addon", token);
        var newUpdatePath = Assert.Single(files.Installer.GetActivePackageDirectories()).Directory;
        Assert.NotEqual(oldUpdatePath, newUpdatePath);
        Assert.True(File.Exists(Path.Combine(oldUpdatePath, "Plugin.dll")));

        await files.Installer.UninstallPackageAsync("test.addon", token);
        Assert.Empty(files.Installer.GetPackages());
        Assert.Null(files.ActiveVersion());
        Assert.True(File.Exists(Path.Combine(newUpdatePath, "Plugin.dll")));
        using (files.Installer.AcquirePackageUsage())
        {
            files.Installer.CleanRetiredPackages((id, error) => Assert.Fail($"{id}: {error}"));
            Assert.True(File.Exists(Path.Combine(newUpdatePath, "Plugin.dll")));
        }
        files.Installer.CleanRetiredPackages((id, error) => Assert.Fail($"{id}: {error}"));
        Assert.False(Directory.Exists(Path.Combine(files.Plugins, AddonInstaller.PackageStoreName, "test.addon")));
    }

    [Fact]
    public async Task An_invalid_state_record_does_not_hide_another_package()
    {
        var token = TestContext.Current.CancellationToken;
        using var files = new PackageFiles();
        using var host = new PluginHost(PluginSurface.Desktop);
        await files.Installer.InstallPackageAsync(files.CreateFolder("1.0"), host, updateId: null, token);
        var broken = Directory.CreateDirectory(Path.Combine(files.Plugins, AddonInstaller.PackageStoreName, "broken"));
        File.WriteAllText(Path.Combine(broken.FullName, "versions"), "not a state record");
        var brokenIds = new List<string>();

        Assert.Equal("test.addon", Assert.Single(files.Installer.GetPackages((id, _) => brokenIds.Add(id))).Id);
        Assert.Equal("broken", Assert.Single(brokenIds));
    }

    [Fact]
    public async Task Archive_entry_count_is_checked_against_the_directory()
    {
        var token = TestContext.Current.CancellationToken;
        using var files = new PackageFiles();
        using var host = new PluginHost(PluginSurface.Desktop);
        var archive = Path.Combine(files.Root, "bad-count.zip");
        ZipFile.CreateFromDirectory(files.CreateFolder("1.0"), archive);
        var bytes = await File.ReadAllBytesAsync(archive, token);
        Array.Clear(bytes, bytes.Length - 22 + 8, 4);
        await File.WriteAllBytesAsync(archive, bytes, token);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => files.Installer.InstallPackageAsync(archive, host, updateId: null, token));
        Assert.Contains("more entries than declared", error.Message);
        Assert.Empty(files.Installer.GetPackages());
    }

    [Fact]
    public async Task Inconsistent_last_zip_end_record_is_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        using var files = new PackageFiles();
        using var host = new PluginHost(PluginSurface.Desktop);
        var archive = Path.Combine(files.Root, "wrong-end.zip");
        ZipFile.CreateFromDirectory(files.CreateFolder("1.0"), archive);
        var bytes = await File.ReadAllBytesAsync(archive, token);
        bytes[^2] = 22; // The real end record describes a comment containing the second signature.
        var trailer = new byte[22];
        new byte[] { 0x50, 0x4b, 0x05, 0x06 }.CopyTo(trailer, 0);
        trailer[^2] = 1; // But the second signature cannot describe a valid end record.
        await File.WriteAllBytesAsync(archive, [.. bytes, .. trailer], token);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => files.Installer.InstallPackageAsync(archive, host, updateId: null, token));
        Assert.Empty(files.Installer.GetPackages());
    }

    [Fact]
    public async Task Incompatible_update_leaves_the_current_version_available()
    {
        var token = TestContext.Current.CancellationToken;
        using var files = new PackageFiles();
        using var host = new PluginHost(PluginSurface.Desktop);
        await files.Installer.InstallPackageAsync(files.CreateFolder("1.0"), host, updateId: null, token);

        var incompatible = files.CreateFolder("2.0", surface: "fullscreen");
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => files.Installer.InstallPackageAsync(incompatible, host, "test.addon", token));

        Assert.Contains("compatible", error.Message);
        Assert.Equal(new Version(1, 0), files.ActiveVersion());
        Assert.Null(Assert.Single(files.Installer.GetPackages()).PreviousVersion);
    }

    [Fact]
    public async Task Missing_declared_entry_point_is_rejected_even_for_another_surface()
    {
        var token = TestContext.Current.CancellationToken;
        using var files = new PackageFiles();
        using var host = new PluginHost(PluginSurface.Desktop);
        var source = files.CreateFolder("1.0");
        File.WriteAllText(Path.Combine(source, "plugin.json"), """
            {"id":"test.addon","name":"Test","version":"1.0","surfaces":["desktop","fullscreen"],
             "entryPoints":{"desktop":"Plugin.dll","fullscreen":"Missing.dll"}}
            """);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => files.Installer.InstallPackageAsync(source, host, updateId: null, token));

        Assert.Contains("Missing.dll", error.Message);
        Assert.Empty(files.Installer.GetPackages());
    }

    [Fact]
    public async Task Cancelled_install_never_publishes_the_package()
    {
        using var files = new PackageFiles();
        using var host = new PluginHost(PluginSurface.Desktop);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => files.Installer.InstallPackageAsync(files.CreateFolder("1.0"), host, updateId: null, cancellation.Token));

        Assert.False(Directory.Exists(files.Plugins));
    }

    private sealed class PackageFiles : IDisposable
    {
        private readonly LinuxRegularFileProbe probe = new();

        public PackageFiles()
        {
            Root = Directory.CreateTempSubdirectory("playnite-addon-lifecycle-").FullName;
            Plugins = Path.Combine(Root, "plugins");
            Installer = new AddonInstaller(new AddonInstallLayout(Plugins), probe);
        }

        public string Root { get; }
        public string Plugins { get; }
        public AddonInstaller Installer { get; }

        public string CreateFolder(string version, string surface = "desktop")
        {
            var folder = Directory.CreateDirectory(Path.Combine(Root, version)).FullName;
            File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
                {"id":"test.addon","name":"Test","version":"{{version}}","surfaces":["{{surface}}"],
                 "entryPoints":{"{{surface}}":"Plugin.dll"}
                }
                """);
            File.WriteAllText(Path.Combine(folder, "Plugin.dll"), "Not loaded during discovery");
            return folder;
        }

        public Version? ActiveVersion()
        {
            var active = Installer.GetActivePackageDirectories().ToArray();
            return active.Length == 0 ? null : Installer.ReadPackageDescriptor(Assert.Single(active).Directory).Version;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
