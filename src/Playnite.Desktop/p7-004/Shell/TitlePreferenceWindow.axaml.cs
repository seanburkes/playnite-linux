using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Playnite.Core.Database;

namespace Playnite.Desktop.Shell;

internal sealed record TitlePreferenceState(Guid LibraryPreference, LibraryRow? Game, TitleProvenance? Title,
    IEnumerable<StoreTitleCandidate> LinkedTitles);

internal sealed record TitlePreferenceSelection(Guid? ProviderId, string? PersonalTitle, Guid? LinkSourceId, Guid? UnlinkProviderId = null);

public sealed partial class TitlePreferenceWindow : Window
{
    private sealed record Choice(Guid? ProviderId, string Label, Guid? LinkSourceId = null, Guid? UnlinkProviderId = null)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox sources;
    private readonly TextBox personalTitle;
    private readonly LibraryRow? game;
    private readonly Func<Guid, string, Task<IEnumerable<StoreTitleCandidate>>>? findCandidates;
    private Choice[] savedChoices = [];
    private string? initialTitle;

    public TitlePreferenceWindow()
    {
        AvaloniaXamlLoader.Load(this);
        sources = this.FindControl<ComboBox>("TitleSources")!;
        personalTitle = this.FindControl<TextBox>("PersonalTitle")!;
    }

    internal TitlePreferenceWindow(TitlePreferenceState state,
        Func<Guid, string, Task<IEnumerable<StoreTitleCandidate>>> findCandidates) : this()
    {
        game = state.Game;
        this.findCandidates = findCandidates;
        var explanation = this.FindControl<TextBlock>("TitleExplanation")!;
        Guid? selected;
        if (game is null)
        {
            explanation.Text = "Set the default title source. Store preferences use the owning store or a store entry you link to a game.";
            savedChoices =
            [
                new(Guid.Empty, "My title when available"),
                new(StoreCatalog.SteamPluginId, "Steam"),
                new(StoreCatalog.EpicPluginId, "Epic via Heroic"),
                new(StoreCatalog.GogPluginId, "GOG via Heroic"),
                new(StoreCatalog.AmazonPluginId, "Amazon via Heroic"),
            ];
            selected = state.LibraryPreference;
        }
        else if (state.Title is { } title)
        {
            explanation.Text = $"Choose the title to display for {game.Name}. Linking a title leaves both library entries and their launch actions in place.";
            var edited = !string.Equals(game.Name, title.AppliedTitle, StringComparison.Ordinal);
            initialTitle = edited ? game.Name : title.PersonalTitle;
            personalTitle.Text = initialTitle ?? game.Name;
            personalTitle.IsVisible = true;
            this.FindControl<StackPanel>("LinkedSearch")!.IsVisible = true;
            savedChoices =
            [
                new(null, "Use library preference"),
                new(Guid.Empty, "Use my title"),
                new(game.PluginId, $"{game.SourceDisplay} · {title.ProviderTitle}"),
                .. state.LinkedTitles.Select(candidate => ToChoice(candidate, shouldLink: false)),
                .. state.LinkedTitles.Select(candidate =>
                {
                    var provider = Guid.Parse(candidate.ProviderId);
                    return new Choice(null, $"Remove link · {StoreCatalog.GetDisplayName(provider)} · {candidate.ProviderTitle}",
                        UnlinkProviderId: provider);
                }),
            ];
            selected = edited ? Guid.Empty : title.Preference is null ? null : Guid.Parse(title.Preference);
        }
        else
        {
            explanation.Text = "Sync this game's source to see the title it provides.";
            sources.IsVisible = false;
            this.FindControl<Button>("SaveButton")!.IsEnabled = false;
            return;
        }

        sources.ItemsSource = savedChoices;
        sources.SelectedItem = savedChoices.FirstOrDefault(choice => choice.ProviderId == selected) ?? savedChoices[0];
    }

    private static Choice ToChoice(StoreTitleCandidate candidate, bool shouldLink)
    {
        var provider = Guid.Parse(candidate.ProviderId);
        return new Choice(provider, $"{StoreCatalog.GetDisplayName(provider)} · {candidate.ProviderTitle}" +
            (shouldLink ? $" (ID {candidate.StoreGameId}, link)" : ""), shouldLink ? Guid.Parse(candidate.SourceGameId) : null);
    }

    private async void OnFindTitles(object? sender, RoutedEventArgs args)
    {
        if (findCandidates is null || game is null)
        {
            return;
        }

        var search = this.FindControl<TextBox>("LinkSearch")!.Text?.Trim();
        var status = this.FindControl<TextBlock>("LinkStatus")!;
        if (string.IsNullOrEmpty(search))
        {
            status.Text = "Enter a title to search.";
            return;
        }

        var find = this.FindControl<Button>("FindTitles")!;
        find.IsEnabled = false;
        try
        {
            var results = (await findCandidates(game.Id, search).ConfigureAwait(true)).Select(candidate => ToChoice(candidate, shouldLink: true)).ToArray();
            var previous = sources.SelectedItem as Choice;
            sources.ItemsSource = savedChoices.Concat(results).ToArray();
            sources.SelectedItem = savedChoices.Contains(previous) ? previous : savedChoices[0];
            status.Text = results.Length == 0 ? "No matching store titles." : "Select a search result above and save to link it.";
        }
        catch (OperationCanceledException)
        {
            status.Text = "Title search canceled.";
        }
        catch (Exception ex)
        {
            status.Text = $"Could not search store titles ({ex.GetType().Name}).";
        }
        finally
        {
            find.IsEnabled = true;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs args)
    {
        if (sources.SelectedItem is Choice choice)
        {
            if (choice.UnlinkProviderId is Guid unlinkProviderId)
            {
                Close(new TitlePreferenceSelection(null, null, null, unlinkProviderId));
                return;
            }

            var text = personalTitle.Text?.Trim();
            if (choice.ProviderId == Guid.Empty && game is not null && string.IsNullOrWhiteSpace(text))
            {
                personalTitle.Focus();
                return;
            }

            var usePersonal = game is not null && (choice.ProviderId == Guid.Empty || text != (initialTitle ?? game.Name));
            Close(new TitlePreferenceSelection(choice.ProviderId, usePersonal ? text : null, choice.LinkSourceId));
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs args) => Close();
}
