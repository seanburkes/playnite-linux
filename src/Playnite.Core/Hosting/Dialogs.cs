namespace Playnite.Core.Hosting;

/// <summary>Buttons offered by a message dialog. Names follow the v10 SDK shape.</summary>
public enum MessageDialogButton
{
    /// <summary>A single OK button.</summary>
    Ok,

    /// <summary>OK and Cancel.</summary>
    OkCancel,

    /// <summary>Yes and No.</summary>
    YesNo,

    /// <summary>Yes, No and Cancel.</summary>
    YesNoCancel,
}

/// <summary>Icon shown by a message dialog. Names follow the v10 SDK shape.</summary>
public enum MessageDialogImage
{
    /// <summary>No icon.</summary>
    None,

    /// <summary>Error icon.</summary>
    Error,

    /// <summary>Warning icon.</summary>
    Warning,

    /// <summary>Information icon.</summary>
    Information,

    /// <summary>Question icon.</summary>
    Question,
}

/// <summary>The button the user chose, or None when no answer was possible.</summary>
public enum MessageDialogResult
{
    /// <summary>No answer; the request degraded or the host failed.</summary>
    None,

    /// <summary>OK.</summary>
    Ok,

    /// <summary>Cancel.</summary>
    Cancel,

    /// <summary>Yes.</summary>
    Yes,

    /// <summary>No.</summary>
    No,
}

/// <summary>A message dialog request.</summary>
public sealed record MessageDialogRequest
{
    /// <summary>Creates a validated request.</summary>
    public MessageDialogRequest(
        string text,
        string? caption = null,
        MessageDialogButton buttons = MessageDialogButton.Ok,
        MessageDialogImage image = MessageDialogImage.None)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(buttons))
        {
            throw new ArgumentOutOfRangeException(nameof(buttons), buttons, "Unknown message dialog button set.");
        }

        if (!Enum.IsDefined(image))
        {
            throw new ArgumentOutOfRangeException(nameof(image), image, "Unknown message dialog image.");
        }

        Text = text;
        Caption = caption;
        Buttons = buttons;
        Image = image;
    }

    /// <summary>Message body.</summary>
    public string Text { get; }

    /// <summary>Optional window caption.</summary>
    public string? Caption { get; }

    /// <summary>Offered buttons.</summary>
    public MessageDialogButton Buttons { get; }

    /// <summary>Icon.</summary>
    public MessageDialogImage Image { get; }
}

/// <summary>What kind of path a file dialog asks for.</summary>
public enum FileDialogKind
{
    /// <summary>Open one or more existing files.</summary>
    OpenFile,

    /// <summary>Choose a new or existing file to save.</summary>
    SaveFile,

    /// <summary>Choose one folder.</summary>
    SelectFolder,
}

/// <summary>A file or folder dialog request.</summary>
public sealed record FileDialogRequest
{
    /// <summary>Creates a validated request.</summary>
    public FileDialogRequest(
        FileDialogKind kind,
        string? title = null,
        string? filter = null,
        string? initialDirectory = null,
        bool allowMultiple = false,
        bool promptOverwrite = false)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown file dialog kind.");
        }

        if (kind == FileDialogKind.SelectFolder && allowMultiple)
        {
            throw new ArgumentException("A folder dialog selects one folder.", nameof(allowMultiple));
        }

        Kind = kind;
        Title = title;
        Filter = filter;
        InitialDirectory = initialDirectory;
        AllowMultiple = allowMultiple;
        PromptOverwrite = promptOverwrite;
    }

    /// <summary>Dialog kind.</summary>
    public FileDialogKind Kind { get; }

    /// <summary>Optional dialog title.</summary>
    public string? Title { get; }

    /// <summary>v10-style filter such as <c>ZIP Archive|*.zip</c>.</summary>
    public string? Filter { get; }

    /// <summary>Optional initial directory.</summary>
    public string? InitialDirectory { get; }

    /// <summary>Whether an open dialog may return several files.</summary>
    public bool AllowMultiple { get; }

    /// <summary>Whether a save dialog asks before overwriting.</summary>
    public bool PromptOverwrite { get; }
}

/// <summary>A text input dialog request.</summary>
public sealed record TextInputDialogRequest
{
    /// <summary>Creates a validated request.</summary>
    public TextInputDialogRequest(string text, string? caption = null, string? defaultInput = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Caption = caption;
        DefaultInput = defaultInput;
    }

