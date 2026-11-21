using Playnite.Desktop.Bootstrap;
using Playnite.Desktop.Shell;
using Xunit;

namespace Playnite.Desktop.Tests;

/// <summary>p7-002 shell seams: each test states one shell behaviour without a window.</summary>
public sealed class ShellSeamsTests
{
    private static readonly LocalizationKeySet Controlled = new([
        new KeyValuePair<string, string>(LOC.AboutWindowTitle, "About From Controlled Catalog"),
        new KeyValuePair<string, string>(LOC.AboutAuthor, "Author From Controlled Catalog")
    ]);

    [Fact]
    public void About_title_and_message_resolve_from_a_controlled_catalog()
    {
        var localization = new BootstrapLocalization(Controlled);
        var viewModel = new ShellViewModel(localization, new FakeDialogService());

        Assert.Equal("About From Controlled Catalog", viewModel.AboutTitle);
        Assert.Equal("Author From Controlled Catalog", viewModel.AboutMessage);
        Assert.Equal("embedded", localization.Source);
    }

    [Fact]
    public void Unknown_about_keys_keep_the_v10_fallback()
    {
        var localization = new BootstrapLocalization(new LocalizationKeySet([]));
        var viewModel = new ShellViewModel(localization, new FakeDialogService());

        Assert.Equal("About Playnite", viewModel.AboutTitle);
        Assert.Equal("Made by Josef Němec", viewModel.AboutMessage);
    }

    [Fact]
    public void About_command_routes_resolved_text_through_the_dialog_service()
    {
        var dialogs = new FakeDialogService();
        var viewModel = new ShellViewModel(new BootstrapLocalization(Controlled), dialogs);

        viewModel.AboutCommand.Execute(null);

        Assert.Equal(("About From Controlled Catalog", "Author From Controlled Catalog"), dialogs.About);
        Assert.Null(dialogs.MessageBox);
    }

    [Fact]
    public void Message_box_command_uses_the_dialog_service_only()
    {
        var dialogs = new FakeDialogService();
        var viewModel = new ShellViewModel(new BootstrapLocalization(Controlled), dialogs);

        viewModel.MessageBoxCommand.Execute(null);

        Assert.Equal(("About From Controlled Catalog", "Author From Controlled Catalog"), dialogs.MessageBox);
        Assert.Null(dialogs.About);
    }

    [Fact]
    public void Shell_view_model_constructor_has_no_service_locator_dependency()
    {
        var parameters = typeof(ShellViewModel).GetConstructors().Single().GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(BootstrapLocalization));
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(IDialogService));
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IServiceProvider));
    }

    private sealed class FakeDialogService : IDialogService
    {
        public (string Title, string Message)? About { get; private set; }

        public (string Title, string Message)? MessageBox { get; private set; }

        public void ShowAbout(string title, string message) => About = (title, message);

        public void ShowMessageBox(string title, string message) => MessageBox = (title, message);
    }
}
