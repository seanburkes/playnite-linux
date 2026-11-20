namespace Playnite.Core.Hosting;

/// <summary>
/// The three host-facing surfaces and the startup diagnostics for every missing host.
/// Creation never throws because a host is missing; degraded calls report Unavailable.
/// </summary>
public sealed class HostApiSet
{
    internal HostApiSet(
        INotificationsAPI notifications,
        IDialogsFactory dialogs,
        IMainViewAPI mainView,
        IReadOnlyList<string> diagnostics)
    {
        Notifications = notifications;
        Dialogs = dialogs;
        MainView = mainView;
        Diagnostics = diagnostics;
    }

    /// <summary>Notification publish, deduplicate and dismiss surface.</summary>
    public INotificationsAPI Notifications { get; }

    /// <summary>Asynchronous modal-dialog surface.</summary>
    public IDialogsFactory Dialogs { get; }

    /// <summary>Main-view query surface.</summary>
    public IMainViewAPI MainView { get; }

    /// <summary>One diagnostic per missing host; empty when all three are registered.</summary>
    public IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>Builds the host-facing API set from optional host seams.</summary>
public static class HostApis
{
    /// <summary>Creates every surface, recording a clear diagnostic for each missing host.</summary>
    public static HostApiSet Create(
        INotificationHost? notificationHost = null,
        IDialogHost? dialogHost = null,
        IMainViewHost? mainViewHost = null)
    {
        var diagnostics = new List<string>(3);
        if (notificationHost is null)
        {
            diagnostics.Add(NotificationsApi.MissingHostDiagnostic);
        }

        if (dialogHost is null)
        {
            diagnostics.Add(DialogsFactory.MissingHostDiagnostic);
        }

        if (mainViewHost is null)
        {
            diagnostics.Add(MainViewApi.MissingHostDiagnostic);
        }

        return new HostApiSet(
            new NotificationsApi(notificationHost),
            new DialogsFactory(dialogHost),
            new MainViewApi(mainViewHost),
            diagnostics.ToArray());
    }
}
