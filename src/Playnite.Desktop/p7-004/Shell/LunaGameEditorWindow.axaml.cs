using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Core.Models;

namespace Playnite.Desktop.Shell;

internal sealed record LunaGameInput(string Name, string Url);

public sealed partial class LunaGameEditorWindow : Window
{
    public LunaGameEditorWindow() : this(null)
    {
    }

    internal LunaGameEditorWindow(Game? game)
    {
        AvaloniaXamlLoader.Load(this);
        Title = game is null ? "Add Amazon Luna Game" : "Edit Amazon Luna Game";
        this.FindControl<TextBox>("GameNameBox")!.Text = game is null ? "Fortnite" : game.Name;
        this.FindControl<TextBox>("GameUrlBox")!.Text = game is null
            ? LunaPlayRequest.FortniteUrl
            : LunaPlayRequest.FindPlayAction(game)?.Path;
    }

    private void OnCancel(object? sender, RoutedEventArgs args) => Close(null);

    private void OnSave(object? sender, RoutedEventArgs args)
    {
        var name = this.FindControl<TextBox>("GameNameBox")!.Text?.Trim();
        var url = LunaPlayRequest.ParseGameUrl(this.FindControl<TextBox>("GameUrlBox")!.Text);
        if (string.IsNullOrEmpty(name) || url is null)
        {
            var error = this.FindControl<TextBlock>("ValidationErrorText")!;
            error.Text = string.IsNullOrEmpty(name) ? "Enter a game name." : LunaPlayRequest.InvalidUrlMessage;
            error.IsVisible = true;
            return;
        }

        Close(new LunaGameInput(name, url.AbsoluteUri));
    }
}
