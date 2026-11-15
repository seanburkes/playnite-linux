using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UiShellSpike;

public sealed record Game(string Title, string Playtime, string LastPlayed, bool Installed, int Motif);

public sealed class LibraryModel : INotifyPropertyChanged
{
    public static IReadOnlyList<Game> Catalog { get; } = [
        new("Amber Atlas", "12 h 30 min", "Last played: 2026-10-12", true, 0),
        new("Cloud Orchard", "3 h 15 min", "Last played: 2026-10-08", false, 1),
        new("Lantern Tides", "28 h 05 min", "Last played: 2026-09-21", true, 2),
        new("Velvet Comet", "45 min", "Last played: 2026-08-04", true, 3),
        new("Copper Grove", "Not played", "Last played: never", false, 4),
        new("Paper Aurora", "7 h 10 min", "Last played: 2026-10-01", true, 5)
    ];
    public ObservableCollection<Game> Games { get; } = new(Catalog);
    private string search = "";
    private bool installedOnly;
    private Game? selectedGame = Catalog[0];
    public string Search { get => search; set { search = value; Changed(); Filter(); } }
    public bool InstalledOnly { get => installedOnly; set { installedOnly = value; Changed(); Filter(); } }
    public Game? SelectedGame { get => selectedGame; set { selectedGame = value; Changed(); } }
    public string CountLabel => $"{Games.Count} invented games";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Filter()
    {
        var previous = SelectedGame;
        var matches = Catalog.Where(g => g.Title.Contains(Search, StringComparison.OrdinalIgnoreCase)
            && (!InstalledOnly || g.Installed)).ToArray();
        Games.Clear();
        foreach (var game in matches) Games.Add(game);
        SelectedGame = previous is not null && matches.Contains(previous) ? previous : matches.FirstOrDefault();
        Changed(nameof(CountLabel));
    }
}
