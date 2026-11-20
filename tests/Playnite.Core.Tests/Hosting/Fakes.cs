using Playnite.Core.Hosting;
using Playnite.Platform.DesktopIntegration;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace Playnite.Core.Tests.Hosting;

internal sealed class FakeNotificationHost : INotificationHost
{
    public Func<NotificationMessage, CancellationToken, Task<NotificationHostResult>> PresentHandler { get; set; } =
        (_, _) => Task.FromResult(new NotificationHostResult(NotificationHostStatus.Presented));

    public Func<string, CancellationToken, Task<NotificationHostResult>> DismissHandler { get; set; } =
        (_, _) => Task.FromResult(new NotificationHostResult(NotificationHostStatus.Presented));

    public List<NotificationMessage> Presented { get; } = new();

    public List<string> Dismissed { get; } = new();

    public Task<NotificationHostResult> PresentAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        Presented.Add(message);
        return PresentHandler(message, cancellationToken);
    }

    public Task<NotificationHostResult> DismissAsync(string id, CancellationToken cancellationToken = default)
    {
        Dismissed.Add(id);
        return DismissHandler(id, cancellationToken);
    }
}

internal sealed class FakeDialogHost : IDialogHost
{
    public Func<MessageDialogRequest, CancellationToken, Task<MessageDialogResponse>> MessageHandler { get; set; } =
        (_, _) => Task.FromResult(new MessageDialogResponse(HostOperationStatus.Completed, MessageDialogResult.Ok));

    public Func<FileDialogRequest, CancellationToken, Task<FileDialogResponse>> FileHandler { get; set; } =
        (_, _) => Task.FromResult(new FileDialogResponse(HostOperationStatus.Completed));

    public Func<TextInputDialogRequest, CancellationToken, Task<TextInputDialogResponse>> StringHandler { get; set; } =
        (_, _) => Task.FromResult(new TextInputDialogResponse(HostOperationStatus.Completed));

    public List<MessageDialogRequest> Messages { get; } = new();

    public List<FileDialogRequest> Files { get; } = new();

    public List<TextInputDialogRequest> Strings { get; } = new();

    public Task<MessageDialogResponse> ShowMessageAsync(
        MessageDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        Messages.Add(request);
        return MessageHandler(request, cancellationToken);
    }

    public Task<FileDialogResponse> SelectFileAsync(
        FileDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        Files.Add(request);
        return FileHandler(request, cancellationToken);
    }

    public Task<TextInputDialogResponse> SelectStringAsync(
        TextInputDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        Strings.Add(request);
        return StringHandler(request, cancellationToken);
    }
}

internal sealed class FakeMainViewHost : IMainViewHost
{
    public Func<CancellationToken, Task<MainViewState>> StateHandler { get; set; } =
        _ => Task.FromResult(new MainViewState());

    public Func<CancellationToken, Task<IReadOnlyList<IGameView>>> SelectedHandler { get; set; } =
        _ => Task.FromResult<IReadOnlyList<IGameView>>(Array.Empty<IGameView>());

    public Func<CancellationToken, Task<IReadOnlyList<IGameView>>> FilteredHandler { get; set; } =
        _ => Task.FromResult<IReadOnlyList<IGameView>>(Array.Empty<IGameView>());

    public Task<MainViewState> GetStateAsync(CancellationToken cancellationToken = default)
        => StateHandler(cancellationToken);

    public Task<IReadOnlyList<IGameView>> GetSelectedGamesAsync(CancellationToken cancellationToken = default)
        => SelectedHandler(cancellationToken);

    public Task<IReadOnlyList<IGameView>> GetFilteredGamesAsync(CancellationToken cancellationToken = default)
        => FilteredHandler(cancellationToken);
}

internal sealed class FakeNotificationService : INotificationService
{
    public Func<NotificationRequest, CancellationToken, Task<NotificationDelivery>> Handler { get; set; } =
        (_, _) => Task.FromResult(new NotificationDelivery(NotificationDeliveryState.Delivered, "fake", "sent"));

    public List<NotificationRequest> Requests { get; } = new();

    public Task<NotificationDelivery> ShowAsync(
        NotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Handler(request, cancellationToken);
    }
}
