using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Playnite.Platform.DesktopIntegration;

namespace Playnite.Desktop.Integration;

/// <summary>
/// Avalonia UI presenter for <see cref="ITrayService"/>. Platform host probing remains in
/// <c>Playnite.Platform.Linux</c>; this class owns only the <see cref="TrayIcon"/> lifecycle.
/// </summary>
public sealed class AvaloniaTrayIconPresenter : ITrayIconPresenter
{
    private readonly Application application;
    private readonly Action<string>? onMenuItemClicked;
    private readonly Action? onTrayClicked;

    public AvaloniaTrayIconPresenter(
        Application application,
        Action<string>? onMenuItemClicked = null,
        Action? onTrayClicked = null)
    {
        this.application = application ?? throw new ArgumentNullException(nameof(application));
        this.onMenuItemClicked = onMenuItemClicked;
        this.onTrayClicked = onTrayClicked;
    }

    public async ValueTask<IAsyncDisposable> PresentAsync(TrayModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();

        if (Dispatcher.UIThread.CheckAccess())
        {
            return CreateAndAttach(model);
        }

        return await Dispatcher.UIThread
            .InvokeAsync(() => CreateAndAttach(model), DispatcherPriority.Normal, cancellationToken)
            .GetTask()
            .ConfigureAwait(false);
    }

    private TrayIconHandle CreateAndAttach(TrayModel model)
    {
        var menu = new NativeMenu();
        foreach (var item in model.Menu)
        {
            var menuItem = new NativeMenuItem(item.Label)
            {
                IsEnabled = item.Enabled,
            };
            var id = item.Id;
            menuItem.Click += (_, _) => onMenuItemClicked?.Invoke(id);
            menu.Items.Add(menuItem);
        }

        var icon = new TrayIcon
        {
            ToolTipText = model.Title,
            Menu = menu,
            IsVisible = true,
        };
        icon.Clicked += (_, _) => onTrayClicked?.Invoke();
        TrayIcon.SetIcons(application, [icon]);
        return new TrayIconHandle(application, icon);
    }

    private sealed class TrayIconHandle(Application application, TrayIcon icon) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Detach();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(Detach).GetTask().ConfigureAwait(false);
        }

        private void Detach()
        {
            icon.IsVisible = false;
            TrayIcon.SetIcons(application, []);
            icon.Dispose();
        }
    }
}
