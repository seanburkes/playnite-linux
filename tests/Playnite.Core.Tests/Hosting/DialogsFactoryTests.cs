using Playnite.Core.Hosting;
using Xunit;

namespace Playnite.Core.Tests.Hosting;

public sealed class DialogsFactoryTests
{
    [Fact]
    public async Task ShowMessageDelegatesAndReturnsTheAnswer()
    {
        var host = new FakeDialogHost
        {
            MessageHandler = (_, _) => Task.FromResult(
                new MessageDialogResponse(HostOperationStatus.Completed, MessageDialogResult.Yes)),
        };
        var dialogs = new DialogsFactory(host);
        var request = new MessageDialogRequest("Continue?", "Playnite", MessageDialogButton.YesNo, MessageDialogImage.Question);

        var response = await dialogs.ShowMessageAsync(request);

        Assert.True(response.IsCompleted);
        Assert.Equal(MessageDialogResult.Yes, response.Result);
        Assert.Single(host.Messages);
        Assert.Same(request, host.Messages[0]);
    }

    [Fact]
    public async Task ShowErrorBuildsAnErrorRequest()
    {
        var host = new FakeDialogHost();
        var dialogs = new DialogsFactory(host);

        await dialogs.ShowErrorAsync("Import failed", "Import");

        var request = Assert.Single(host.Messages);
        Assert.Equal("Import failed", request.Text);
        Assert.Equal("Import", request.Caption);
        Assert.Equal(MessageDialogButton.Ok, request.Buttons);
        Assert.Equal(MessageDialogImage.Error, request.Image);
    }

    [Fact]
    public async Task MissingHostDegradesEveryDialogWithADiagnostic()
    {
        var dialogs = new DialogsFactory();

        var message = await dialogs.ShowMessageAsync(new MessageDialogRequest("Text"));
        var file = await dialogs.SelectFileAsync(new FileDialogRequest(FileDialogKind.OpenFile));
        var text = await dialogs.SelectStringAsync(new TextInputDialogRequest("Name?"));

        Assert.Equal(HostOperationStatus.Unavailable, message.Status);
        Assert.Equal(MessageDialogResult.None, message.Result);
        Assert.Equal(DialogsFactory.MissingHostDiagnostic, message.Diagnostic);
        Assert.Equal(HostOperationStatus.Unavailable, file.Status);
        Assert.Empty(file.Paths);
        Assert.False(file.IsAccepted);
        Assert.Equal(HostOperationStatus.Unavailable, text.Status);
        Assert.Null(text.Text);
    }

