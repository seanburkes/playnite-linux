using Playnite.SDK;

namespace Playnite.Core.Hosting;

/// <summary>Delegates every dialog request to an optional host.</summary>
public sealed class DialogsFactory : IDialogsFactory
{
    /// <summary>Diagnostic recorded when no host was registered.</summary>
    public const string MissingHostDiagnostic =
        "No dialog host is registered; the dialog request was not shown.";

    private readonly IDialogHost? host;

    /// <summary>Creates the factory over an optional host.</summary>
    public DialogsFactory(IDialogHost? host = null) => this.host = host;

    /// <inheritdoc />
    public async Task<MessageDialogResponse> ShowMessageAsync(
        MessageDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (host is null)
        {
            return new MessageDialogResponse(HostOperationStatus.Unavailable, MessageDialogResult.None, MissingHostDiagnostic);
        }

        try
        {
            var response = await host.ShowMessageAsync(request, cancellationToken).ConfigureAwait(false);
            return response ?? new MessageDialogResponse(
                HostOperationStatus.Failed,
                MessageDialogResult.None,
                "The dialog host returned no response.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MessageDialogResponse(
                HostOperationStatus.Failed,
                MessageDialogResult.None,
                "The dialog host failed: " + exception.Message);
        }
    }

    /// <inheritdoc />
    public Task<MessageDialogResponse> ShowErrorAsync(
        string text,
        string? caption = null,
        CancellationToken cancellationToken = default)
        => ShowMessageAsync(
            new MessageDialogRequest(text, caption, MessageDialogButton.Ok, MessageDialogImage.Error),
            cancellationToken);

    /// <inheritdoc />
    public async Task<FileDialogResponse> SelectFileAsync(
        FileDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (host is null)
        {
            return new FileDialogResponse(HostOperationStatus.Unavailable, diagnostic: MissingHostDiagnostic);
        }

        try
        {
            var response = await host.SelectFileAsync(request, cancellationToken).ConfigureAwait(false);
            return response ?? new FileDialogResponse(
                HostOperationStatus.Failed,
                diagnostic: "The dialog host returned no response.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new FileDialogResponse(
                HostOperationStatus.Failed,
                diagnostic: "The dialog host failed: " + exception.Message);
        }
    }

    /// <inheritdoc />
    public Task<FileDialogResponse> SelectFolderAsync(
        string? title = null,
        string? initialDirectory = null,
        CancellationToken cancellationToken = default)
        => SelectFileAsync(
            new FileDialogRequest(FileDialogKind.SelectFolder, title, initialDirectory: initialDirectory),
            cancellationToken);

    /// <inheritdoc />
    public Task<FileDialogResponse> SaveFileAsync(
        string? title = null,
        string? filter = null,
        string? initialDirectory = null,
        bool promptOverwrite = false,
        CancellationToken cancellationToken = default)
        => SelectFileAsync(
            new FileDialogRequest(
                FileDialogKind.SaveFile,
                title,
                filter,
                initialDirectory,
                promptOverwrite: promptOverwrite),
            cancellationToken);

    /// <inheritdoc />
    public async Task<TextInputDialogResponse> SelectStringAsync(
        TextInputDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (host is null)
        {
            return new TextInputDialogResponse(HostOperationStatus.Unavailable, Diagnostic: MissingHostDiagnostic);
        }

        try
        {
            var response = await host.SelectStringAsync(request, cancellationToken).ConfigureAwait(false);
            return response ?? new TextInputDialogResponse(
                HostOperationStatus.Failed,
                Diagnostic: "The dialog host returned no response.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new TextInputDialogResponse(
                HostOperationStatus.Failed,
                Diagnostic: "The dialog host failed: " + exception.Message);
        }
    }
}