    /// <summary>Prompt text.</summary>
    public string Text { get; }

    /// <summary>Optional window caption.</summary>
    public string? Caption { get; }

    /// <summary>Initial input value.</summary>
    public string? DefaultInput { get; }
}

/// <summary>The host's answer to a message dialog.</summary>
public sealed record MessageDialogResponse(
    HostOperationStatus Status,
    MessageDialogResult Result = MessageDialogResult.None,
    string? Diagnostic = null)
{
    /// <summary>True when the host answered the request.</summary>
    public bool IsCompleted => Status == HostOperationStatus.Completed;
}

/// <summary>The host's answer to a file or folder dialog.</summary>
public sealed record FileDialogResponse
{
    /// <summary>Creates a response, copying the selected paths.</summary>
    public FileDialogResponse(
        HostOperationStatus status,
        IEnumerable<string>? paths = null,
        string? diagnostic = null)
    {
        Status = status;
        Paths = (paths ?? Array.Empty<string>()).ToArray();
        Diagnostic = diagnostic;
    }

    /// <summary>Host outcome.</summary>
    public HostOperationStatus Status { get; }

    /// <summary>Selected paths in the order the host returned them; empty when cancelled or unavailable.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Diagnostic for an unavailable or failed request.</summary>
    public string? Diagnostic { get; }

    /// <summary>True when the host answered the request.</summary>
    public bool IsCompleted => Status == HostOperationStatus.Completed;

    /// <summary>True when at least one path was selected.</summary>
    public bool IsAccepted => IsCompleted && Paths.Count > 0;

    /// <summary>The first selected path, or null.</summary>
    public string? Path => Paths.Count > 0 ? Paths[0] : null;
}

/// <summary>The host's answer to a text input dialog.</summary>
public sealed record TextInputDialogResponse(
    HostOperationStatus Status,
    bool Accepted = false,
    string? Text = null,
    string? Diagnostic = null)
{
    /// <summary>True when the host answered the request.</summary>
    public bool IsCompleted => Status == HostOperationStatus.Completed;
}

/// <summary>
/// The modal-dialog seam supplied by the UI. Implementations show the request on the UI thread
/// and complete the task with the user's answer; the service never blocks a caller thread.
/// </summary>
public interface IDialogHost
{
    /// <summary>Shows a message dialog and waits for the answer.</summary>
    Task<MessageDialogResponse> ShowMessageAsync(MessageDialogRequest request, CancellationToken cancellationToken = default);

    /// <summary>Shows a file or folder dialog and waits for the answer.</summary>
    Task<FileDialogResponse> SelectFileAsync(FileDialogRequest request, CancellationToken cancellationToken = default);

    /// <summary>Shows a text input dialog and waits for the answer.</summary>
    Task<TextInputDialogResponse> SelectStringAsync(TextInputDialogRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The v10-named dialog surface, made asynchronous. Every request is a typed request/response
/// pair over an injectable host; a missing host degrades with a diagnostic and never blocks.
/// </summary>
public interface IDialogsFactory
{
    /// <summary>Shows a message dialog.</summary>
    Task<MessageDialogResponse> ShowMessageAsync(MessageDialogRequest request, CancellationToken cancellationToken = default);

    /// <summary>Shows an error dialog.</summary>
    Task<MessageDialogResponse> ShowErrorAsync(string text, string? caption = null, CancellationToken cancellationToken = default);

    /// <summary>Shows an open/save/folder dialog from a typed request.</summary>
    Task<FileDialogResponse> SelectFileAsync(FileDialogRequest request, CancellationToken cancellationToken = default);

    /// <summary>Shows a folder picker.</summary>
    Task<FileDialogResponse> SelectFolderAsync(string? title = null, string? initialDirectory = null, CancellationToken cancellationToken = default);

    /// <summary>Shows a save dialog.</summary>
    Task<FileDialogResponse> SaveFileAsync(
        string? title = null,
        string? filter = null,
        string? initialDirectory = null,
        bool promptOverwrite = false,
        CancellationToken cancellationToken = default);

    /// <summary>Shows a text input dialog.</summary>
    Task<TextInputDialogResponse> SelectStringAsync(TextInputDialogRequest request, CancellationToken cancellationToken = default);
}

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
