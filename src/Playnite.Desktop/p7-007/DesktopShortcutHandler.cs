using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Playnite.Desktop.Integration;

/// <summary>In-app desktop keyboard shortcut actions (global hotkeys are out of v1 scope).</summary>
public enum DesktopShortcutAction
{
    None = 0,
    OpenSettings,
    OpenAbout,
    FocusSearch,
    Shutdown,
}

/// <summary>Callbacks invoked when an in-app desktop shortcut fires.</summary>
public sealed class DesktopShortcutCallbacks
{
    public Action? OpenSettings { get; init; }

    public Action? OpenAbout { get; init; }

    public Action? FocusSearch { get; init; }

    public Action? Shutdown { get; init; }
}

/// <summary>Maps v10 in-app keyboard gestures to desktop shell actions.</summary>
public static class DesktopShortcutHandler
{
    public static DesktopShortcutAction Resolve(Key key, KeyModifiers modifiers)
    {
        return (key, modifiers) switch
        {
            (Key.F4, KeyModifiers.None) => DesktopShortcutAction.OpenSettings,
            (Key.F1, KeyModifiers.None) => DesktopShortcutAction.OpenAbout,
            (Key.F, KeyModifiers.Control) => DesktopShortcutAction.FocusSearch,
            (Key.Q, KeyModifiers.Alt) => DesktopShortcutAction.Shutdown,
            _ => DesktopShortcutAction.None,
        };
    }

    public static IDisposable Attach(Window window, DesktopShortcutCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(callbacks);

        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled)
            {
                return;
            }

            var action = Resolve(e.Key, e.KeyModifiers);
            var callback = action switch
            {
                DesktopShortcutAction.OpenSettings => callbacks.OpenSettings,
                DesktopShortcutAction.OpenAbout => callbacks.OpenAbout,
                DesktopShortcutAction.FocusSearch => callbacks.FocusSearch,
                DesktopShortcutAction.Shutdown => callbacks.Shutdown,
                _ => null,
            };

            if (callback is null)
            {
                return;
            }

            e.Handled = true;
            callback();
        }

        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        return new Subscription(() => window.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown));
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
