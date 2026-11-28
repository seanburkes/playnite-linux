using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Playnite.Core.Addons;

namespace Playnite.Desktop.Shell;

public sealed partial class PluginManagerWindow
{
    private AddonInstaller? installer;
    private string? pendingUninstall;
    private CancellationTokenSource? packageOperation;

    private ListBox packageList => this.FindControl<ListBox>("PackageList")!;
    private TextBlock packageStatus => this.FindControl<TextBlock>("PackageStatus")!;
    private ManagedAddonPackage? SelectedPackage => packageList.SelectedItem as ManagedAddonPackage;

    private void InitializePackages(AddonInstaller? packageInstaller)
    {
        if (packageInstaller is null || host is null)
        {
            return;
        }

        installer = packageInstaller;
        this.FindControl<TabItem>("PackagesTab")!.IsVisible = true;
        packageList.SelectionChanged += (_, _) =>
        {
            ClearPendingUninstall();
            RefreshPackageButtons();
        };
        this.FindControl<Button>("InstallPackageButton")!.Click += async (_, _) => await PickPackageAsync(folder: false, updateId: null);
        this.FindControl<Button>("InstallFolderButton")!.Click += async (_, _) => await PickPackageAsync(folder: true, updateId: null);
        this.FindControl<Button>("UpdatePackageButton")!.Click += async (_, _) =>
        {
            if (SelectedPackage is { } package)
            {
                await PickPackageAsync(folder: false, package.Id);
            }
        };
        this.FindControl<Button>("UpdateFolderButton")!.Click += async (_, _) =>
        {
            if (SelectedPackage is { } package)
            {
                await PickPackageAsync(folder: true, package.Id);
            }
        };
        this.FindControl<Button>("RollbackPackageButton")!.Click += async (_, _) =>
        {
            if (SelectedPackage is { } package)
            {
                await RunPackageOperationAsync(async token =>
                {
                    var restored = await installer.RollBackPackageAsync(package.Id, host, token,
                        descriptor => host.ValidatePackageSourceOwnership(descriptor, package.Id));
                    return (restored.Id, $"Restored '{restored.Id}' to v{restored.Version}.");
                });
            }
        };
        this.FindControl<Button>("UninstallPackageButton")!.Click += (_, _) =>
        {
            if (SelectedPackage is { } package)
            {
                pendingUninstall = package.Id;
                packageStatus.Text = $"Remove '{package.Id}' and its rollback version?";
                RefreshPackageButtons();
            }
        };
        this.FindControl<Button>("ConfirmUninstallPackageButton")!.Click += async (_, _) =>
        {
            if (pendingUninstall is { } id)
            {
                await RunPackageOperationAsync(async token =>
                {
                    await installer.UninstallPackageAsync(id, token);
                    return (id, $"Uninstall scheduled for '{id}'.");
                });
            }
        };
        this.FindControl<Button>("CancelUninstallPackageButton")!.Click += (_, _) =>
        {
            ClearPendingUninstall();
            RefreshPackageButtons();
        };
        Closed += (_, _) => packageOperation?.Cancel();
        RefreshPackages(preferredId: null);
    }

    private Task PickPackageAsync(bool folder, string? updateId) =>
        RunPackageOperationAsync(async cancellationToken =>
        {
            var title = updateId is null ? "Install add-on" : $"Update {updateId}";
            IStorageItem? item = folder
                ? (await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title })).FirstOrDefault()
                : (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    FileTypeFilter = [new FilePickerFileType("Add-on packages") { Patterns = ["*.zip", "*.pext"] }],
                })).FirstOrDefault();
            if (item is null)
            {
                return (null, null);
            }

            using (item)
            {
                var path = item.TryGetLocalPath() ?? throw new IOException("The selected package must be a local file or folder.");
                var installed = await installer!.InstallPackageAsync(path, host!, updateId, cancellationToken,
                    descriptor => host!.ValidatePackageSourceOwnership(descriptor, updateId));
                var action = updateId is null ? "Installed" : "Updated";
                return (installed.Id, $"{action} '{installed.Id}' v{installed.Version}.");
            }
        });

    private async Task RunPackageOperationAsync(
        Func<CancellationToken, Task<(string? Id, string? Message)>> operation)
    {
        if (packageOperation is not null || host is not { IsDisposed: false })
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        packageOperation = cancellation;
        ClearPendingUninstall();
        RefreshPackageButtons();
        try
        {
            var (id, message) = await operation(cancellation.Token);
            if (!cancellation.IsCancellationRequested && message is not null)
            {
                packageStatus.Text = $"{message} Restart Playnite to apply the change.";
                RefreshPackages(id);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"addon-package: {exception.Message}");
            if (!cancellation.IsCancellationRequested)
            {
                packageStatus.Text = exception.Message;
                RefreshPackages(SelectedPackage?.Id);
            }
        }
        finally
        {
            packageOperation = null;
            if (!cancellation.IsCancellationRequested)
            {
                RefreshPackageButtons();
            }
        }
    }

    private void RefreshPackages(string? preferredId)
    {
        var diagnostics = this.FindControl<TextBlock>("PackageDiagnostics")!;
        try
        {
            var warnings = new List<string>();
            var packages = installer!.GetPackages(
                (id, error) => warnings.Add($"Could not read package '{id}': {error}"));
            diagnostics.Text = string.Join(Environment.NewLine, warnings);
            packageList.ItemsSource = packages;
            packageList.SelectedItem = packages.FirstOrDefault(package => package.Id == preferredId)
                ?? packages.FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"addon-package: {exception.Message}");
            diagnostics.Text = $"Could not list packages: {exception.Message}";
            packageList.ItemsSource = null;
        }
        RefreshPackageButtons();
    }

    private void RefreshPackageButtons()
    {
        var selected = SelectedPackage;
        var available = packageOperation is null && host is { IsDisposed: false };
        packageList.IsEnabled = available;
        this.FindControl<WrapPanel>("PackageActions")!.IsEnabled = available;
        this.FindControl<Button>("UpdatePackageButton")!.IsEnabled = selected is not null;
        this.FindControl<Button>("UpdateFolderButton")!.IsEnabled = selected is not null;
        this.FindControl<Button>("RollbackPackageButton")!.IsEnabled = selected?.PreviousVersion is not null;
        this.FindControl<Button>("UninstallPackageButton")!.IsEnabled = selected is not null && pendingUninstall is null;
        this.FindControl<Button>("ConfirmUninstallPackageButton")!.IsVisible = pendingUninstall is not null;
        this.FindControl<Button>("CancelUninstallPackageButton")!.IsVisible = pendingUninstall is not null;
    }

    private void ClearPendingUninstall()
    {
        if (pendingUninstall is not null)
        {
            pendingUninstall = null;
            packageStatus.Text = string.Empty;
        }
    }
}