    [Fact]
    public async Task AHostFailureIsReportedRatherThanThrown()
    {
        var host = new FakeDialogHost
        {
            MessageHandler = (_, _) => throw new InvalidOperationException("window failed"),
        };
        var dialogs = new DialogsFactory(host);

        var response = await dialogs.ShowMessageAsync(new MessageDialogRequest("Text"));

        Assert.Equal(HostOperationStatus.Failed, response.Status);
        Assert.Contains("window failed", response.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectFileDelegatesAndPreservesEverySelectedPath()
    {
        var host = new FakeDialogHost
        {
            FileHandler = (_, _) => Task.FromResult(
                new FileDialogResponse(HostOperationStatus.Completed, ["/games/a.zip", "/games/b.zip"])),
        };
        var dialogs = new DialogsFactory(host);
        var request = new FileDialogRequest(
            FileDialogKind.OpenFile,
            "Choose archives",
            "ZIP Archive|*.zip",
            "/games",
            allowMultiple: true);

        var response = await dialogs.SelectFileAsync(request);

        Assert.True(response.IsAccepted);
        Assert.Equal(2, response.Paths.Count);
        Assert.Equal("/games/a.zip", response.Path);
        var sent = Assert.Single(host.Files);
        Assert.Equal(FileDialogKind.OpenFile, sent.Kind);
        Assert.True(sent.AllowMultiple);
    }

    [Fact]
    public async Task SelectFolderBuildsAFolderRequest()
    {
        var host = new FakeDialogHost();
        var dialogs = new DialogsFactory(host);

        await dialogs.SelectFolderAsync("Choose folder", "/games");

        var request = Assert.Single(host.Files);
        Assert.Equal(FileDialogKind.SelectFolder, request.Kind);
        Assert.Equal("Choose folder", request.Title);
        Assert.Equal("/games", request.InitialDirectory);
        Assert.False(request.AllowMultiple);
    }

    [Fact]
    public async Task SaveFileBuildsASaveRequest()
    {
        var host = new FakeDialogHost();
        var dialogs = new DialogsFactory(host);

        await dialogs.SaveFileAsync("Save archive", "ZIP Archive|*.zip", "/games", promptOverwrite: true);

        var request = Assert.Single(host.Files);
        Assert.Equal(FileDialogKind.SaveFile, request.Kind);
        Assert.Equal("ZIP Archive|*.zip", request.Filter);
        Assert.True(request.PromptOverwrite);
    }

    [Fact]
    public async Task SelectStringReturnsAcceptedText()
    {
        var host = new FakeDialogHost
        {
            StringHandler = (_, _) => Task.FromResult(
                new TextInputDialogResponse(HostOperationStatus.Completed, true, "Portal 2")),
        };
        var dialogs = new DialogsFactory(host);

        var response = await dialogs.SelectStringAsync(new TextInputDialogRequest("Game name", "Add game", "Portal"));

        Assert.True(response.IsCompleted);
        Assert.True(response.Accepted);
        Assert.Equal("Portal 2", response.Text);
    }

    [Fact]
    public async Task ANullHostResponseIsReportedAsFailed()
    {
        var host = new FakeDialogHost
        {
            MessageHandler = (_, _) => Task.FromResult<MessageDialogResponse>(null!),
            FileHandler = (_, _) => Task.FromResult<FileDialogResponse>(null!),
            StringHandler = (_, _) => Task.FromResult<TextInputDialogResponse>(null!),
        };
        var dialogs = new DialogsFactory(host);

        var message = await dialogs.ShowMessageAsync(new MessageDialogRequest("Text"));
        var file = await dialogs.SelectFileAsync(new FileDialogRequest(FileDialogKind.OpenFile));
        var text = await dialogs.SelectStringAsync(new TextInputDialogRequest("Text"));

        Assert.Equal(HostOperationStatus.Failed, message.Status);
        Assert.Equal(HostOperationStatus.Failed, file.Status);
        Assert.Equal(HostOperationStatus.Failed, text.Status);
    }

    [Fact]
    public async Task CancellationBeforeTheHostRunsThrowsWithoutCallingIt()
    {
        var host = new FakeDialogHost();
        var dialogs = new DialogsFactory(host);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dialogs.ShowMessageAsync(new MessageDialogRequest("Text"), source.Token));

        Assert.Empty(host.Messages);
    }

    [Fact]
    public async Task CancellationDuringTheHostCallThrows()
    {
        using var source = new CancellationTokenSource();
        var host = new FakeDialogHost
        {
            MessageHandler = async (_, token) =>
            {
                source.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new MessageDialogResponse(HostOperationStatus.Completed);
            },
        };
        var dialogs = new DialogsFactory(host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dialogs.ShowMessageAsync(new MessageDialogRequest("Text"), source.Token));
    }

    [Fact]
    public async Task InvalidRequestsAreRejectedExplicitly()
    {
        var dialogs = new DialogsFactory();

        await Assert.ThrowsAsync<ArgumentNullException>(() => dialogs.ShowMessageAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => dialogs.SelectFileAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => dialogs.SelectStringAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => dialogs.ShowErrorAsync(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MessageDialogRequest("Text", buttons: (MessageDialogButton)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FileDialogRequest((FileDialogKind)99));
        Assert.Throws<ArgumentException>(
            () => new FileDialogRequest(FileDialogKind.SelectFolder, allowMultiple: true));
    }
}
