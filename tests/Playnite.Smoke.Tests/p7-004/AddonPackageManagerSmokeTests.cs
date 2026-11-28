using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Playnite.Core.Addons;
using Playnite.Core.Plugins;
using Playnite.Desktop.Plugins;
using Playnite.Desktop.Shell;
using Playnite.Platform.Linux;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class AddonPackageManagerSmokeTests
{
    [Fact]
    public async Task Managed_addon_can_be_disabled_rolled_back_and_uninstalled_from_the_window()
    {
        var token = TestContext.Current.CancellationToken;
        var folder = Directory.CreateTempSubdirectory("playnite-addon-manager-");
        try
        {
            const string id = "smoke.external";
            var database = Path.Combine(folder.FullName, "library.sqlite");
            var pluginsRoot = Path.Combine(folder.FullName, "plugins");
            var installer = new AddonInstaller(new AddonInstallLayout(pluginsRoot), new LinuxRegularFileProbe());
            var source = Directory.CreateDirectory(Path.Combine(folder.FullName, "source")).FullName;
            var manifest = Path.Combine(source, "plugin.json");
            var document = $$"""
                {"id":"{{id}}","name":"Managed Smoke Plugin","version":"1.0.0",
                 "sourceId":"{{SmokeExternalPlugin.ExternalSourceId:D}}",
                 "surfaces":["desktop"],"capabilities":["librarySource","launch"],
                 "entryPoints":{"desktop":"Plugin.dll","entryType":"{{typeof(SmokeExternalPlugin).FullName}}"}
                }
                """;
            File.WriteAllText(manifest, document);
            File.Copy(typeof(SmokeExternalPlugin).Assembly.Location, Path.Combine(source, "Plugin.dll"));

            using (var setup = DesktopPluginHostFactory.CreateForLibrary(database, externalPluginsRoot: pluginsRoot))
            {
                await installer.InstallPackageAsync(source, setup, updateId: null, token);
                File.WriteAllText(manifest, document.Replace("1.0.0", "2.0.0", StringComparison.Ordinal));
                await installer.InstallPackageAsync(source, setup, id, token);
            }

            using var host = DesktopPluginHostFactory.CreateForLibrary(database, externalPluginsRoot: pluginsRoot);
            await using var ui = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
            await ui.Dispatch(() =>
            {
                var window = new PluginManagerWindow(host, themeManager: null, installer: installer);
                var plugins = window.FindControl<ListBox>("PluginList")!;
                plugins.SelectedItem = Assert.Single(plugins.Items.OfType<ListBoxItem>(), item => Equals(id, item.Tag));
                window.FindControl<CheckBox>("EnabledToggle")!.IsChecked = false;
                Assert.Equal(PluginLifecycleState.Disabled, Assert.Single(host.GetRecords(), record => record.Descriptor.Id == id).State);

                window.FindControl<TabControl>("ManagerTabs")!.SelectedIndex = 2;
                var list = window.FindControl<ListBox>("PackageList")!;
                var rollback = window.FindControl<Button>("RollbackPackageButton")!;
                Assert.True(rollback.IsEnabled);
                ClickAndWait(window, rollback, "Restored", token);
                Assert.Equal(new Version(1, 0, 0), Assert.IsType<ManagedAddonPackage>(list.SelectedItem).Version);
                Assert.False(rollback.IsEnabled);
                Assert.Contains("Restart Playnite", window.FindControl<TextBlock>("PackageStatus")!.Text);
            }, token);

            using var restarted = DesktopPluginHostFactory.CreateForLibrary(database, externalPluginsRoot: pluginsRoot);
            var restored = Assert.Single(restarted.GetRecords(), record => record.Descriptor.Id == id);
            Assert.Equal(new Version(1, 0, 0), restored.Descriptor.Version);
            Assert.Equal(PluginLifecycleState.Disabled, restored.State);

            using var enabledHost = DesktopPluginHostFactory.CreateForLibrary(database,
                settingsStore: new InMemoryPluginSettingsStore(), externalPluginsRoot: pluginsRoot);
            Assert.Equal(PluginLifecycleState.Registered, Assert.Single(enabledHost.GetRecords(), record => record.Descriptor.Id == id).State);
            await ui.Dispatch(() =>
            {
                var window = new PluginManagerWindow(enabledHost, themeManager: null, installer: installer);
                window.FindControl<TabControl>("ManagerTabs")!.SelectedIndex = 2;
                window.FindControl<Button>("UninstallPackageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var confirm = window.FindControl<Button>("ConfirmUninstallPackageButton")!;
                Assert.True(confirm.IsVisible);
                Assert.Single(installer.GetPackages());
                ClickAndWait(window, confirm, "Uninstall scheduled", token);
                Assert.Empty(window.FindControl<ListBox>("PackageList")!.Items);
            }, token);

            Assert.True(enabledHost.TryGetSource(SmokeExternalPlugin.ExternalSourceId, out _));
            using var afterRemoval = DesktopPluginHostFactory.CreateForLibrary(database, externalPluginsRoot: pluginsRoot);
            Assert.DoesNotContain(afterRemoval.GetRecords(), record => record.Descriptor.Id == id);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Package_preflight_checks_builtin_source_capabilities_not_just_descriptors()
    {
        using var host = DesktopPluginHostFactory.CreateDefault();
        var descriptor = new PluginDescriptor("test.epic-conflict", StoreCatalog.EpicPluginId, "Epic conflict",
            new Version(1, 0), PluginLoadKind.ExternalAssembly, PluginSurface.Desktop,
            PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch, new PluginEntryPoints(Desktop: "Plugin.dll"));

        var failure = Assert.Throws<InvalidDataException>(() => host.ValidatePackageSourceOwnership(descriptor, replacingId: null));
        Assert.Contains(HeroicPlugin.PluginId, failure.Message);
    }

    private static void ClickAndWait(PluginManagerWindow window, Button button, string expected, CancellationToken token)
    {
        var status = window.FindControl<TextBlock>("PackageStatus")!;
        var frame = new DispatcherFrame();
        status.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBlock.TextProperty && status.Text?.StartsWith(expected, StringComparison.Ordinal) == true)
            {
                frame.Continue = false;
            }
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var registration = deadline.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (frame.Continue)
        {
            Dispatcher.UIThread.PushFrame(frame);
        }
        Assert.StartsWith(expected, status.Text);
    }
}
